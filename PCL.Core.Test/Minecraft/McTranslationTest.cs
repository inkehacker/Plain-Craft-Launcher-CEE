using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Keybind;
using PCL.Core.Minecraft.Translation;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class McTranslationTest
{
    [TestMethod]
    public void ParseJsonLang()
    {
        var map = McLangFile.Parse("""{"a.b":"你好","c.d":"世界 %s"}""", McLangFormat.Json);
        Assert.AreEqual(2, map.Count);
        Assert.AreEqual("你好", map["a.b"]);
        Assert.AreEqual("世界 %s", map["c.d"]);
    }

    [TestMethod]
    public void ParseLegacyLang()
    {
        var map = McLangFile.Parse("# comment\na.b=你好\nc.d=世界 %s\nbadline", McLangFormat.LangLegacy);
        Assert.AreEqual(2, map.Count);
        Assert.AreEqual("你好", map["a.b"]);
        Assert.AreEqual("世界 %s", map["c.d"]);
    }

    [TestMethod]
    public void SerializeRoundTrip()
    {
        var map = new System.Collections.Generic.Dictionary<string, string> { ["b.b"] = "\"引号\"", ["a.a"] = "值\n换行" };
        var json = McLangFile.SerializeJson(map);
        var parsed = McLangFile.Parse(json, McLangFormat.Json);
        Assert.AreEqual(map["b.b"], parsed["b.b"]);
        Assert.AreEqual(map["a.a"], parsed["a.a"]);
    }

    [TestMethod]
    public void ChunkSplitsByMaxKeys()
    {
        var map = new System.Collections.Generic.Dictionary<string, string>
        {
            ["a"] = "1", ["b"] = "2", ["c"] = "3", ["d"] = "4", ["e"] = "5"
        };
        var chunks = McLangFile.Chunk(map, 2);
        Assert.AreEqual(3, chunks.Count);
        Assert.AreEqual(2, chunks[0].Count);
        Assert.AreEqual(2, chunks[1].Count);
        Assert.AreEqual(1, chunks[2].Count);
        Assert.AreEqual(5, chunks.Sum(c => c.Count));
    }

    [TestMethod]
    public void FormatFromPath()
    {
        Assert.AreEqual(McLangFormat.Json, McLangFile.FormatFromPath("assets/minecraft/lang/en_us.json"));
        Assert.AreEqual(McLangFormat.LangLegacy, McLangFile.FormatFromPath("lang/en_US.lang"));
    }

    [TestMethod]
    public void KeybindValidValues()
    {
        Assert.IsTrue(KeybindValidator.IsValidBindingName("key_key.sneak"));
        Assert.IsTrue(KeybindValidator.IsValidBindingName("key_key.hotbar.1"));
        Assert.IsFalse(KeybindValidator.IsValidBindingName("sneak"));
        Assert.IsFalse(KeybindValidator.IsValidBindingName("key_key.sneak;drop"));
        Assert.IsTrue(KeybindValidator.IsValidKeyValue("key.keyboard.left.control"));
        Assert.IsTrue(KeybindValidator.IsValidKeyValue("key.keyboard.f"));
        Assert.IsTrue(KeybindValidator.IsValidKeyValue("key.mouse.button4"));
        Assert.IsFalse(KeybindValidator.IsValidKeyValue("key.keyboard.notarealkey"));
        Assert.IsFalse(KeybindValidator.IsValidKeyValue("left.control"));
        Assert.IsNull(KeybindValidator.ValidateChange("key_key.sneak", "key.keyboard.left.control"));
        Assert.IsNotNull(KeybindValidator.ValidateChange("key_key.sneak", "key.keyboard.badkey"));
    }

    [TestMethod]
    public void PackFormatMapping()
    {
        Assert.AreEqual(1, McResourcePackBuilder.PackFormatForVanillaVersion("1.8.9"));
        Assert.AreEqual(3, McResourcePackBuilder.PackFormatForVanillaVersion("1.12.2"));
        Assert.AreEqual(13, McResourcePackBuilder.PackFormatForVanillaVersion("1.19.4"));
        Assert.AreEqual(18, McResourcePackBuilder.PackFormatForVanillaVersion("1.20.2"));
        Assert.AreEqual(34, McResourcePackBuilder.PackFormatForVanillaVersion("1.21.1"));
        Assert.AreEqual(42, McResourcePackBuilder.PackFormatForVanillaVersion("1.21.2-Fabric_0.100"));
        Assert.AreEqual(55, McResourcePackBuilder.PackFormatForVanillaVersion("1.21.5"));
    }

    [TestMethod]
    public void BuildResourcePackZip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PCLTest", "ResPack", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, "test.zip");
            var minecraft = new System.Collections.Generic.Dictionary<string, string> { ["menu.game"] = "游戏" };
            var modA = new System.Collections.Generic.Dictionary<string, string> { ["item.a.name"] = "物品A" };
            McResourcePackBuilder.BuildToZip(zipPath, 34, "测试", new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
            {
                ["minecraft"] = minecraft, ["moda"] = modA
            });

            using var archive = ZipFile.OpenRead(zipPath);
            Assert.IsNotNull(archive.GetEntry("pack.mcmeta"));
            using var reader = new StreamReader(archive.GetEntry("assets/minecraft/lang/zh_cn.json")!.Open());
            var json = reader.ReadToEnd();
            Assert.IsTrue(json.Contains("menu.game"));
            Assert.IsTrue(json.Contains("游戏"));
            Assert.IsNotNull(archive.GetEntry("assets/moda/lang/zh_cn.json"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
