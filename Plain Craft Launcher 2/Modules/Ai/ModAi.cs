using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Translation;

namespace PCL;

/// <summary>
/// 内置 AI 助手：会话管理与工具调用循环。
/// 使用用户自接入的 OpenAI 兼容 API（配置见 Config.Ai）。
/// </summary>
public static class ModAi
{
    public enum AiEventKind
    {
        /// <summary>正文增量（流式）。</summary>
        ContentDelta,

        /// <summary>开始执行工具。</summary>
        ToolStarted,

        /// <summary>工具执行完成，Text 为工具返回摘要。</summary>
        ToolFinished,

        /// <summary>回答完成。</summary>
        Done,

        /// <summary>出错。Text 为错误信息。</summary>
        Error
    }

    public sealed record AiEvent(AiEventKind Kind, string? Text = null);

    private static readonly List<(AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler)> _tools =
    [
        AiSkillKeybind.CreateTool(),
        AiSkillCoreTranslate.CreateTool(),
        AiSkillModTranslate.CreateTool()
    ];

    public static IReadOnlyList<AiToolDefinition> ToolDefinitions => _tools.Select(t => t.Definition).ToList();

    public static string SystemPrompt => Lang.Text("Tools.Ai.SystemPrompt");

    public static OpenAiChatClient CreateClient() =>
        new(Config.Ai.Endpoint, Config.Ai.ApiKey, Config.Ai.Model);

    /// <summary>
    /// 运行一轮对话（含工具调用循环），流式产出事件。
    /// 传入的 history 会被就地更新（可在后续对话中继续使用）。
    /// </summary>
    public static async IAsyncEnumerable<AiEvent> RunAsync(
        List<AiChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 保证历史以系统提示词开头
        if (history.All(m => m.Role != "system"))
            history.Insert(0, AiChatMessage.System(SystemPrompt));

        var client = CreateClient();
        const int maxRounds = 6;

        for (var round = 0; round < maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentBuilder = new StringBuilder();
            var toolAccumulator = new Dictionary<int, AiToolCall>();

            await foreach (var chunk in client.StreamAsync(history, ToolDefinitions, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.ContentDelta is { } delta && delta.Length > 0)
                {
                    contentBuilder.Append(delta);
                    yield return new AiEvent(AiEventKind.ContentDelta, delta);
                }

                foreach (var toolDelta in chunk.ToolCallDeltas)
                {
                    if (!toolAccumulator.TryGetValue(toolDelta.Index, out var call))
                    {
                        call = new AiToolCall();
                        toolAccumulator[toolDelta.Index] = call;
                    }
                    if (string.IsNullOrEmpty(call.Id))
                        call.Id = toolDelta.Id ?? "";
                    if (string.IsNullOrEmpty(call.Name))
                        call.Name = toolDelta.Name ?? "";
                    call.ArgumentsJson += toolDelta.ArgumentsFragment ?? "";
                }
            }

            var toolCalls = toolAccumulator.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            if (toolCalls.Count == 0)
            {
                if (contentBuilder.Length > 0)
                    history.Add(AiChatMessage.Assistant(contentBuilder.ToString()));
                yield return new AiEvent(AiEventKind.Done);
                yield break;
            }

            // 记录助手消息（含工具调用）
            history.Add(AiChatMessage.Assistant(contentBuilder.Length > 0 ? contentBuilder.ToString() : null, toolCalls));

            foreach (var call in toolCalls)
            {
                yield return new AiEvent(AiEventKind.ToolStarted, call.Name);
                string result;
                try
                {
                    result = await _ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "AI 工具执行失败：" + call.Name);
                    result = "工具执行失败：" + ex.Message;
                }
                yield return new AiEvent(AiEventKind.ToolFinished, result);
                history.Add(AiChatMessage.ToolResult(call.Id, result));
            }
        }

        yield return new AiEvent(AiEventKind.Error, Lang.Text("Tools.Ai.Error.TooManyRounds"));
    }

    private static async Task<string> _ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        foreach (var (definition, handler) in _tools)
        {
            if (!string.Equals(definition.Name, call.Name, StringComparison.Ordinal))
                continue;
            JsonObject args;
            try
            {
                args = JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? new JsonObject();
            }
            catch
            {
                args = new JsonObject();
            }
            return await handler(args, cancellationToken).ConfigureAwait(false);
        }
        return $"未知工具：{call.Name}";
    }

    /// <summary>
    /// 将语言条目分块翻译并合并结果。两个翻译技能共用。
    /// </summary>
    public static async Task<Dictionary<string, string>> TranslateChunksAsync(
        OpenAiChatClient client,
        List<Dictionary<string, string>> chunks,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var done = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = McLangFile.SerializeJson(chunk);
            var completion = await client.CompleteAsync(
            [
                AiChatMessage.System(Lang.Text("Tools.Ai.Translate.Prompt")),
                AiChatMessage.User(json)
            ], null, cancellationToken).ConfigureAwait(false);

            var text = _StripCodeFence(completion.Choice.Content ?? "");
            Dictionary<string, string> parsed;
            try
            {
                parsed = McLangFile.Parse(text, McLangFormat.Json);
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Tools.Ai.Translate.InvalidResponse") + " " + ex.Message);
            }
            if (parsed.Count == 0)
                throw new Exception(Lang.Text("Tools.Ai.Translate.EmptyResponse"));

            // 只接受源分块中存在的键，防止模型幻觉新增键
            foreach (var (key, value) in parsed)
                if (chunk.ContainsKey(key))
                    result[key] = value;

            progress?.Invoke(++done, chunks.Count);
        }
        return result;
    }

    private static string _StripCodeFence(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd < 0)
                return "";
            text = text[(firstLineEnd + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                text = text[..fenceEnd];
        }
        return text.Trim();
    }
}
