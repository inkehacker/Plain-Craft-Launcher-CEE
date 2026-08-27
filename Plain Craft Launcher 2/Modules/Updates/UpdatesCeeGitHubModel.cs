using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// CEE 自研版本的更新源：从 GitHub 仓库 inkehacker/Plain-Craft-Launcher-CEE 的
/// Versions 分支读取发布文件（Plain Craft Launcher CEE x.y.z.zip / .exe），
/// 取文件名中最高版本作为最新版本。只发布 x64 包，故忽略通道与架构参数。
/// </summary>
public class UpdatesCeeGitHubModel : IUpdateSource
{
    private const string RepositoryOwner = "inkehacker";
    private const string RepositoryName = "Plain-Craft-Launcher-CEE";
    private const string BranchName = "Versions";
    private const string FilePattern = @"^Plain Craft Launcher CEE (\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.\-]+))?\.(zip|exe)$";

    private readonly string _listUrl =
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/contents?ref={BranchName}";

    private List<CeeRemoteFile> _remoteFiles;

    public string SourceName { get; set; } = "CEE GitHub";

    public bool IsAvailable()
    {
        return true;
    }

    public bool RefreshCache()
    {
        var json = ModBase.GetJson(Requester.FetchString(_listUrl, RequestParam.WithRetry));
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
                node["download_url"]?.GetValue<string>()));
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
            // GitHub 只提供 git blob 哈希而不提供内容 SHA256，更新时跳过哈希校验
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
        // 自定义源不提供公告，抛异常交由更新系统回退到官方源
        throw new NotSupportedException("CEE GitHub 更新源不提供公告");
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var latest = GetLatestFile();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\{latest.Name}";
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetVersionInfo"), load =>
        {
            load.output = new List<DownloadFile> { new(new[] { latest.DownloadUrl }, tempPath) };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), new List<DownloadFile>()));
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
