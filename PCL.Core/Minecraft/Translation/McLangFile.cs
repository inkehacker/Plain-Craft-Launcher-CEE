using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Translation;

/// <summary>
/// Minecraft 语言文件格式。
/// </summary>
public enum McLangFormat
{
    /// <summary>1.6+ 的 JSON 格式（assets/minecraft/lang/xx_xx.json）。</summary>
    Json,

    /// <summary>1.1-1.5 的 key=value 格式（lang/xx_XX.lang）。</summary>
    LangLegacy
}

/// <summary>
/// Minecraft 语言文件解析/序列化（纯逻辑，可单测）。
/// </summary>
public static class McLangFile
{
    /// <summary>
    /// 根据 jar 内条目路径判断语言文件格式。
    /// </summary>
    public static McLangFormat FormatFromPath(string entryPath)
    {
        var name = Path.GetFileName(entryPath);
        return name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)
            ? McLangFormat.LangLegacy
            : McLangFormat.Json;
    }

    /// <summary>
    /// 解析语言文件内容。
    /// </summary>
    public static Dictionary<string, string> Parse(string content, McLangFormat format)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
            return result;

        if (format == McLangFormat.LangLegacy)
        {
            using var reader = new StringReader(content);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var splitIndex = line.IndexOf('=');
                if (splitIndex <= 0)
                    continue;
                result[line[..splitIndex]] = line[(splitIndex + 1)..];
            }
            return result;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return result;
        }
        if (node is not JsonObject obj)
            return result;
        foreach (var (key, value) in obj)
        {
            if (value is not null)
                result[key] = value.GetValue<string>();
        }
        return result;
    }

    /// <summary>
    /// 序列化为 1.6+ JSON 语言文件（按键排序、标准转义）。
    /// </summary>
    public static string SerializeJson(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            obj[key] = map[key];
        return obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 保留非 ASCII 字符原样输出，便于用户直接查看资源包内容
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 序列化为 1.1-1.5 key=value 语言文件。
    /// </summary>
    public static string SerializeLangLegacy(Dictionary<string, string> map)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            builder.Append(key).Append('=').Append(map[key]).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// 将语言键分块，每块不超过 maxKeys 个，便于分批调用模型翻译。
    /// </summary>
    public static List<Dictionary<string, string>> Chunk(Dictionary<string, string> map, int maxKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeys, 1);
        var result = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            if (current.Count >= maxKeys)
            {
                result.Add(current);
                current = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            current[key] = value;
        }
        if (current.Count > 0)
            result.Add(current);
        return result;
    }
}
