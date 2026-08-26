using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Translation;

namespace PCL;

/// <summary>
/// 翻译核心技能：将当前实例游戏核心的语言文件翻译为简体中文。
/// 常用于远古版本（无中文翻译）、bug 版本或愚人节版本。
/// 1.6+ 输出资源包 zip（不改动核心 jar）；1.1-1.5 直接写入 jar 内 lang 文件；
/// 更早版本（无语言文件机制）返回不支持说明。
/// </summary>
public static class AiSkillCoreTranslate
{
    public static (AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler) CreateTool() =>
        (definition, ExecuteAsync);

    private static readonly AiToolDefinition definition = new()
    {
        Name = "translate_core",
        Description = "翻译当前实例的游戏核心（原版 Minecraft 本体）的语言文件为简体中文。适用于远古版本（1.1-1.5 无中文翻译）、bug 版本、愚人节版本（如 3D Shareware、22w13oneBlockAtATime）等没有可用中文翻译的情况。1.6 及以上版本会生成资源包（不改动核心文件），1.1-1.5 会直接写入版本 jar。",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        }
    };

    public static async Task<string> ExecuteAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var instance = ModInstanceList.McMcInstanceSelected
                       ?? throw new Exception(Lang.Text("Tools.Ai.Error.NoInstance"));

        var coreJarPath = instance.PathInstance + instance.Name + ".jar";
        if (!File.Exists(coreJarPath))
            throw new Exception(Lang.Text("Tools.Ai.CoreTranslate.NoCoreJar", coreJarPath));

        // 收集核心 jar 内的语言文件
        string? enEntry = null;
        string? zhEntry = null;
        using (var archive = ZipFile.OpenRead(coreJarPath))
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.Equals("assets/minecraft/lang/en_us.json", StringComparison.OrdinalIgnoreCase))
                    enEntry = entry.FullName;
                else if (name.Equals("assets/minecraft/lang/zh_cn.json", StringComparison.OrdinalIgnoreCase))
                    zhEntry = entry.FullName;
                else if (name.Equals("lang/en_US.lang", StringComparison.OrdinalIgnoreCase))
                    enEntry = entry.FullName;
                else if (name.Equals("lang/zh_CN.lang", StringComparison.OrdinalIgnoreCase))
                    zhEntry = entry.FullName;
            }
        }
        if (enEntry is null)
            return Lang.Text("Tools.Ai.CoreTranslate.Unsupported");

        var format = McLangFile.FormatFromPath(enEntry);
        var isLegacy = format == McLangFormat.LangLegacy;

        using (var archive = ZipFile.OpenRead(coreJarPath))
        {
            var enContent = new StreamReader(archive.GetEntry(enEntry)!.Open()).ReadToEnd();
            var enMap = McLangFile.Parse(enContent, format);
            if (enMap.Count == 0)
                return Lang.Text("Tools.Ai.CoreTranslate.Empty");

            // 已有中文翻译时只补缺失词条
            var zhMap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (zhEntry is not null)
            {
                var zhContent = new StreamReader(archive.GetEntry(zhEntry)!.Open()).ReadToEnd();
                zhMap = McLangFile.Parse(zhContent, format);
            }
            var pending = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in enMap)
                if (!zhMap.ContainsKey(key))
                    pending[key] = value;
            if (pending.Count == 0)
                return Lang.Text("Tools.Ai.CoreTranslate.AlreadyTranslated");

            var client = ModAi.CreateClient();
            var chunks = McLangFile.Chunk(pending, 150);
            var translated = await ModAi.TranslateChunksAsync(client, chunks, null, cancellationToken).ConfigureAwait(false);

            foreach (var (key, value) in zhMap)
                translated.TryAdd(key, value);

            var vanillaName = instance.Info.VanillaName ?? "";

            if (isLegacy)
            {
                // 1.1-1.5：直接写入版本 jar（先备份）
                var backupPath = coreJarPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(coreJarPath, backupPath, true);
                WriteLegacyLangIntoJar(coreJarPath, translated);
                return Lang.Text("Tools.Ai.CoreTranslate.SuccessJar", translated.Count - zhMap.Count,
                    Path.GetFileName(coreJarPath), Path.GetFileName(backupPath));
            }

            var packFormat = McResourcePackBuilder.PackFormatForVanillaVersion(vanillaName);
            var resourcePacksDir = Path.Combine(instance.PathIndie, "resourcepacks");
            Directory.CreateDirectory(resourcePacksDir);
            var packName = $"PCL-核心翻译-{vanillaName}.zip";
            var zipPath = Path.Combine(resourcePacksDir, packName);
            McResourcePackBuilder.BuildToZip(zipPath, packFormat,
                $"PCL CE 核心翻译 {vanillaName}", new Dictionary<string, Dictionary<string, string>> { ["minecraft"] = translated });

            return Lang.Text("Tools.Ai.CoreTranslate.SuccessPack", translated.Count - zhMap.Count, packName);
        }
    }

    private static void WriteLegacyLangIntoJar(string jarPath, Dictionary<string, string> lang)
    {
        using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
        archive.GetEntry("lang/zh_CN.lang")?.Delete();
        var entry = archive.CreateEntry("lang/zh_CN.lang", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(McLangFile.SerializeLangLegacy(lang));
    }
}
