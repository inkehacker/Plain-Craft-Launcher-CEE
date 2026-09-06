using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
/// 报错诊断技能：只读检查类工具。
/// AI 在收到报错后可用这些工具自查上下文（日志/崩溃报告/Mods），全部只读、不敏感。
/// </summary>
public static class AiSkillDiagnosis
{
    private const int DefaultTailLines = 200;
    private const int MaxResultChars = 4000;

    public static IReadOnlyList<(AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler)>
        CreateTools() =>
    [
        (new AiToolDefinition
        {
            Name = "inspect_launcher",
            Description = "检查启动器当前状态：PCL 版本、系统概要、当前选中的游戏实例，以及启动器自身日志的尾部。用户报错后建议先调用此工具自查上下文。",
            Parameters = emptyParams
        }, InspectLauncherAsync),
        (new AiToolDefinition
        {
            Name = "read_game_latest_log",
            Description = "读取当前游戏实例 logs\\latest.log 的尾部内容（最近约 200 行）。游戏刚崩溃或启动失败时，这是最近的游戏输出。",
            Parameters = emptyParams
        }, ReadGameLatestLogAsync),
        (new AiToolDefinition
        {
            Name = "read_crash_report",
            Description = "读取当前游戏实例 crash-reports 目录下最新的崩溃报告（*.txt）开头内容。分析崩溃原因时优先调用。若无崩溃报告则返回提示。",
            Parameters = emptyParams
        }, ReadCrashReportAsync),
        (new AiToolDefinition
        {
            Name = "list_instance_mods",
            Description = "列出当前游戏实例 mods 目录下的全部文件名（含 .disabled 等，最多 100 个）与总数，用于判断 Mod 冲突、重复或过多。",
            Parameters = emptyParams
        }, ListInstanceModsAsync)
    ];

    private static readonly JsonObject emptyParams = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray()
    };

    private static async Task<string> InspectLauncherAsync(JsonObject _, CancellationToken __)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PCL 版本：" + Basics.VersionName);
        sb.AppendLine("系统概要：" + ModBase.CollectDiagnosticInfo().Replace("\n", " ").Trim());
        var instance = ModInstanceList.McMcInstanceSelected;
        sb.AppendLine("当前选中实例：" + (instance is null ? "（无）" : instance.Name + " | " + instance.PathIndie));
        var logTail = AiDiagnosisContextCollector.ReadLauncherLogTail(DefaultTailLines);
        sb.AppendLine();
        sb.AppendLine(logTail is null
            ? "启动器日志尾部：（无日志文件）"
            : "启动器日志尾部：\n" + _Cap(logTail));
        return await Task.FromResult(sb.ToString());
    }

    private static async Task<string> ReadGameLatestLogAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));
        var path = Path.Combine(instance.PathIndie, "logs", "latest.log");
        if (!File.Exists(path))
            return "（没有找到 logs\\latest.log —— 游戏可能还没有成功启动过）";
        return await Task.FromResult(_Cap(await _ReadTailAsync(path, DefaultTailLines)));
    }

    private static async Task<string> ReadCrashReportAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));
        var reports = Path.Combine(instance.PathIndie, "crash-reports");
        if (!Directory.Exists(reports))
            return "（没有 crash-reports 目录 —— 本次问题可能不是 Java 崩溃）";
        var latest = Directory.EnumerateFiles(reports, "*.txt")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
        if (latest is null)
            return "（crash-reports 目录为空）";
        var text = await File.ReadAllTextAsync(latest);
        var lines = text.Split('\n').Take(150);
        return "崩溃报告：" + Path.GetFileName(latest) + "\n---\n" + _Cap(string.Join("\n", lines));
    }

    private static async Task<string> ListInstanceModsAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));
        var modsDir = Path.Combine(instance.PathIndie, "mods");
        if (!Directory.Exists(modsDir))
            return "（mods 目录不存在）";
        var files = Directory.EnumerateFiles(modsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var active = files.Count(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
        var disabled = files.Count - active;
        var sb = new StringBuilder($"mods 目录共 {files.Count} 个文件（启用 {active} / 禁用 {disabled}）");
        if (files.Count > 0)
        {
            sb.AppendLine("：");
            foreach (var name in files.Take(100))
                sb.AppendLine("- " + name);
            if (files.Count > 100)
                sb.AppendLine($"…（其余 {files.Count - 100} 个省略）");
        }
        return await Task.FromResult(sb.ToString());
    }

    private static async Task<string> _ReadTailAsync(string path, int maxLines)
    {
        var lines = new List<string>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (await reader.ReadLineAsync() is { } line)
            lines.Add(line);
        return string.Join("\n", lines.TakeLast(maxLines));
    }

    /// <summary>结果长度上限（防止把超长内容全部塞给模型）。</summary>
    private static string _Cap(string text)
    {
        if (text.Length <= MaxResultChars)
            return text;
        var cut = text[..MaxResultChars];
        var lastNewLine = cut.LastIndexOf('\n');
        return (lastNewLine > 0 ? cut[..lastNewLine] : cut) + "\n…（内容过长已截断）";
    }
}
