using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PCL.Core.AI;

/// <summary>
/// 聊天消息（OpenAI 兼容协议）。
/// </summary>
public sealed class AiChatMessage
{
    /// <summary>
    /// 角色：system / user / assistant / tool。
    /// </summary>
    public string Role { get; init; } = "user";

    /// <summary>
    /// 消息正文（工具结果消息时为工具返回值）。
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// tool 角色消息对应的工具调用 ID。
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// assistant 角色消息携带的工具调用。
    /// </summary>
    public List<AiToolCall>? ToolCalls { get; init; }

    public static AiChatMessage System(string content) => new() { Role = "system", Content = content };
    public static AiChatMessage User(string content) => new() { Role = "user", Content = content };
    public static AiChatMessage Assistant(string? content = null, List<AiToolCall>? toolCalls = null) => new() { Role = "assistant", Content = content, ToolCalls = toolCalls };
    public static AiChatMessage ToolResult(string toolCallId, string content) => new() { Role = "tool", Content = content, ToolCallId = toolCallId };
}

/// <summary>
/// 助手消息中的工具调用。
/// </summary>
public sealed class AiToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// 工具参数 JSON。流式响应中由各增量片段拼接而成，故初始为空串而非 "{}"；
    /// 读取时对空串/无效 JSON 需按无参处理。
    /// </summary>
    public string ArgumentsJson { get; set; } = "";
}

/// <summary>
/// 提供给模型的工具定义（JSON Schema 风格）。
/// </summary>
public sealed class AiToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JsonObject Parameters { get; init; } = new();

    /// <summary>
    /// 是否属于敏感操作（修改文件/设置、启动游戏等）。为 True 时执行前会弹出用户确认框，用户可拒绝。
    /// </summary>
    public bool Sensitive { get; init; }
}

/// <summary>
/// 非流式完成结果。
/// </summary>
public sealed class AiCompletion
{
    public AiCompletionChoice Choice { get; init; } = new();
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
}

public sealed class AiCompletionChoice
{
    public string? Content { get; init; }
    public List<AiToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
}

/// <summary>
/// 流式响应块。
/// </summary>
public sealed class AiStreamChunk
{
    /// <summary>
    /// 正文增量。
    /// </summary>
    public string? ContentDelta { get; set; }

    /// <summary>
    /// 工具调用增量（按 index 聚合）。
    /// </summary>
    public List<AiToolCallDelta> ToolCallDeltas { get; set; } = [];

    /// <summary>
    /// 结束原因（最后一个块携带）。
    /// </summary>
    public string? FinishReason { get; set; }

    /// <summary>
    /// 是否为流结束标记（[DONE]）。
    /// </summary>
    public bool IsDone { get; set; }

    /// <summary>
    /// 本次请求的输入 token 数（携带 usage 的末块有值）。
    /// </summary>
    public int PromptTokens { get; set; }

    /// <summary>
    /// 本次请求的输出 token 数（携带 usage 的末块有值）。
    /// </summary>
    public int CompletionTokens { get; set; }
}

public sealed class AiToolCallDelta
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ArgumentsFragment { get; set; }
}

/// <summary>
/// AI 接口调用失败。
/// </summary>
public sealed class AiApiException : Exception
{
    public int StatusCode { get; }

    public AiApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
