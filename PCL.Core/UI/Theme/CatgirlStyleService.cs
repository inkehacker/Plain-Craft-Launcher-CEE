using System.Collections.Generic;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 猫娘化文案覆盖：开启时把关键界面文案覆盖为猫娘风，关闭时移除覆盖、回落语言字典原值。
/// 文案属于风格内容而非界面文本，按约定放代码中。
/// </summary>
[LifecycleScope("catgirl-style", "猫娘化文案", false)]
[LifecycleService(LifecycleState.WindowCreating)]
public sealed partial class CatgirlStyleService
{
    // 键名 → 猫娘文案（保留 {0} 等占位符与 \n 换行，与语言字典格式一致）
    private static readonly Dictionary<string, string> _CatgirlTexts = new()
    {
        // 主界面 Tab
        ["Main.Tab.Launch"] = "启动喵",
        ["Main.Tab.Download"] = "下载喵",
        ["Main.Tab.Settings"] = "设置喵",
        ["Main.Tab.Tools"] = "工具喵",

        // 启动页
        ["Main.UpdateLog.Empty"] = "欢迎使用呀喵~ 今天也要开开心心地玩 Minecraft 喵！",
        ["Launch.Home.Button.Launch"] = "启动游戏喵！",
        ["Launch.Home.Button.Download"] = "下载游戏喵",
        ["Launch.Home.Button.Loading"] = "正在加载喵…",
        ["Launch.Home.SelectInstance"] = "选择实例喵",
        ["Launch.Right.CommunityHint.Title"] = "PCL CE 喵喵提示",
        ["Launch.Right.CommunityHint.Message"] = "喵呜~ 你正在使用 PCL CE（PCL 社区版）！这个版本是别人家独立开发和维护的喵，和原版的维护路线不一样，体验也会有点不同喵。\n\n如果你是不小心下载到 PCL CE 的，人家建议你改用原版 PCL 长期使用喵，这个发行版对新手可能不太友好喵。\n另外，PCL CE 的问题请去 PCL CE 的仓库提 Issue 喵，不要跑到官方仓库去反馈 PCL CE 的问题哦！",
        ["Launch.Right.CommunityHint.HidePrompt"] = "想要永久藏起这条提示的话，要输入正确的 PCL CE 开发组织名称喵。",
        ["Launch.Right.CommunityHint.InputTitle"] = "输入 PCL CE 开发组织名称喵",

        // 启动状态
        ["Launch.Status.Title.Launching"] = "正在启动游戏喵…",
        ["Launch.Status.Title.Launched"] = "游戏启动完成喵！",
        ["Launch.Status.Completed"] = "完成喵！",
        ["Launch.Status.CurrentStep"] = "当前步骤喵",
        ["Launch.Status.DownloadLibs"] = "下载支持喵",
        ["Launch.Status.DownloadSpeed"] = "下载速度喵",
        ["Launch.Status.LaunchProgress"] = "启动进度喵",
        ["Launch.Status.Trivia"] = "喵知道吗",

        // 窗口标题
        ["Main.Title.GameLog"] = "实时日志喵",
        ["Main.Title.InstanceSelect"] = "选择实例喵",
        ["Main.Title.InstanceSetup"] = "实例设置喵 - {0}",
        ["Main.Title.ResourceDownload"] = "资源下载喵 - {0}",
        ["Main.Title.SaveManagement"] = "存档管理喵 - {0}",
        ["Main.Title.TaskManager"] = "任务管理喵",

        // 更新
        ["Update.Title"] = "启动器更新喵",
        ["Update.Action"] = "更新喵",
        ["Update.Available"] = "喵！启动器有新版本可用（{0} -> {1}）\n要现在更新吗喵？",

        // 对话框与通用状态
        ["Common.Dialog.Title"] = "喵～提示",
        ["Common.Dialog.Warning"] = "喵呜！警告",
        ["Common.State.Loading"] = "加载中喵…",
        ["Common.State.Unknown"] = "唔…未知喵",
        ["Common.Status.Success"] = "成功喵！",
        ["Common.Status.Failure"] = "呜喵…失败了：",
        ["Common.Status.Cancelled"] = "已取消喵…",
        ["Common.Hint.Copied"] = "已复制喵！",

        // 通用按钮
        ["Common.Action.Agree"] = "同意喵",
        ["Common.Action.Back"] = "返回喵",
        ["Common.Action.Cancel"] = "取消喵",
        ["Common.Action.Close"] = "关闭喵",
        ["Common.Action.Confirm"] = "确定喵",
        ["Common.Action.Continue"] = "继续喵",
        ["Common.Action.Copy"] = "复制喵",
        ["Common.Action.Delete"] = "删除喵",
        ["Common.Action.Download"] = "下载喵",
        ["Common.Action.GotIt"] = "知道啦喵",
        ["Common.Action.Install"] = "安装喵",
        ["Common.Action.Open"] = "打开喵",
        ["Common.Action.OpenFolder"] = "打开文件夹喵",
        ["Common.Action.Refresh"] = "刷新喵",
        ["Common.Action.Reset"] = "重置喵",
    };

    [LifecycleStart]
    private static void _Start()
    {
        Apply(Config.Preference.CatgirlMode);
    }

    /// <summary>
    /// 应用或移除猫娘化文案覆盖（写入 Application 根资源，DynamicResource 即时生效）。
    /// </summary>
    public static void Apply(bool enabled)
    {
        var resources = Application.Current.Resources;
        foreach (var pair in _CatgirlTexts)
        {
            if (enabled)
                resources[pair.Key] = pair.Value;
            else if (resources.Contains(pair.Key))
                resources.Remove(pair.Key);
        }
    }
}
