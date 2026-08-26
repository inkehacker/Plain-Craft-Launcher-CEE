using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Translation;

/// <summary>
/// 构建 Minecraft 资源包 zip（纯逻辑，可单测）。
/// </summary>
public static class McResourcePackBuilder
{
    /// <summary>
    /// 根据原版版本号推算资源包格式号（pack_format），未知版本返回保守值。
    /// </summary>
    public static int PackFormatForVanillaVersion(string vanillaName)
    {
        // 从完整版本名中提取主版本段，如 1.20.1-Fabric_0.19 -> 1.20.1
        var versionPart = vanillaName ?? "";
        var dashIndex = versionPart.IndexOf('-');
        if (dashIndex > 0)
            versionPart = versionPart[..dashIndex];

        var parts = versionPart.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var minor))
            return 3;

        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;

        // 特殊：快照版本名（如 23w14a）按发布年份区间兜底
        if (!int.TryParse(parts[0], out _))
            return minor >= 18 ? 55 : 15;

        return (minor, patch) switch
        {
            (< 6, _) => 1,
            (6, _) => 1,
            (7, _) or (8, _) => 1,
            (9, _) or (10, _) => 2,
            (11, _) or (12, _) => 3,
            (13, _) or (14, _) => 4,
            (15, _) => 5,
            (16, _) => 6,
            (17, _) => 7,
            (18, _) => 8,
            (19, < 3) => 9,
            (19, 3) => 12,
            (19, >= 4) => 13,
            (20, <= 1) => 15,
            (20, <= 4) => 18,
            (20, >= 5) => 32,
            (21, <= 1) => 34,
            (21, <= 3) => 42,
            (21, 4) => 46,
            _ => 55
        };
    }

    /// <summary>
    /// 将各命名空间的语言文件打包为资源包 zip。
    /// </summary>
    /// <param name="zipPath">输出路径。</param>
    /// <param name="packFormat">pack_format 值。</param>
    /// <param name="description">资源包描述。</param>
    /// <param name="langByNamespace">命名空间（minecraft / modid）→ lang 条目。</param>
    /// <param name="langFileName">语言文件名（默认 zh_cn.json）。</param>
    public static void BuildToZip(
        string zipPath,
        int packFormat,
        string description,
        IReadOnlyDictionary<string, Dictionary<string, string>> langByNamespace,
        string langFileName = "zh_cn.json")
    {
        var mcmeta = new JsonObject
        {
            ["pack"] = new JsonObject
            {
                ["pack_format"] = packFormat,
                ["description"] = description
            }
        };

        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        WriteEntry(archive, "pack.mcmeta", mcmeta.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (var (ns, lang) in langByNamespace)
        {
            if (lang.Count == 0)
                continue;
            WriteEntry(archive, $"assets/{ns}/lang/{langFileName}", McLangFile.SerializeJson(lang));
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
