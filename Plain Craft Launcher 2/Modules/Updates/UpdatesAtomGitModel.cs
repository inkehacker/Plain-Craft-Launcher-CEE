using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// CEE 自研版本的国内镜像更新源：从 AtomGit（gitcode）仓库 2503_93182279/Plain-Craft-Launcher-CEE 的
/// Versions 分支读取发布文件（Plain Craft Launcher CEE x.y.z.zip / .exe），与 GitHub 源内容一致。
/// AtomGit 即 gitcode，提供 gitee v5 风格 API（/api/v5/repos/.../contents），JSON 字段与 GitHub 兼容。
/// </summary>
public class UpdatesAtomGitModel : IUpdateSource
{
    private const string RepositoryOwner = "2503_93182279";
    private const string RepositoryName = "Plain-Craft-Launcher-CEE";
    private const string BranchName = "Versions";
    private const string FilePattern = @"^Plain Craft Launcher CEE (\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.\-]+))?\.(zip|exe)$";

    private readonly string _listUrl =
        $"https://atomgit.com/api/v5/repos/{RepositoryOwner}/{RepositoryName}/contents?ref={BranchName}";

    // 下载走 contents API（base64）：实测 atomgit/gitcode 的 raw、blob、archive 端点均不返回文件内容，
    // 只有 /api/v5/.../contents?ref= 会把文件以 base64 完整返回（已验证 10MB 文件完整无截断）
    private static string BuildContentUrl(string fileName) =>
        $"https://atomgit.com/api/v5/repos/{RepositoryOwner}/{RepositoryName}/contents/{Uri.EscapeDataString(fileName)}?ref={BranchName}";

    private List<CeeRemoteFile> _remoteFiles;

    public string SourceName { get; set; } = "AtomGit";

    public bool IsAvailable()
    {
        return true;
    }

    public bool RefreshCache()
    {
        // 镜像源快速失败：不可达时 ~8 秒内放弃，交由更新系统回退其他源，避免页面长时间无响应
        var json = ModBase.GetJson(Requester.FetchString(_listUrl, new RequestParam { Timeout = 8000, Retries = 1 }));
        var files = new List<CeeRemoteFile>();
        foreach (var node in json.AsArray())
        {
            if (node?["type"]?.GetValue<string>() != "file")
                continue;
            var name = node["name"]?.GetValue<string>();
            if (name is null)
                continue;
            var match = Regex.Match(name, FilePattern);
            if (!match.Success)
                continue;
            files.Add(new CeeRemoteFile(
                new SemVer(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value), match.Groups[4].Value),
                name,
                string.Equals(match.Groups[5].Value, "zip", StringComparison.OrdinalIgnoreCase),
                BuildContentUrl(name)));
        }

        _remoteFiles = files;
        return files.Count > 0;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var latest = GetLatestFile();
        return new VersionDataModel
        {
            VersionName = latest.Version.ToString(),
            // 镜像只提供 git blob 哈希而不提供内容 SHA256，更新时跳过哈希校验
            Sha256 = null,
            Source = SourceName,
            Changelog = string.Empty,
            VersionCode = 0
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        return currentVersion >= GetLatestFile().Version;
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // 镜像源不提供公告，抛异常交由更新系统回退到官方源
        throw new NotSupportedException("AtomGit 镜像更新源不提供公告");
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var latest = GetLatestFile();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\{latest.Name}";
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, string>(Lang.Text("Update.Task.DownloadFile"), load =>
        {
            // contents API 返回 base64 编码的文件内容，解码后写入（请求体较大，超时放宽）
            var json = ModBase.GetJson(Requester.FetchString(latest.DownloadUrl,
                new RequestParam { Timeout = 120000, Retries = 3 }));
            var content = json["content"]?.GetValue<string>()
                          ?? throw new Exception(Lang.Text("Update.Error.FileNotFound"));
            ModBase.WriteFile(tempPath, Convert.FromBase64String(content));
        }));
        loaders.Add(new ModLoader.LoaderTask<string, int>(Lang.Text("Update.Task.ApplyFile"), _ =>
        {
            if (latest.IsZip)
            {
                using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new ZipArchive(fs))
                {
                    var entry = zip.Entries
                        .FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                        throw new Exception(Lang.Text("Update.Error.FileNotFound"));
                    entry.ExtractToFile(output, true);
                }
            }
            else
            {
                ModBase.CopyFile(tempPath, output);
            }
        }));
        return loaders;
    }

    private CeeRemoteFile GetLatestFile()
    {
        if (_remoteFiles is null)
            RefreshCache();
        return _remoteFiles?.OrderByDescending(x => x.Version).FirstOrDefault()
               ?? throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
    }

    private record CeeRemoteFile(SemVer Version, string Name, bool IsZip, string DownloadUrl);
}
