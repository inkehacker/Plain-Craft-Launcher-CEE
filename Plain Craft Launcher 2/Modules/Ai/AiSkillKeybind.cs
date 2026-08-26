using System.IO;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Keybind;

namespace PCL;

/// <summary>
/// 键位调整技能：修改当前实例的 options.txt。
/// AI 输出的修改必须先通过 KeybindValidator 校验。
/// </summary>
public static class AiSkillKeybind
{
    public static (AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler) CreateTool() =>
        (definition, ExecuteAsync);

    private static readonly AiToolDefinition definition = new()
    {
        Name = "set_keybinds",
        Description = "调整 Minecraft 键位设置。把用户的自然语言要求（如“把潜行键改成左 Ctrl”）转换为键位修改并写入当前实例的 options.txt。binding 使用 Minecraft 的键位绑定名（如 key_key.sneak、key_key.jump、key_key.hotbar.1），key 使用 key.keyboard.<键名> 或 key.mouse.<按钮> 格式（如 key.keyboard.left.control、key.mouse.right、key.keyboard.f）。",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["changes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "需要修改的键位列表",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["binding"] = new JsonObject { ["type"] = "string", ["description"] = "Minecraft 键位绑定名，如 key_key.sneak" },
                            ["key"] = new JsonObject { ["type"] = "string", ["description"] = "目标键值，如 key.keyboard.left.control" }
                        },
                        ["required"] = new JsonArray("binding", "key")
                    }
                }
            },
            ["required"] = new JsonArray("changes")
        }
    };

    public static async Task<string> ExecuteAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));
        var optionsPath = Path.Combine(instance.PathIndie, "options.txt");
        if (!File.Exists(optionsPath))
            throw new Exception(Lang.Text("Tools.Ai.Keybind.NoOptionsFile"));

        var changes = new List<(string Binding, string Key)>();
        if (args["changes"] is JsonArray array)
            foreach (var item in array)
                if (item is JsonObject obj)
                    changes.Add((
                        obj["binding"]?.GetValue<string>() ?? "",
                        obj["key"]?.GetValue<string>() ?? ""));
        if (changes.Count == 0)
            throw new Exception(Lang.Text("Tools.Ai.Keybind.NoChanges"));

        var errors = new List<string>();
        foreach (var (binding, key) in changes)
            if (KeybindValidator.ValidateChange(binding, key) is { } error)
                errors.Add($"{binding}：{error}");
        if (errors.Count > 0)
            throw new Exception(Lang.Text("Tools.Ai.Keybind.Invalid") + "\n" + string.Join("\n", errors));

        // 备份原文件
        var backupPath = optionsPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(optionsPath, backupPath, true);

        ModBase.IniClearCache(optionsPath);
        foreach (var (binding, key) in changes)
            ModBase.WriteIni(optionsPath, binding, key);
        ModBase.IniClearCache(optionsPath);

        return Lang.Text("Tools.Ai.Keybind.Success", changes.Count, Path.GetFileName(backupPath))
               + "\n" + string.Join("\n", changes.Select(c => $"{c.Binding} -> {c.Key}"));
    }
}
