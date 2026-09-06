using System.Globalization;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI;

namespace PCL;

internal sealed class CrashDialogPresenter(CrashAnalysisContext context)
{
    private readonly CrashReportExporter _exporter = new();
    private readonly CrashResultFormatter _formatter = new();

    public void Output(
        bool isHandAnalyze,
        List<string>? extraFiles)
    {
        ModMain.frmMain.ShowWindowToTop();

        var crashContent = _formatter.Format(context, isHandAnalyze);
        var resultText = crashContent.Text;
        var directFile = context.DirectOpenFile;
        var openInstanceSettings =
            context.Instance is not null &&
            crashContent.SuggestedAction == CrashSuggestedAction.OpenInstanceSettings;

        var title = isHandAnalyze
            ? Lang.Text("Crash.Dialog.Title.Manual")
            : Lang.Text("Crash.Dialog.Title.Auto");

        var secondButtonText = _GetSecondButtonText(
            isHandAnalyze,
            directFile,
            openInstanceSettings);

        var thirdButtonText = isHandAnalyze
            ? ""
            : Lang.Text("Crash.Dialog.Button.ExportReport");

        var secondButtonAction = _GetSecondButtonAction(
            isHandAnalyze,
            directFile,
            openInstanceSettings);

        // AI 诊断按钮：可用时显示，点击后关闭本弹窗并交给 AI 助手页的 Agent 分析
        var aiButtonText = AiDiagnosisService.IsAvailable ? Lang.Text("Ai.Diagnosis.Button") : "";

        var selectedButton = MsgBoxWrapper.ShowWithCustomButtons(
            resultText,
            title,
            MsgBoxTheme.Info,
            true,
            new MsgBoxButtonInfo(Lang.Text("Common.Action.Confirm"), 1),
            new MsgBoxButtonInfo(secondButtonText, 2, secondButtonAction),
            new MsgBoxButtonInfo(thirdButtonText, 3),
            new MsgBoxButtonInfo(aiButtonText, 4));

        switch (selectedButton)
        {
            case 2:
                if (openInstanceSettings)
                    _OpenModLoaderInstallPage();
                else if (directFile is not null)
                    _OpenDirectFile(directFile);
                break;

            case 3:
                _ExportReport(extraFiles);
                break;

            case 4:
                _StartAiAgentSession(resultText);
                break;
        }
    }

    /// <summary>
    ///     把崩溃上下文交给「AI 助手」的 Agent：本地分析结果（作为先验）+ 游戏输出尾部，
    ///     在 AI 助手页自动开跑分析（AI 可用工具进一步检查日志/Mods 或执行修复动作）。
    /// </summary>
    private void _StartAiAgentSession(string crashText)
    {
        var rawOutput = context.RawFiles
            .FirstOrDefault(f => f.FullPath.EndsWith("RawOutput.log", StringComparison.OrdinalIgnoreCase))
            ?.Lines;

        AiDiagnosisService.OpenAgentSession(
            "gameCrash",
            analysisText: crashText,
            gameLogTail: rawOutput);
    }

    private static string _GetSecondButtonText(
        bool isHandAnalyze,
        CrashLogEntry? directFile,
        bool openInstanceSettings)
    {
        if (isHandAnalyze || directFile is null)
            return "";

        return openInstanceSettings
            ? Lang.Text("Crash.Dialog.Button.GoToModify")
            : Lang.Text("Crash.Dialog.Button.OpenLog");
    }

    private static Action? _GetSecondButtonAction(
        bool isHandAnalyze,
        CrashLogEntry? directFile,
        bool openInstanceSettings)
    {
        if (isHandAnalyze ||
            directFile is null ||
            openInstanceSettings)
            return null;

        return () => _OpenDirectFile(directFile);
    }

    private void _OpenModLoaderInstallPage()
    {
        PageInstanceLeft.McInstance = context.Instance;

        ModBase.RunInUi(() => ModMain.frmMain.PageChange(
            FormMain.PageType.InstanceSetup,
            FormMain.PageSubType.VersionInstall));
    }

    private static void _OpenDirectFile(CrashLogEntry directFile)
    {
        if (File.Exists(directFile.FullPath))
        {
            Basics.OpenPath(directFile.FullPath);
            return;
        }

        var filePath = Path.Combine(Paths.Temp, "Crash.txt");

        CrashFileIo.WriteText(filePath, string.Join("\r\n", directFile.Lines));
        Basics.OpenPath(filePath);
    }

    private void _ExportReport(List<string>? extraFiles)
    {
        string? fileAddress = null;

        try
        {
            fileAddress = _SelectReportSavePath();

            if (string.IsNullOrEmpty(fileAddress))
                return;

            _exporter.Export(context, fileAddress, extraFiles);

            HintWrapper.Show(
                Lang.Text("Crash.Report.Export.Success"),
                HintTheme.Success);

            Basics.OpenPath(Path.GetDirectoryName(fileAddress) ?? fileAddress);
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Crash", "导出错误报告失败");

            var message = _CreateExportFailureMessage(fileAddress, ex);
            MsgBoxWrapper.ShowWithCustomButtons(
                message,
                Lang.Text("Crash.Export.Failed.Title"),
                MsgBoxTheme.Error,
                false,
                new MsgBoxButtonInfo(Lang.Text("Common.Action.Confirm"), 1),
                new MsgBoxButtonInfo(
                    Lang.Text("Crash.Export.Failed.CopyDetails"),
                    2,
                    () => ModBase.ClipboardSet(message, false)));
        }
    }

    private static string _CreateExportFailureMessage(
        string? targetZipPath,
        Exception exception)
    {
        var summary = string.IsNullOrWhiteSpace(targetZipPath)
            ? Lang.Text("Crash.Export.Failed.MessageWithoutPath")
            : Lang.Text("Crash.Export.Failed.Message", targetZipPath);

        return ExceptionDetails.Compose(summary, exception);
    }

    private static string? _SelectReportSavePath()
    {
        string? fileAddress = null;

        ModBase.RunInUiWait(() => fileAddress = SystemDialogs.SelectSaveFile(
            Lang.Text("Crash.Report.SaveDialog.Title"),
            _GetDefaultReportFileName(),
            Lang.Text("Crash.Report.SaveDialog.Filter")));

        return fileAddress;
    }

    private static string _GetDefaultReportFileName()
    {
        var time = DateTime.Now
            .ToString("G", CultureInfo.InvariantCulture)
            .Replace("/", "-")
            .Replace(":", ".")
            .Replace(" ", "_");

        return Lang.Text("Crash.Report.SaveDialog.DefaultFileName", time);
    }
}