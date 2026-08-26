using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft.Keybind;

/// <summary>
/// Minecraft options.txt 键位校验（纯逻辑，可单测）。
/// AI 输出的键位修改必须先经过校验才能写入 options.txt。
/// </summary>
public static class KeybindValidator
{
    private static readonly HashSet<string> KeyboardKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "space", "tab", "enter", "escape", "backspace", "delete", "insert",
        "up", "down", "left", "right",
        "page.up", "page.down", "home", "end",
        "left.shift", "right.shift", "left.control", "right.control", "left.alt", "right.alt",
        "capslock", "print.screen", "scroll.lock", "pause",
        "apostrophe", "backslash", "comma", "equal", "grave.accent",
        "left.bracket", "minus", "period", "right.bracket", "semicolon", "slash",
        "numpad.0", "numpad.1", "numpad.2", "numpad.3", "numpad.4",
        "numpad.5", "numpad.6", "numpad.7", "numpad.8", "numpad.9",
        "numpad.add", "numpad.decimal", "numpad.divide", "numpad.enter",
        "numpad.multiply", "numpad.subtract"
    };

    private static readonly HashSet<string> MouseKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "right", "middle", "button4", "button5", "button6", "button7", "button8"
    };

    /// <summary>
    /// 校验绑定名（如 key_key.sneak、key_key.hotbar.1）。仅允许 key_ 前缀的选项名。
    /// </summary>
    public static bool IsValidBindingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Length > 64 || !name.StartsWith("key_", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '_')
                return false;
        return true;
    }

    /// <summary>
    /// 校验键值（如 key.keyboard.left.control、key.mouse.right）。
    /// </summary>
    public static bool IsValidKeyValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.StartsWith("key.keyboard.", StringComparison.OrdinalIgnoreCase))
            return KeyboardKeys.Contains(value["key.keyboard.".Length..]);
        if (value.StartsWith("key.mouse.", StringComparison.OrdinalIgnoreCase))
            return MouseKeys.Contains(value["key.mouse.".Length..]);
        return false;
    }

    /// <summary>
    /// 校验一条键位修改。返回 null 表示合法，否则返回错误描述。
    /// </summary>
    public static string? ValidateChange(string binding, string value)
    {
        if (!IsValidBindingName(binding))
            return $"无效的键位绑定名：{binding}";
        if (!IsValidKeyValue(value))
            return $"无效的键值：{value}";
        return null;
    }
}
