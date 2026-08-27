using System.Collections.Generic;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 猫娘化文案覆盖：开启时把关键界面文案覆盖为猫娘风，关闭时移除覆盖、回落语言字典原值。
/// 文案属于风格内容而非界面文本，按约定放代码中。
/// </summary>
[LifecycleService(LifecycleState.WindowCreating)]
public sealed class CatgirlStyleService
{
    // 键名 → 猫娘文案
    private static readonly Dictionary<string, string> _CatgirlTexts = new()
    {
        ["Main.UpdateLog.Empty"] = "欢迎使用呀喵~ 今天也要开开心心地玩 Minecraft 喵！",
        ["Launch.Right.CommunityHint.Title"] = "PCL CE 喵喵提示",
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
