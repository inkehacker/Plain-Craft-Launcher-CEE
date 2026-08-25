using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PCL.Core.Minecraft;

public class GameCore
{
    private readonly string _corePath;
    public GameCore(string corePath)
    {
        if (!File.Exists(corePath)) throw new FileNotFoundException($"未找到指定文件：{corePath}");
        this._corePath = corePath;
    }
    /// <summary>
    /// 将指定的 Jar 文件添加到到游戏核心
    /// </summary>
    /// <param name="jarPath">要添加到 Jar 的文件</param>
    /// <exception cref="FileNotFoundException">提供的文件路径不存在</exception>
    public void AddToCore(string jarPath)
    {
        if (!File.Exists(jarPath)) throw new FileNotFoundException($"未找到指定文件：{jarPath}");
        using var coreStream = new FileStream(_corePath,FileMode.Open,FileAccess.ReadWrite,FileShare.Read,16384,true);
        using var jarStream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, true);
        using var coreArchive = new ZipArchive(coreStream,ZipArchiveMode.Update);
        using var jarArchive = new ZipArchive(jarStream);
        // Better Than Wolves 的 Mod File 是 .zip 结尾的
        var filter = jarPath.EndsWith(".jar") ? "" : "MINECRAFT-JAR";
        foreach (var entry in jarArchive.Entries)
        {
            if (!entry.FullName.Contains(filter)) continue;
            // 核心 jar 中可能已存在同名条目（如 MANIFEST.MF、net/minecraft 类），
            // Update 模式下重复 CreateEntry 会抛 ArgumentException，先删除旧条目
            coreArchive.GetEntry(entry.FullName)?.Delete();
            using var coreArchiveStream = coreArchive.CreateEntry(entry.FullName).Open();
            using var jarArchiveStream = jarArchive.GetEntry(entry.FullName)?.Open();
            jarArchiveStream?.CopyTo(coreArchiveStream);
        }
        // 删除 META-INF 下的签名文件，避免 Oracle JDK 加载时验证签名失败导致无法启动
        foreach (var signature in coreArchive.Entries
                     .Where(entry => entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                                     && (entry.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
                                         || entry.FullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                                         || entry.FullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)))
                     .ToList())
            signature.Delete();
    }
}
