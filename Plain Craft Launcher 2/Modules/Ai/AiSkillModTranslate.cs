using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Translation;

namespace PCL;

/// <summary>
/// 翻译 mod 技能：将当前实例的 mod 语言文件批量翻译为简体中文，
/// 输出单个资源包 zip（assets/&lt;命名空间&gt;/lang/zh_cn.json），不改动 mod 本体。
/// </summary>
public static class AiSkillModTranslate
{
    public static (AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler) CreateTool() =>
        (definition, ExecuteAsync);

    private static readonly AiToolDefinition definition = new()
    {
        Name = "translate_mods",
        Description = "翻译当前实例的 mod（模组）语言文件为简体中文，生成资源包。默认翻译所有没有中文翻译的 mod；可用 mods 参数指定只翻译部分 mod（值为 jar 文件名，如 [\"jei-1.20.1.jar\"]）。已包含中文翻译的 mod 会被跳过。",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mods"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "可选：只翻译这些 jar 文件名对应的 mod",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            }
        }
    };

    public static async Task<string> ExecuteAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));
        var modsDir = Path.Combine(instance.PathIndie, "mods");
        if (!Directory.Exists(modsDir))
            throw new Exception(Lang.Text("Tools.Ai.ModTranslate.NoModsFolder"));

        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (args["mods"] is JsonArray modsArray)
            foreach (var item in modsArray)
                if (item is not null)
                    filter.Add(item.GetValue<string>());

        var client = ModAi.CreateClient();
        var resultByNamespace = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var translatedModCount = 0;
        var skippedCount = 0;
        var totalKeys = 0;

        foreach (var jarFile in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(jarFile);
            if (filter.Count > 0 && !filter.Contains(fileName))
                continue;

            var enNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var zhNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var archive = ZipFile.OpenRead(jarFile);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    var ns = _NamespaceFromLangEntry(name, "en_us.json");
                    if (ns is not null)
                        enNamespaces.Add(ns);
                    ns = _NamespaceFromLangEntry(name, "zh_cn.json");
                    if (ns is not null)
                        zhNamespaces.Add(ns);
                }
            }
            catch (InvalidDataException)
            {
                continue; // 损坏的 jar 直接跳过
            }

            var pendingNamespaces = enNamespaces.Except(zhNamespaces, StringComparer.OrdinalIgnoreCase).ToList();
            if (pendingNamespaces.Count == 0)
            {
                if (enNamespaces.Count > 0)
                    skippedCount++;
                continue;
            }

            foreach (var ns in pendingNamespaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var enMap = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var archive = ZipFile.OpenRead(jarFile))
                {
                    var entry = archive.GetEntry($"assets/{ns}/lang/en_us.json");
                    if (entry is null)
                        continue;
                    using var reader = new StreamReader(entry.Open());
                    enMap = McLangFile.Parse(reader.ReadToEnd(), McLangFormat.Json);
                }
                if (enMap.Count == 0)
                    continue;

                var chunks = McLangFile.Chunk(enMap, 150);
                var translated = await ModAi.TranslateChunksAsync(client, chunks, null, cancellationToken).ConfigureAwait(false);
                if (!resultByNamespace.TryGetValue(ns, out var target))
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal);
                    resultByNamespace[ns] = target;
                }
                foreach (var (key, value) in translated)
                    target[key] = value;

                totalKeys += translated.Count;
                translatedModCount++;
            }
        }

        if (translatedModCount == 0)
            return Lang.Text("Tools.Ai.ModTranslate.Nothing", skippedCount);

        var vanillaName = instance.Info.VanillaName ?? "";
        var packFormat = McResourcePackBuilder.PackFormatForVanillaVersion(vanillaName);
        var resourcePacksDir = Path.Combine(instance.PathIndie, "resourcepacks");
        Directory.CreateDirectory(resourcePacksDir);
        var packName = $"PCL-Mod翻译-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        McResourcePackBuilder.BuildToZip(Path.Combine(resourcePacksDir, packName), packFormat,
            "PCL CE Mod 翻译", resultByNamespace);

        return Lang.Text("Tools.Ai.ModTranslate.Success", translatedModCount, totalKeys, skippedCount, packName);
    }

    /// <summary>
    /// 从 jar 内路径提取语言文件所在命名空间；不匹配时返回 null。
    /// </summary>
    private static string? _NamespaceFromLangEntry(string entryName, string langFile)
    {
        const string assetsPrefix = "assets/";
        if (!entryName.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var suffix = "/lang/" + langFile;
        if (!entryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        var ns = entryName[assetsPrefix.Length..^suffix.Length];
        return ns.Contains('/') ? null : ns;
    }
}
