using System;
using System.Text.Json.Nodes;
using PCL.Core.Utils.Exts;

namespace PCL.Core.AI;

/// <summary>
/// OpenAI 兼容协议的响应解析（纯静态，可单测）。
/// </summary>
public static class AiResponseParser
{
    /// <summary>
    /// 解析一条 SSE "data:" 行内容。返回 null 表示该行不是有效数据。
    /// </summary>
    public static AiStreamChunk? TryParseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;
        data = data.Trim();
        if (data.Equals("[DONE]", StringComparison.Ordinal))
            return new AiStreamChunk { IsDone = true };
        if (!data.StartsWith("{"))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch
        {
            return null;
        }
        if (node is not JsonObject obj)
            return null;

        var chunk = new AiStreamChunk();
        var choice = obj["choices"]?[0];
        if (choice is null)
            return chunk;

        var delta = choice["delta"];
        if (delta is JsonObject deltaObj)
        {
            var content = deltaObj["content"];
            if (content is not null)
                chunk.ContentDelta = content.GetValue<string>();

            var toolCalls = deltaObj["tool_calls"] as JsonArray;
            if (toolCalls is not null && toolCalls.Count > 0)
            {
                foreach (var item in toolCalls)
                {
                    if (item is not JsonObject toolCallObj)
                        continue;
                    var deltaItem = new AiToolCallDelta
                    {
                        Index = toolCallObj["index"]?.GetValue<int>() ?? 0,
                        Id = toolCallObj["id"]?.GetValue<string>()
                    };
                    if (toolCallObj["function"] is JsonObject funcObj)
                    {
                        deltaItem.Name = funcObj["name"]?.GetValue<string>();
                        deltaItem.ArgumentsFragment = funcObj["arguments"]?.GetValue<string>();
                    }
                    chunk.ToolCallDeltas.Add(deltaItem);
                }
            }
        }

        var finish = choice["finish_reason"];
        if (finish is not null)
            chunk.FinishReason = finish.GetValue<string>();
        return chunk;
    }

    /// <summary>
    /// 解析非流式完成响应。
    /// </summary>
    public static AiCompletion? ParseCompletion(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
        if (node is not JsonObject obj)
            return null;

        var choiceObj = obj["choices"]?[0] as JsonObject;
        if (choiceObj is null)
            return null;

        var message = choiceObj["message"] as JsonObject;
        var choice = new AiCompletionChoice
        {
            Content = message?["content"]?.GetValue<string>(),
            FinishReason = choiceObj["finish_reason"]?.GetValue<string>()
        };

        if (message?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var item in toolCalls)
            {
                if (item is not JsonObject toolCallObj)
                    continue;
                var func = toolCallObj["function"] as JsonObject;
                choice.ToolCalls.Add(new AiToolCall
                {
                    Id = toolCallObj["id"]?.GetValue<string>() ?? "",
                    Name = func?["name"]?.GetValue<string>() ?? "",
                    ArgumentsJson = func?["arguments"]?.GetValue<string>() ?? "{}"
                });
            }
        }

        return new AiCompletion
        {
            Choice = choice,
            PromptTokens = obj["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
            CompletionTokens = obj["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0
        };
    }

    /// <summary>
    /// 从错误响应中提取错误信息。
    /// </summary>
    public static string ParseError(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["error"]?["message"]?.GetValue<string>() ?? json;
        }
        catch
        {
            return json;
        }
    }
}
