using PCL.Core.AI;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>
/// 报错 AI 诊断服务：把报错上下文交给「AI 助手」页的通用 Agent 处理（复用 ModAi.RunAsync 工具循环，
/// 能力不限于预设动作，AI 可自由检查上下文、执行修复工具或直接对话解决）。
/// 同时为 ModAi 的敏感工具注入「允许/拒绝」用户确认门。
/// </summary>
internal static class AiDiagnosisService
{
    static AiDiagnosisService()
    {
        // 注入敏感工具确认门：ModAi 在 Sensitive 工具执行前回调本服务弹确认框
        ModAi.ToolConfirm = ConfirmToolAsync;
    }

    /// <summary>诊断功能是否可用（开关 + 已配置 API Key）。</summary>
    public static bool IsAvailable =>
        Config.Ai.AiDiagnosisEnabled && !string.IsNullOrEmpty(Config.Ai.ApiKey);

    /// <summary>
    /// 打开 AI 助手并自动开始一次报错诊断会话。可由任意线程调用（弹窗按钮、日志线程等）。
    /// 会话在 AI 助手页进行：自动开跑分析，用户可继续追问，AI 可调用工具检查上下文或执行修复动作
    /// （敏感动作执行前会弹确认框）。Agent 已在运行时本次请求被忽略（提示稍候）。
    /// </summary>
    public static void OpenAgentSession(
        string kind,
        string? errorSummary = null,
        string? exceptionDetails = null,
        IReadOnlyList<string>? gameLogTail = null,
        string? analysisText = null)
    {
        if (!IsAvailable)
        {
            HintService.Hint(Lang.Text("Tools.Ai.Setting.NoKey"), HintType.Warning);
            return;
        }

        var text = AiDiagnosisContextCollector.BuildDiagnosisMessage(
            kind, errorSummary, exceptionDetails, gameLogTail, analysisText);
        if (string.IsNullOrEmpty(text))
            return;

        ModBase.RunInUi(() =>
        {
            var page = ModMain.frmToolsAi;
            if (page is { IsAgentRunning: true })
            {
                // Agent 正在运行：不能打断当前会话，本次请求丢弃
                HintService.Hint(Lang.Text("Ai.Diagnosis.Busy"), HintType.Info);
                return;
            }

            PageToolsAi.QueueDiagnosis(text);

            // 页面未创建或不可见（在别的页/别的工具页）→ 先导航过去；导航后 Loaded/可见性事件会消费队列
            if (page is null || !page.IsVisible)
            {
                if (ModMain.frmMain is not null)
                    ModMain.frmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsAi);
            }

            // 兜底消费：页面已在眼前则立即开跑；刚导航过去时 Loaded 已消费则此处为空操作
            ModMain.frmToolsAi?.TryConsumePendingDiagnosis();
        });
    }

    /// <summary>
    /// ModAi 敏感工具执行前的确认门：弹「允许/拒绝」框，返回用户选择。
    /// 在后台线程（工具执行线程）调用，内部切换到 UI 线程弹窗并等待结果。
    /// </summary>
    private static Task<bool> ConfirmToolAsync(AiToolDefinition definition, AiToolCall call)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ModBase.RunInUi(() =>
        {
            // 敏感工具当前均无参数；将来若带参，展示参数预览防止误导
            var argsText = call.ArgumentsJson?.Trim();
            var message = Lang.Text("Ai.Diagnosis.Confirm.Message", ModAi.ToolDisplayName(definition.Name));
            if (!string.IsNullOrEmpty(argsText) && argsText != "{}")
            {
                var args = argsText.Length > 200 ? argsText[..200] + "…" : argsText;
                message += "\n" + args;
            }
            var allowed = ModMain.MyMsgBox(
                message,
                Lang.Text("Ai.Diagnosis.Confirm.Title"),
                Lang.Text("Common.Action.Agree"),
                Lang.Text("Common.Action.Decline"),
                isWarn: true,
                enableAiButton: false) == 1;
            tcs.SetResult(allowed);
        });
        return tcs.Task;
    }
}
