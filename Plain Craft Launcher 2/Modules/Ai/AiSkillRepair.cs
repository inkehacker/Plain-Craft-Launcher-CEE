using System.IO;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App.Localization;
using PCL.Core.Logging;

namespace PCL;

/// <summary>
/// 报错修复技能：对启动器/游戏实例执行可见操作。
/// 打开类操作只读导航；retry_launch（重新启动游戏）与 clear_cache（删除缓存文件）标记为敏感，
/// 执行前 ModAi 会弹确认框征求用户同意。
/// </summary>
public static class AiSkillRepair
{
    private static readonly JsonObject emptyParams = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray()
    };

    public static IReadOnlyList<(AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler)>
        CreateTools() =>
    [
        (new AiToolDefinition
        {
            Name = "open_log_folder",
            Description = "在系统文件管理器中定位并选中启动器当前的日志文件（只打开窗口，不修改任何内容）。用户想查看日志细节时调用。",
            Parameters = emptyParams
        }, OpenLogFolderAsync),
        (new AiToolDefinition
        {
            Name = "open_crash_report_folder",
            Description = "在系统文件管理器中打开当前游戏实例的 crash-reports 崩溃报告文件夹（不存在时打开实例文件夹）。只打开窗口，不修改内容。",
            Parameters = emptyParams
        }, OpenCrashReportFolderAsync),
        (new AiToolDefinition
        {
            Name = "open_instance_folder",
            Description = "在系统文件管理器中打开当前游戏实例的文件夹（.minecraft 目录）。只打开窗口，不修改内容。",
            Parameters = emptyParams
        }, OpenInstanceFolderAsync),
        (new AiToolDefinition
        {
            Name = "open_mods_folder",
            Description = "在系统文件管理器中打开当前游戏实例的 mods 文件夹。只打开窗口，不修改内容。",
            Parameters = emptyParams
        }, OpenModsFolderAsync),
        (new AiToolDefinition
        {
            Name = "open_settings_page",
            Description = "把启动器界面跳转到对应设置页：param 为 java（Java 设置，可切换 Java 版本/内存）、update（更新设置）、launch（启动设置，可调整启动参数）。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["param"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("java", "update", "launch"),
                        ["description"] = "要打开的设置页：java / update / launch"
                    }
                },
                ["required"] = new JsonArray("param")
            }
        }, OpenSettingsPageAsync),
        (new AiToolDefinition
        {
            Name = "retry_launch",
            Description = "回到启动页并重新启动当前选中的游戏实例。适用于“刚才启动失败/崩溃，想直接重试一次”的场景。执行前会请求用户确认。",
            Parameters = emptyParams,
            Sensitive = true
        }, RetryLaunchAsync),
        (new AiToolDefinition
        {
            Name = "clear_cache",
            Description = "清理启动器缓存目录（PCL\\Cache）中的临时文件，可解决部分下载/更新残留导致的问题。执行前会请求用户确认。",
            Parameters = emptyParams,
            Sensitive = true
        }, ClearCacheAsync)
    ];

    private static string _InstanceRequired() => Lang.Text("Tools.Ai.Error.NoInstance");

    /// <summary>在 UI 线程执行动作（PageChange/LaunchButtonClick 需要）；已在 UI 线程则直接执行。</summary>
    private static async Task _RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }

    private static async Task<string> OpenLogFolderAsync(JsonObject _, CancellationToken __)
    {
        var log = LogWrapper.CurrentLogger.CurrentLogFiles.LastOrDefault();
        if (string.IsNullOrEmpty(log) || !File.Exists(log))
            return "（启动器当前没有日志文件）";
        ModBase.ShellOnly("explorer.exe", "/select,\"" + log + "\"");
        return "已打开日志文件夹并选中日志文件";
    }

    private static async Task<string> OpenCrashReportFolderAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected ?? throw new Exception(_InstanceRequired());
        var reports = Path.Combine(instance.PathIndie, "crash-reports");
        ModBase.ShellOnly(Directory.Exists(reports) ? reports : instance.PathIndie);
        return "已打开崩溃报告文件夹";
    }

    private static async Task<string> OpenInstanceFolderAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected ?? throw new Exception(_InstanceRequired());
        ModBase.ShellOnly(instance.PathIndie);
        return "已打开实例文件夹";
    }

    private static async Task<string> OpenModsFolderAsync(JsonObject _, CancellationToken __)
    {
        var instance = ModInstanceList.McMcInstanceSelected ?? throw new Exception(_InstanceRequired());
        ModBase.ShellOnly(Path.Combine(instance.PathIndie, "mods"));
        return "已打开 mods 文件夹";
    }

    private static async Task<string> OpenSettingsPageAsync(JsonObject args, CancellationToken __)
    {
        // 本地白名单校验：只允许三个固定设置页，防止任意跳转
        var page = args["param"]?.GetValue<string>() ?? "";
        var (subType, label) = page switch
        {
            "java" => (FormMain.PageSubType.SetupJava, "Java 设置"),
            "update" => (FormMain.PageSubType.SetupUpdate, "更新设置"),
            "launch" => (FormMain.PageSubType.SetupLaunch, "启动设置"),
            _ => throw new Exception($"未知的设置页参数：{page}（仅允许 java/update/launch）")
        };
        await _RunOnUiAsync(() => ModMain.frmMain.PageChange(FormMain.PageType.Setup, subType)).ConfigureAwait(false);
        return $"已跳转到{label}页";
    }

    private static async Task<string> RetryLaunchAsync(JsonObject _, CancellationToken __)
    {
        await _RunOnUiAsync(() =>
        {
            ModMain.frmMain.PageChange(FormMain.PageType.Launch);
            // 复用启动按钮的启动中/状态守卫，避免重复启动
            if (ModMain.frmLaunchLeft is not null)
                ModMain.frmLaunchLeft.LaunchButtonClick();
        }).ConfigureAwait(false);
        return "已回到启动页并开始重新启动游戏";
    }

    private static async Task<string> ClearCacheAsync(JsonObject _, CancellationToken __)
    {
        // 白名单目录：仅清理 pathTemp\Cache，且只删除其直接子项
        var cache = Path.Combine(ModBase.pathTemp, "Cache");
        if (!Directory.Exists(cache))
            return "（缓存目录不存在，无需清理）";
        var dirs = Directory.EnumerateDirectories(cache).ToList();
        var files = Directory.EnumerateFiles(cache).ToList();
        foreach (var dir in dirs)
            Directory.Delete(dir, true);
        foreach (var file in files)
            File.Delete(file);
        return $"已清理缓存：删除 {dirs.Count} 个文件夹、{files.Count} 个文件";
    }
}
