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

        /// <summary>token 用量更新（PromptTokens/CompletionTokens 为本次会话累计值）。</summary>
        UsageUpdated,

        /// <summary>出错。Text 为错误信息。</summary>
        Error
    }

    public sealed record AiEvent(AiEventKind Kind, string? Text = null, int PromptTokens = 0, int CompletionTokens = 0);

    private static readonly List<(AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler)> _tools =
    [
        AiSkillKeybind.CreateTool(),
        AiSkillCoreTranslate.CreateTool(),
        AiSkillModTranslate.CreateTool()
    ];

    public static IReadOnlyList<AiToolDefinition> ToolDefinitions => _tools.Select(t => t.Definition).ToList();

    public static string SystemPrompt => BuildSystemPrompt();

    /// <summary>本次会话累计输入 token 数。</summary>
    public static int SessionPromptTokens { get; private set; }

    /// <summary>本次会话累计输出 token 数。</summary>
    public static int SessionCompletionTokens { get; private set; }

    /// <summary>重置会话 token 统计（新建/加载会话时调用）。</summary>
    public static void ResetTokenUsage()
    {
        SessionPromptTokens = 0;
        SessionCompletionTokens = 0;
    }

    /// <summary>
    ///     系统提示词 = 基础提示词 + 当前 UI 语言规则（让 AI 始终用启动器界面语言回复）+ 猫娘人设（可选）+ 长期记忆。
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var prompt = Lang.Text("Tools.Ai.SystemPrompt");
        var rule = LanguageRule(LocalizationService.CurrentLanguage.Code);
        if (rule.Length > 0)
            prompt += "\n" + rule;

        // 猫娘人设是提示词内容而非界面文本，故放在代码中
        if (Config.Preference.CatgirlMode)
            prompt += "\n\n你现在是猫娘。保持全能助手的能力和规则不变，但语气要软萌可爱：自称「喵」，称呼用户为「主人」，句尾常带「喵」「呜喵」等语气词，回答简洁自然，不要过度堆砌语气词。";

        var memory = LoadMemory();
        if (memory.Count > 0)
        {
            // 记忆注入是提示词内容而非界面文本，故放在代码中
            prompt += "\n\n用户长期记忆（回答时可参考）：\n" + string.Join("\n", memory.Select(m => "- " + m));
        }
        return prompt;
    }

    /// <summary>
    ///     按 UI 语言返回回复语言指令。语言规则是提示词内容而非界面文本，故放在代码中。
    /// </summary>
    private static string LanguageRule(string languageCode) => languageCode switch
    {
        "zh-CN" => "请始终使用简体中文与用户交流。",
        "zh-TW" => "請始終使用繁體中文與用戶交流。",
        "en-US" => "Always communicate with the user in English.",
        "en-GB" => "Always communicate with the user in British English.",
        "ja-JP" => "常にユーザーには日本語で応答してください。",
        "fr-FR" => "Répondez toujours à l'utilisateur en français.",
        "es-ES" => "Responde siempre al usuario en español.",
        _ => ""
    };

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

                // 携带 usage 的末块（stream_options include_usage）
                if (chunk.PromptTokens > 0 || chunk.CompletionTokens > 0)
                {
                    SessionPromptTokens += chunk.PromptTokens;
                    SessionCompletionTokens += chunk.CompletionTokens;
                }
            }

            // 每轮结束后下发累计用量
            yield return new AiEvent(AiEventKind.UsageUpdated, null, SessionPromptTokens, SessionCompletionTokens);

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

    #region 历史会话持久化

    private static string SessionDirectory => Path.Combine(ModBase.exePath, "PCL", "AiSessions");

    /// <summary>列出所有历史会话（按时间倒序）。</summary>
    public static List<(string Path, string Name)> ListSessions()
    {
        var result = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(SessionDirectory))
                return result;
            foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*.json").OrderByDescending(f => f))
            {
                var name = _SessionNameFromFile(file);
                if (name.Length > 0)
                    result.Add((file, name));
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 历史会话列表读取失败");
        }
        return result;
    }

    /// <summary>加载历史会话（剔除 system 消息，运行时由 RunAsync 重新注入）。</summary>
    public static List<AiChatMessage> LoadSession(string path)
    {
        try
        {
            var messages = JsonSerializer.Deserialize<List<AiChatMessage>>(File.ReadAllText(path)) ?? [];
            return messages.Where(m => m.Role != "system").ToList();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 历史会话加载失败：" + path);
            return [];
        }
    }

    /// <summary>保存会话（剔除 system 消息）。path 为 null 时按首个用户消息新建文件。返回会话文件路径。</summary>
    public static string SaveSession(string? path, List<AiChatMessage> history)
    {
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            path ??= CreateSessionPath(history);
            var messages = history.Where(m => m.Role != "system").ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 历史会话保存失败");
            return path ?? "";
        }
    }

    /// <summary>按首个用户消息生成会话文件名（时间戳 + 截断标题）。</summary>
    public static string CreateSessionPath(List<AiChatMessage> history)
    {
        var first = history.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
        var name = new string(first.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (name.Length > 20)
            name = name[..20];
        if (name.Length == 0)
            name = "chat";
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(SessionDirectory, $"{timestamp}-{name}.json");
    }

    private static string _SessionNameFromFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dash = name.IndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : name;
    }

    #endregion

    #region 长期记忆

    private static string MemoryFilePath => Path.Combine(ModBase.exePath, "PCL", "AiMemory.json");

    /// <summary>读取长期记忆（最多 50 条，失败返回空列表）。</summary>
    public static List<string> LoadMemory()
    {
        try
        {
            if (!File.Exists(MemoryFilePath))
                return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(MemoryFilePath)) ?? [];
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 长期记忆读取失败");
            return [];
        }
    }

    private static void _SaveMemory(List<string> memory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryFilePath)!);
            File.WriteAllText(MemoryFilePath,
                JsonSerializer.Serialize(memory.TakeLast(50).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 长期记忆保存失败");
        }
    }

    /// <summary>
    /// 从对话中提炼用户长期偏好并合并入记忆（异步执行，失败静默不影响主流程）。
    /// 记忆上限 50 条，按内容去重。
    /// </summary>
    public static async Task LearnMemoryAsync(List<AiChatMessage> history, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(Config.Ai.ApiKey))
                return;
            var conversation = history
                .Where(m => m.Role != "system" && m.Content is { Length: > 0 })
                .TakeLast(10)
                .Select(m => $"{m.Role}: {m.Content[..Math.Min(m.Content.Length, 300)]}")
                .ToList();
            if (conversation.Count == 0)
                return;

            var client = CreateClient();
            var completion = await client.CompleteAsync(
            [
                AiChatMessage.System("你是记忆提炼助手。从对话中提取值得长期记住的关于用户的事实或偏好（例如：玩家名称、常用游戏版本、模组/玩法偏好、使用习惯）。最多 3 条，只输出 JSON 字符串数组，如 [\"...\"]；没有值得记住的内容时输出 []。不要输出其他内容。"),
                AiChatMessage.User(string.Join("\n", conversation))
            ], null, cancellationToken).ConfigureAwait(false);

            var text = _StripCodeFence(completion.Choice.Content ?? "").Trim();
            List<string>? extracted;
            try
            {
                extracted = JsonSerializer.Deserialize<List<string>>(text);
            }
            catch
            {
                return;
            }
            if (extracted is null || extracted.Count == 0)
                return;

            var memory = LoadMemory();
            var existing = new HashSet<string>(memory, StringComparer.Ordinal);
            var added = false;
            foreach (var fact in extracted)
            {
                var trimmed = fact.Trim();
                if (trimmed.Length == 0 || trimmed.Length > 100 || existing.Contains(trimmed))
                    continue;
                memory.Add(trimmed);
                existing.Add(trimmed);
                added = true;
            }
            if (added)
                _SaveMemory(memory);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 记忆学习失败");
        }
    }

    #endregion
}
