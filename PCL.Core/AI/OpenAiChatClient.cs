using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.AI;

/// <summary>
/// OpenAI 兼容 Chat Completions 客户端（支持流式输出与 function calling）。
/// </summary>
public sealed class OpenAiChatClient
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiChatClient(string endpoint, string apiKey, string model)
    {
        _endpoint = (endpoint ?? throw new ArgumentNullException(nameof(endpoint))).TrimEnd('/');
        _apiKey = apiKey ?? "";
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    private string ChatUrl => _endpoint + "/chat/completions";

    private HttpRequestMessage _BuildRequest(JsonObject body)
    {
        var request = HttpRequest.CreatePost(ChatUrl)
            .WithContent(new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!string.IsNullOrEmpty(_apiKey))
            request.WithBearerToken(_apiKey);
        return request;
    }

    private static JsonObject _BuildBody(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition>? tools, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = new JsonArray(messages.Select(_MessageToNode).ToArray())
        };
        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone()
                    }
                });
            body["tools"] = toolArray;
            body["tool_choice"] = "auto";
        }
        if (stream)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        return body;
    }

    private static JsonNode? _MessageToNode(AiChatMessage message)
    {
        var obj = new JsonObject { ["role"] = message.Role };
        switch (message.Role)
        {
            case "tool":
                obj["tool_call_id"] = message.ToolCallId ?? "";
                obj["content"] = message.Content ?? "";
                break;
            case "assistant" when message.ToolCalls is { Count: > 0 }:
                obj["content"] = message.Content;
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = _ValidArguments(call.ArgumentsJson)
                        }
                    });
                obj["tool_calls"] = calls;
                break;
            default:
                obj["content"] = message.Content ?? "";
                break;
        }
        return obj;
    }

    /// <summary>
    /// 回传给接口的工具参数必须是合法 JSON 对象：历史会话中可能残留无效参数（参数被截断或旧版本数据），
    /// 直接回传会导致接口报 400，故无效时回退为空对象。
    /// </summary>
    private static string _ValidArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";
        try
        {
            return JsonNode.Parse(json) is JsonObject ? json : "{}";
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>
    /// 非流式完成。
    /// </summary>
    public async Task<AiCompletion> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        using var request = _BuildRequest(_BuildBody(_model, messages, tools, false));
        using var response = await request
            .SendAsync(httpClient: NetworkService.GetClient(NetworkService.Ai), retryTimes: 0,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AiApiException((int)response.StatusCode, AiResponseParser.ParseError(json));

        return AiResponseParser.ParseCompletion(json)
               ?? throw new AiApiException((int)response.StatusCode, "响应格式无效：" + json);
    }

    /// <summary>
    /// 流式完成，逐块产出。
    /// </summary>
    public async IAsyncEnumerable<AiStreamChunk> StreamAsync(
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = _BuildRequest(_BuildBody(_model, messages, tools, true));
        HttpResponseMessage response;
        try
        {
            response = await request
                .SendAsync(httpClient: NetworkService.GetClient(NetworkService.Ai),
                    httpCompletionOption: HttpCompletionOption.ResponseHeadersRead,
                    retryTimes: 0, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 用户没有取消却收到取消：属于接口超时或连接中断。静默结束会被上层当成正常完成，
            // 表现为 Agent 工作到一半无声停止，故必须上抛让界面报错。
            throw new TimeoutException(Lang.Text("Tools.Ai.Error.Timeout"));
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new AiApiException((int)response.StatusCode, AiResponseParser.ParseError(json));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // 工具调用增量由上层（ModAi 的轮次循环）按 index 聚合，此处只做透传
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var chunk = AiResponseParser.TryParseData(line[5..].TrimStart());
                if (chunk is null)
                    continue;
                yield return chunk;
                if (chunk.IsDone)
                    yield break;
            }
        }
    }
}
