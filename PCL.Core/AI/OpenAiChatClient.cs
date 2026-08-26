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
                            ["arguments"] = call.ArgumentsJson
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
    /// 非流式完成。
    /// </summary>
    public async Task<AiCompletion> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        using var request = _BuildRequest(_BuildBody(_model, messages, tools, false));
        using var response = await request
            .SendAsync(retryTimes: 0, cancellationToken: cancellationToken)
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
                .SendAsync(httpCompletionOption: HttpCompletionOption.ResponseHeadersRead,
                    retryTimes: 0, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
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
            var dataBuilder = new StringBuilder();
            var pendingToolDeltas = new Dictionary<int, AiToolCallDelta>();

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataBuilder.Append(line.AsSpan(5).TrimStart());
                    var chunk = AiResponseParser.TryParseData(dataBuilder.ToString());
                    dataBuilder.Clear();
                    if (chunk is null)
                        continue;
                    if (chunk.IsDone)
                        yield break;

                    // 按 index 聚合工具调用增量（arguments 分段到达）
                    if (chunk.ToolCallDeltas.Count > 0)
                    {
                        foreach (var delta in chunk.ToolCallDeltas)
                        {
                            if (!pendingToolDeltas.TryGetValue(delta.Index, out var accumulated))
                            {
                                pendingToolDeltas[delta.Index] = new AiToolCallDelta { Index = delta.Index };
                                accumulated = pendingToolDeltas[delta.Index];
                            }
                            accumulated.Id ??= delta.Id;
                            accumulated.Name ??= delta.Name;
                            accumulated.ArgumentsFragment = accumulated.ArgumentsFragment + (delta.ArgumentsFragment ?? "");
                        }
                        chunk.ToolCallDeltas = [.. pendingToolDeltas.Values.OrderBy(v => v.Index)];
                    }

                    if (chunk.FinishReason is not null)
                    {
                        // 流结束：附带最终工具调用列表
                        if (pendingToolDeltas.Count > 0)
                            chunk.ToolCallDeltas = [.. pendingToolDeltas.Values.OrderBy(v => v.Index)];
                        yield return chunk;
                        yield break;
                    }

                    yield return chunk;
                }
            }
        }
    }
}
