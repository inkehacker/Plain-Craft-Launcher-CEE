using System.IO;
using System.Text;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;

namespace PCL;

/// <summary>
/// 报错诊断的上下文收集器：把报错场景/摘要/详情/游戏输出/本地崩溃分析汇总成发给 AI 的消息文本，
/// 统一脱敏（token、密码等）并限制总长度。启动器日志不注入对话，AI 需要时通过 inspect_launcher 工具读取。
/// </summary>
internal static class AiDiagnosisContextCollector
{
    private const int MaxContextChars = 12000;
    private const int GameLogTailLines = 150;
    private const int SummaryMaxChars = 500;
    private const int DetailsMaxChars = 3000;
    private const int AnalysisMaxChars = 2000;

    /// <summary>
    /// 构建诊断会话的用户消息文本。所有输入在进入模型前都经过脱敏与截断。
    /// </summary>
    /// <param name="kind">"launcher" / "gameCrash" / "unhandled"。</param>
    /// <param name="errorSummary">面向用户的错误摘要。</param>
    /// <param name="exceptionDetails">脱敏前的异常详细文本（内部脱敏）。</param>
    /// <param name="gameLogTail">游戏原始输出尾部（如崩溃对话框捕获的 RawOutput）。</param>
    /// <param name="analysisText">本地崩溃分析的展示文本（可作为先验，可为 null）。</param>
    public static string? BuildDiagnosisMessage(
        string kind,
        string? errorSummary,
        string? exceptionDetails,
        IReadOnlyList<string>? gameLogTail = null,
        string? analysisText = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Lang.Text("Ai.Diagnosis.Kind." + kind switch
        {
            "gameCrash" => "GameCrash",
            "unhandled" => "Unhandled",
            _ => "Launcher"
        }));
        if (!string.IsNullOrWhiteSpace(errorSummary))
            sb.AppendLine(Lang.Text("Ai.Diagnosis.Summary") + "\n" + _Cap(errorSummary!, SummaryMaxChars));
        if (!string.IsNullOrWhiteSpace(exceptionDetails))
            sb.AppendLine(Lang.Text("Ai.Diagnosis.Details") + "\n" + _Cap(exceptionDetails!, DetailsMaxChars));
        if (!string.IsNullOrWhiteSpace(analysisText))
            sb.AppendLine(Lang.Text("Ai.Diagnosis.Analysis") + "\n" + _Cap(analysisText!, AnalysisMaxChars));
        var tail = JoinLines(gameLogTail, GameLogTailLines);
        if (!string.IsNullOrEmpty(tail))
            sb.AppendLine(Lang.Text("Ai.Diagnosis.GameOutput") + "\n" + tail);

        var text = Redact(sb.ToString()).Trim();
        if (text.Length == 0)
            return null;
        return text;
    }

    /// <summary>
    /// 读取当前启动器日志文件尾部（供 inspect_launcher 工具使用；文件以 FileShare.ReadWrite 打开，可直接读取）。
    /// </summary>
    internal static string? ReadLauncherLogTail(int maxLines = 200)
    {
        try
        {
            var path = LogWrapper.CurrentLogger.CurrentLogFiles.LastOrDefault();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            if (lines.Count == 0)
                return null;
            return Redact(string.Join("\n", lines.TakeLast(maxLines)));
        }
        catch
        {
            return null;
        }
    }

    private static string? JoinLines(IEnumerable<string>? lines, int maxLines)
    {
        if (lines is null)
            return null;
        var list = lines.TakeLast(maxLines).ToList();
        return list.Count == 0 ? null : string.Join("\n", list);
    }

    private static string _Cap(string text, int maxChars)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
            return trimmed;
        var cut = trimmed[..maxChars];
        var lastNewLine = cut.LastIndexOf('\n');
        return (lastNewLine > 0 ? cut[..lastNewLine] : cut).TrimEnd() + "\n…（过长已截断）";
    }

    /// <summary>脱敏并限制总长度（防注入兜底：日志与异常只是数据）。</summary>
    private static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = ExceptionDetails.RedactSensitiveText(text);
        return text.Length > MaxContextChars ? text[..MaxContextChars] : text;
    }
}
