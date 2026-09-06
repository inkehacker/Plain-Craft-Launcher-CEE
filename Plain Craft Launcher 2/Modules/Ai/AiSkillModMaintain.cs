using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.AI;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

// ModComp 内的嵌套类型，用别名保持代码可读
using CompLoaderType = PCL.ModComp.CompLoaderType;
using CompFileStatus = PCL.ModComp.CompFileStatus;
using CompFile = PCL.ModComp.CompFile;
using CompProject = PCL.ModComp.CompProject;
using CompType = PCL.ModComp.CompType;

/// <summary>
/// Mod 维护技能：查看实例与 Mod、搜索资源、查询版本、安装/更新 Mod（可指定版本目标）。
/// 安装与更新会向实例写入文件，标记为敏感（执行前 ModAi 会弹确认框征求用户同意）。
/// 工具结果以纯文本返回，由模型组织语言汇报。
/// </summary>
public static class AiSkillModMaintain
{
    private static readonly JsonObject emptyParams = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray()
    };

    public static IReadOnlyList<(AiToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Handler)>
        CreateTools() =>
    [
        (new AiToolDefinition
        {
            Name = "list_instances",
            Description = "列出启动器内全部游戏实例的名称、MC 版本与加载器概要。后续 Mod 操作若用户未指定实例，可先用此工具确认实例名。",
            Parameters = emptyParams
        }, ListInstancesAsync),
        (new AiToolDefinition
        {
            Name = "list_mods",
            Description = "列出指定实例 mods 目录下的全部文件名（含 .disabled）与总数，用于了解已装 Mod、排查冲突。instance 缺省时为当前选中实例。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["instance"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "实例名称（用 list_instances 的结果）。缺省用当前选中实例。"
                    }
                },
                ["required"] = new JsonArray()
            }
        }, ListModsAsync),
        (new AiToolDefinition
        {
            Name = "search_mods",
            Description = "在 CurseForge 与 Modrinth 上按名称搜索 Mod 项目，返回项目 ID、来源、简介等（最多约 12 个）。结果可直接作为 install_mod / update_mod / mod_versions 的 projectId + source。loader / gameVersion 可预筛（如 loader=fabric、gameVersion=26.2）。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "要搜索的 Mod 名称关键词"
                    },
                    ["loader"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("forge", "fabric", "neoforge", "quilt", "liteloader"),
                        ["description"] = "加载器筛选（可选）"
                    },
                    ["gameVersion"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "MC 版本筛选（可选），例如 26.2 或 1.21.8"
                    }
                },
                ["required"] = new JsonArray("query")
            }
        }, SearchModsAsync),
        (new AiToolDefinition
        {
            Name = "mod_versions",
            Description = "列出某 Mod 项目的全部可用版本文件：版本号、MC 版本、加载器、状态与发布日期（最多 30 条）。查看某版本是否存在、或为指定版本安装/更新前可先调用。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "项目 ID（search_mods 返回；纯数字=CurseForge，乱码=Modrinth）"
                    },
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("modrinth", "curseforge"),
                        ["description"] = "项目来源（可选，缺省按 ID 自动判断）"
                    }
                },
                ["required"] = new JsonArray("projectId")
            }
        }, ModVersionsAsync),
        (new AiToolDefinition
        {
            Name = "install_mod",
            Description = "把指定 Mod 项目安装到目标实例的 mods 目录：自动挑选适配该实例 MC 版本与加载器的最新版本；targetVersion 可指定目标（可填该 Mod 适配的 MC 版本如 26.2，或版本号关键词）。执行前会请求用户确认。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["instance"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "实例名称（用 list_instances 的结果）。缺省用当前选中实例。"
                    },
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "项目 ID（search_mods 返回；纯数字=CurseForge，乱码=Modrinth）"
                    },
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("modrinth", "curseforge"),
                        ["description"] = "项目来源（可选，缺省按 ID 自动判断）"
                    },
                    ["targetVersion"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标版本（可选）：优先按适配 MC 版本匹配，再按版本号关键词匹配"
                    }
                },
                ["required"] = new JsonArray("projectId")
            },
            Sensitive = true
        }, InstallModAsync),
        (new AiToolDefinition
        {
            Name = "update_mod",
            Description = "把目标实例中已安装的某 Mod 更新到最新版（或 targetVersion 指定版本）。会先通过文件哈希确认实例中确有该项目再执行；若实例中没有该项目会明确告知。执行前会请求用户确认。",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["instance"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "实例名称（用 list_instances 的结果）。缺省用当前选中实例。"
                    },
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "项目 ID（search_mods 返回；纯数字=CurseForge，乱码=Modrinth）"
                    },
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("modrinth", "curseforge"),
                        ["description"] = "项目来源（可选，缺省按 ID 自动判断）"
                    },
                    ["targetVersion"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标版本（可选）：优先按适配 MC 版本匹配，再按版本号关键词匹配；不填则更新到最新"
                    }
                },
                ["required"] = new JsonArray("projectId")
            },
            Sensitive = true
        }, UpdateModAsync)
    ];

    #region 工具入口（全部在后台线程执行，避免阻塞 UI）

    private static Task<string> ListInstancesAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => ListInstancesCoreAsync(t), ct);

    private static Task<string> ListModsAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => ListModsCoreAsync(args, t), ct);

    private static Task<string> SearchModsAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => SearchModsCoreAsync(args, t), ct);

    private static Task<string> ModVersionsAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => ModVersionsCoreAsync(args, t), ct);

    private static Task<string> InstallModAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => InstallModCoreAsync(args, t), ct);

    private static Task<string> UpdateModAsync(JsonObject args, CancellationToken ct) =>
        _RunBackgroundAsync(t => UpdateModCoreAsync(args, t), ct);

    private static Task<string> _RunBackgroundAsync(Func<CancellationToken, Task<string>> work, CancellationToken ct)
    {
        // 处理器可能在 UI 线程同步起步，先切到线程池再执行（下载/网络可能耗时数秒）
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => work(ct), ct);
    }

    #endregion

    #region 通用辅助

    private const int MaxResultChars = 4000;

    /// <summary>实例 → mods 目录（含 LabyMod 分支，与资源管理页算法一致）。</summary>
    private static string _ModsFolder(McInstance instance) =>
        Path.Combine(instance.PathIndie,
                instance.Info.HasLabyMod
                    ? Path.Combine("labymod-neo", "fabric", instance.Info.VanillaName)
                    : "",
                ModLocalComp.GetPathNameByCompType(CompType.Mod)) +
            Path.DirectorySeparatorChar;

    private static string _LoaderSummary(McInstanceInfo info) => string.Join("+", new[]
    {
        (info.HasForge, "Forge"),
        (info.HasCleanroom, "Cleanroom"),
        (info.HasNeoForge, "NeoForge"),
        (info.HasFabric, "Fabric"),
        (info.HasLegacyFabric, "LegacyFabric"),
        (info.HasQuilt, "Quilt"),
        (info.HasLiteLoader, "LiteLoader"),
        (info.HasLabyMod, "LabyMod")
    }.Where(x => x.Item1).Select(x => x.Item2));

    /// <summary>解析 instance 参数：未指定用当前选中实例；指定了但找不到则给出可用实例名列表。</summary>
    private static McInstance _ResolveInstance(JsonObject args, out string error)
    {
        error = "";
        var wanted = args["instance"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            var current = ModInstanceList.McMcInstanceSelected;
            if (current is null)
                error = "未指定实例，且当前没有选中的实例。请先用 list_instances 工具查看可用实例，再指定 instance 参数。";
            if (!current.IsLoaded)
            {
                try
                {
                    current.Load();
                }
                catch
                {
                    // Load 内部会记日志，这里只需兜底
                }
            }
            if (current.state == McInstanceState.Error)
            {
                error = "当前选中的实例读取失败，无法进行操作。请先用 list_instances 工具查看可用实例。";
                return null!;
            }
            return current;
        }

        // 名称匹配（忽略大小写）
        var names = _ListInstanceNames();
        var name = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            var sample = names.Take(20).Select(n => "- " + n);
            error = $"找不到名为「{wanted}」的实例。可用实例：\n" + string.Join("\n", sample);
            return null!;
        }

        var instance = new McInstance(name);
        if (!instance.IsLoaded)
        {
            try
            {
                instance.Load();
            }
            catch
            {
                // Load 内部会记日志，这里只需兜底
            }
        }
        if (instance.state == McInstanceState.Error)
        {
            error = $"实例「{name}」读取失败，无法进行操作。请先用 list_instances 工具查看可用实例。";
            return null!;
        }
        return instance;
    }

    private static List<string> _ListInstanceNames()
    {
        var versionsRoot = Path.Combine(ModFolder.mcFolderSelected, "versions");
        try
        {
            if (!Directory.Exists(versionsRoot))
                return [];
            return Directory.EnumerateDirectories(versionsRoot)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取实例列表失败");
            return [];
        }
    }

    private static (bool FromCurseForge, string Id) _ProjectKey(JsonObject args, out string error)
    {
        error = "";
        var id = args["projectId"]?.GetValue<string>()?.Trim() ?? "";
        if (id.Length == 0)
        {
            error = "缺少 projectId 参数";
            return (false, "");
        }
        var source = args["source"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var fromCf = source switch
        {
            "curseforge" => true,
            "modrinth" => false,
            _ => ModComp.CompRequest.IsFromCurseForge(id)
        };
        return (fromCf, id);
    }

    private static CompLoaderType _LoaderFromText(string text)
    {
        var loader = text.Trim().ToLowerInvariant();
        return loader switch
        {
            "forge" => CompLoaderType.Forge,
            "liteloader" => CompLoaderType.LiteLoader,
            "fabric" => CompLoaderType.Fabric,
            "quilt" => CompLoaderType.Quilt,
            "neoforge" => CompLoaderType.NeoForge,
            _ => CompLoaderType.Any
        };
    }

    /// <summary>文件允许的加载器：文件自声明优先，缺失回退工程加载器（与 ModComp 页面一致）。</summary>
    private static List<CompLoaderType> _FileLoaders(CompFile file, CompProject project) =>
        file.ModLoaders.Count > 0 ? file.ModLoaders : project.ModLoaders;

    private static List<CompFile> _SuitableFiles(McInstance instance, CompProject project, List<CompFile> files) =>
        files.Where(f => f.Available && f.Type == CompType.Mod &&
                         ModComp.IsInstanceSuitableForFile(instance, f, _FileLoaders(f, project))).ToList();

    /// <summary>挑选最新文件：优先 Release，其次按发布日期（与 ModComp._PickLatestFile 一致）。</summary>
    private static CompFile? _PickLatest(List<CompFile> files)
    {
        if (files.Count == 0)
            return null;
        return files
            .OrderByDescending(f => f.Status == CompFileStatus.Release)
            .ThenByDescending(f => f.ReleaseDate)
            .First();
    }

    /// <summary>按目标版本挑文件：先按 MC 版本精确匹配，再按版本号/文件名关键词匹配。</summary>
    private static CompFile? _PickByTarget(List<CompFile> files, string? targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
            return _PickLatest(files);
        var t = targetVersion.Trim();
        var byGame = files.Where(f => f.GameVersions.Any(v =>
            string.Equals(v, t, StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith(t + " ", StringComparison.OrdinalIgnoreCase))).ToList();
        if (byGame.Count > 0)
            return _PickLatest(byGame);
        var byName = files.Where(f =>
        {
            var lower = t.ToLowerInvariant();
            return (f.Version?.Contains(lower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (f.DisplayName?.Contains(lower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (f.FileName?.Contains(lower, StringComparison.OrdinalIgnoreCase) ?? false);
        }).ToList();
        if (byName.Count > 0)
            return _PickLatest(byName);
        return null;
    }

    private static string _VersionLabel(CompFile f) =>
        f.FromCurseForge
            ? f.DisplayName ?? f.FileName ?? ""
            : f.Version ?? f.DisplayName ?? f.FileName ?? "";

    private static string _VersionLine(CompFile f)
    {
        var sb = new StringBuilder();
        sb.Append("- ").Append(_VersionLabel(f));
        if (f.FromCurseForge && f.DisplayName != f.FileName)
            sb.Append("（文件 ").Append(f.FileName).Append("）");
        if (f.Status != CompFileStatus.Release)
            sb.Append(" [").Append(f.Status).Append("]");
        if (f.GameVersions.Count > 0)
            sb.Append(" MC ").Append(string.Join("、", f.GameVersions.Distinct().Take(8)));
        if (f.ModLoaders.Count > 0)
            sb.Append(" · ").Append(string.Join("、", f.ModLoaders));
        sb.Append(" · ").Append(f.ReleaseDate.ToString("yyyy-MM-dd"));
        return sb.ToString();
    }

    private static string _Cap(string text)
    {
        if (text.Length <= MaxResultChars)
            return text;
        var cut = text[..MaxResultChars];
        var lastNewLine = cut.LastIndexOf('\n');
        return (lastNewLine > 0 ? cut[..lastNewLine] : cut) + "\n…（内容过长已截断）";
    }

    /// <summary>下载单个文件到目标路径并等待完成（后台线程调用）。返回 null 表示成功。</summary>
    private static async Task<string?> _DownloadFileAsync(string title, CompProject project, CompFile file,
        string targetPath, CancellationToken ct)
    {
        // 与 ModComp._StartQuickDownload 一致：走 LoaderDownload + LoaderCombo，hash 由 FileChecker 校验
        var loader = new ModLoader.LoaderCombo<int>(title, new List<ModLoader.LoaderBase>
        {
            new LoaderDownload(title, new List<DownloadFile>
            {
                file.ToNetFile(targetPath, ModComp.DownloadReason.Standalone,
                    file.GameVersions.FirstOrDefault(), _FileLoaders(file, project).FirstOrDefault())
            })
            {
                ProgressWeight = 6,
                block = true
            }
        });
        loader.Start(1);
        try
        {
            // LoaderCombo 异步执行，轮询等待结束（可取消、可超时）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (loader.State is not (ModBase.LoadState.Finished or ModBase.LoadState.Failed or ModBase.LoadState.Aborted))
            {
                ct.ThrowIfCancellationRequested();
                if (sw.ElapsedMilliseconds > 15 * 60 * 1000)
                {
                    loader.Abort();
                    return "下载超时（超过 15 分钟），已中止";
                }
                await Task.Delay(200, ct);
            }
            if (loader.State != ModBase.LoadState.Finished)
            {
                var reason = loader.Error?.Message is { Length: > 0 } m ? m : loader.State.ToString();
                return "下载失败：" + reason;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            loader.Abort();
            throw;
        }
        finally
        {
            // 交给任务管理器继续跟踪进度
            if (ModMain.frmMain is not null)
                ModLoader.LoaderTaskbarAdd(loader);
        }
    }

    /// <summary>无可用文件时给出解释与样例列表。</summary>
    private static string _NoSuitableText(McInstance instance, CompProject project, List<CompFile> files,
        string? targetVersion)
    {
        var sb = new StringBuilder();
        sb.Append("没有找到适用于实例「").Append(instance.Name)
            .Append("」（MC ").Append(instance.Info.VanillaName)
            .Append(_LoaderSummary(instance.Info) is { Length: > 0 } l ? "，" + l : "")
            .Append("）的 Mod 文件。");
        if (!string.IsNullOrWhiteSpace(targetVersion))
            sb.Append("目标版本「").Append(targetVersion.Trim()).Append("」与该实例不匹配。");
        var available = files.Where(f => f.Available).ToList();
        if (available.Count > 0)
        {
            sb.Append(" 该项目最近的可用版本示例：\n");
            foreach (var line in available
                         .OrderByDescending(f => f.Status == CompFileStatus.Release)
                         .ThenByDescending(f => f.ReleaseDate)
                         .Take(8)
                         .Select(_VersionLine))
                sb.Append(line).Append('\n');
        }
        else
        {
            sb.Append(" 该项目当前没有可下载的文件（可能已下架）。");
        }
        return sb.ToString();
    }

    #endregion

    #region 工具实现

    private static async Task<string> ListInstancesCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var names = _ListInstanceNames();
        if (names.Count == 0)
            return "（启动器内没有找到任何游戏实例）";
        var sb = new StringBuilder($"共 {names.Count} 个实例：");
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var instance = new McInstance(name);
            try
            {
                instance.Load();
            }
            catch
            {
                // 忽略读取失败
            }
            if (instance.state == McInstanceState.Error)
                continue;
            var loaders = _LoaderSummary(instance.Info);
            sb.AppendLine().Append("- ").Append(name)
                .Append("（MC ").Append(instance.Info.VanillaName);
            if (loaders.Length > 0)
                sb.Append("，").Append(loaders);
            sb.Append("）");
        }
        return await Task.FromResult(_Cap(sb.ToString()));
    }

    private static async Task<string> ListModsCoreAsync(JsonObject args, CancellationToken ct)
    {
        var instance = _ResolveInstance(args, out var err);
        if (instance is null)
            return err;
        ct.ThrowIfCancellationRequested();
        var modsDir = _ModsFolder(instance);
        if (!Directory.Exists(modsDir))
            return $"实例「{instance.Name}」的 mods 目录不存在（{modsDir}）";
        var files = Directory.EnumerateFiles(modsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var active = files.Count(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
        var disabled = files.Count - active;
        var sb = new StringBuilder($"实例「{instance.Name}」的 mods 目录共 {files.Count} 个文件（启用 {active} / 禁用 {disabled}）");
        if (files.Count > 0)
        {
            sb.AppendLine("：");
            foreach (var name in files.Take(200))
                sb.AppendLine("- " + name);
            if (files.Count > 200)
                sb.AppendLine($"…（其余 {files.Count - 200} 个省略）");
        }
        return await Task.FromResult(_Cap(sb.ToString()));
    }

    private static async Task<string> SearchModsCoreAsync(JsonObject args, CancellationToken ct)
    {
        var query = args["query"]?.GetValue<string>()?.Trim() ?? "";
        if (query.Length == 0)
            return "缺少 query（搜索关键词）参数";
        var loader = _LoaderFromText(args["loader"]?.GetValue<string>() ?? "");
        var gameVersion = args["gameVersion"]?.GetValue<string>()?.Trim();
        if (gameVersion is { Length: 0 })
            gameVersion = null;

        ct.ThrowIfCancellationRequested();
        var request = new ModComp.CompProjectRequest(CompType.Mod, new ModComp.CompProjectStorage(), 40)
        {
            searchText = query,
            modLoader = loader,
            gameVersion = gameVersion ?? ""
        };

        var results = new List<(bool FromCurseForge, ModComp.CompProject Project)>();
        try
        {
            // CurseForge
            var cfAddress = request.GetCurseForgeAddress();
            if (!string.IsNullOrEmpty(cfAddress))
            {
                var cfJson = ModDownload.DlModRequest<JsonObject>(cfAddress);
                if (cfJson?["data"] is JsonArray cfItems)
                    foreach (var item in cfItems)
                        if (item is JsonObject obj)
                            results.Add((true, new ModComp.CompProject(obj)));
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 搜索 Mod（CurseForge）失败");
        }
        try
        {
            // Modrinth
            var mrAddress = request.GetModrinthAddress();
            if (!string.IsNullOrEmpty(mrAddress))
            {
                var mrJson = ModDownload.DlModRequest<JsonObject>(mrAddress);
                if (mrJson?["hits"] is JsonArray hits)
                    foreach (var hit in hits)
                        if (hit is JsonObject obj)
                            results.Add((false, new ModComp.CompProject(obj)));
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 搜索 Mod（Modrinth）失败");
        }

        if (results.Count == 0)
            return $"两个平台都没有搜到「{query}」相关的结果" +
                   (gameVersion is null ? "" : $"（MC {gameVersion}）") +
                   (loader == CompLoaderType.Any ? "" : $"（加载器 {loader}）") +
                   "。可尝试换关键词或去掉筛选条件。";

        var sb = new StringBuilder($"搜索「{query}」共找到 {results.Count} 个项目（前 12 个）：");
        foreach (var (fromCf, project) in results.Take(12))
        {
            ct.ThrowIfCancellationRequested();
            var loaders = project.ModLoaders.Count > 0 ? string.Join("、", project.ModLoaders) : "不限";
            var desc = (project.Description ?? "").Replace("\n", " ").Trim();
            if (desc.Length > 90)
                desc = desc[..90] + "…";
            sb.AppendLine()
                .Append("- ").Append(project.RawName)
                .Append(" [").Append(fromCf ? "CurseForge" : "Modrinth").Append("]")
                .Append(" ID=").Append(project.Id)
                .Append("，类型 ").Append(project.Type)
                .Append("，加载器 ").Append(loaders)
                .Append("，下载量 ").Append(project.DownloadCount);
            if (desc.Length > 0)
                sb.Append("\n  ").Append(desc);
        }
        return await Task.FromResult(_Cap(sb.ToString()));
    }

    private static async Task<string> ModVersionsCoreAsync(JsonObject args, CancellationToken ct)
    {
        var (fromCf, projectId) = _ProjectKey(args, out var err);
        if (err.Length > 0)
            return err;
        ct.ThrowIfCancellationRequested();
        List<ModComp.CompFile> files;
        ModComp.CompProject? project;
        try
        {
            files = ModComp.CompFilesGet(projectId, fromCf);
            project = ModComp.compProjectCache.TryGetValue(projectId, out var p) ? p : null;
        }
        catch (Exception ex)
        {
            return $"获取项目版本列表失败：{ex.Message}";
        }

        var available = files.Where(f => f.Available).ToList();
        var sb = new StringBuilder();
        if (project is not null)
            sb.Append(project.RawName).Append(" [").Append(fromCf ? "CurseForge" : "Modrinth").Append("]");
        sb.Append(" 共 ").Append(available.Count).Append(" 个可用版本（显示最新 30 个）：");
        foreach (var line in available
                     .OrderByDescending(f => f.Status == CompFileStatus.Release)
                     .ThenByDescending(f => f.ReleaseDate)
                     .Take(30)
                     .Select(_VersionLine))
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine().Append(line);
        }
        return await Task.FromResult(_Cap(sb.ToString()));
    }

    /// <summary>下载目标文件到实例 mods 目录（共用：安装直达目录，更新先下到临时目录）。</summary>
    private static async Task<string> _PickAndDownloadAsync(JsonObject args, string operation, CancellationToken ct)
    {
        var instance = _ResolveInstance(args, out var err);
        if (instance is null)
            return err;
        var (fromCf, projectId) = _ProjectKey(args, out err);
        if (err.Length > 0)
            return err;
        var targetVersion = args["targetVersion"]?.GetValue<string>();

        ct.ThrowIfCancellationRequested();
        List<ModComp.CompFile> files;
        CompProject? project;
        try
        {
            files = ModComp.CompFilesGet(projectId, fromCf);
            project = ModComp.compProjectCache.TryGetValue(projectId, out var p) ? p : null;
        }
        catch (Exception ex)
        {
            return $"获取项目信息失败：{ex.Message}";
        }
        if (project is null)
            return $"项目 {projectId} 信息读取失败";
        if (project.Type != CompType.Mod)
            return $"项目「{project.RawName}」类型为 {project.Type}，本工具仅支持 Mod（mods 目录）";

        var suitable = _SuitableFiles(instance, project, files);
        var file = _PickByTarget(suitable, targetVersion);
        if (file is null)
            return _NoSuitableText(instance, project, suitable.Count > 0 ? suitable : files, targetVersion);

        // 构造下载任务标题与目标文件名（与快速下载一致：中文名规则 CompFileNameGet）
        var targetName = ModComp.CompFileNameGet(project, file);
        return await _DownloadIntoAsync(instance, project, file, targetName, operation, ct);
    }

    private static async Task<string> _DownloadIntoAsync(McInstance instance, CompProject project, CompFile file,
        string targetName, string operation, CancellationToken ct)
    {
        var folder = _ModsFolder(instance);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return $"创建 mods 目录失败：{ex.Message}";
        }
        var target = Path.Combine(folder, targetName);
        var title = operation + "：" + project.RawName + " - " + _VersionLabel(file);
        var error = await _DownloadFileAsync(title, project, file, target, ct);
        if (error is not null)
        {
            try
            {
                if (File.Exists(target))
                    File.Delete(target); // 清掉可能的残缺文件
            }
            catch
            {
                // 忽略清理失败
            }
            return error;
        }
        if (!File.Exists(target))
            return "下载已完成但目标文件不存在（下载可能被跳过）";
        return $"已{operation}完成：{targetName}（{_VersionLabel(file)}，MC {string.Join("、", file.GameVersions.Distinct().Take(6))}）\n位置：{target}";
    }

    private static async Task<string> InstallModCoreAsync(JsonObject args, CancellationToken ct) =>
        await _PickAndDownloadAsync(args, "安装到实例", ct);

    private static async Task<string> UpdateModCoreAsync(JsonObject args, CancellationToken ct)
    {
        var instance = _ResolveInstance(args, out var err);
        if (instance is null)
            return err;
        var (fromCf, projectId) = _ProjectKey(args, out err);
        if (err.Length > 0)
            return err;
        var targetVersion = args["targetVersion"]?.GetValue<string>();

        ct.ThrowIfCancellationRequested();
        List<ModComp.CompFile> files;
        CompProject? project;
        try
        {
            files = ModComp.CompFilesGet(projectId, fromCf);
            project = ModComp.compProjectCache.TryGetValue(projectId, out var p) ? p : null;
        }
        catch (Exception ex)
        {
            return $"获取项目信息失败：{ex.Message}";
        }
        if (project is null)
            return $"项目 {projectId} 信息读取失败";

        // 1. 计算本地 mods 目录中该项目所属的文件（文件哈希 ↔ 项目各版本哈希）
        var folder = _ModsFolder(instance);
        List<(string Path, CompFile Version)> installed;
        try
        {
            if (!Directory.Exists(folder))
                return $"实例「{instance.Name}」的 mods 目录不存在，没有可更新的文件";
            var hashMap = files.Where(f => !string.IsNullOrEmpty(f.Hash))
                .GroupBy(f => f.Hash.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());
            installed = new List<(string, CompFile)>();
            foreach (var local in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(local);
                if (!_IsModFile(name))
                    continue;
                var sha1 = ModBase.GetFileSHA1(local);
                if (sha1.Length == 0)
                    continue;
                if (hashMap.TryGetValue(sha1.ToLowerInvariant(), out var matched))
                    installed.Add((local, matched));
            }
        }
        catch (Exception ex)
        {
            return $"扫描实例 mods 目录失败：{ex.Message}";
        }

        if (installed.Count == 0)
            return $"实例「{instance.Name}」的 mods 目录中没有找到项目「{project.RawName}」的文件（按文件哈希比对，该 mod 可能来自其他来源或尚未安装）。若要安装它，请改用 install_mod 工具。";

        // 2. 挑选目标版本
        var suitable = _SuitableFiles(instance, project, files);
        var target = _PickByTarget(suitable, targetVersion);
        if (target is null)
            return _NoSuitableText(instance, project, suitable.Count > 0 ? suitable : files, targetVersion);

        // 3. 与已装版本比较（多个同项目文件取写入时间最新者为主）
        var primary = installed.OrderByDescending(x => File.GetLastWriteTimeUtc(x.Path)).First();
        var primaryLabel = _VersionLabel(primary.Version);
        if (string.Equals(primary.Version?.Id, target.Id, StringComparison.Ordinal) ||
            string.Equals(primary.Version?.Hash, target.Hash, StringComparison.OrdinalIgnoreCase))
            return $"实例中已安装的就是目标版本：{primaryLabel}，无需更新" +
                   (string.IsNullOrWhiteSpace(targetVersion) ? "（已是最新）" : "");

        // 4. 下载到临时目录，成功后再替换主文件（保留原文件名，避免实例引用变化）
        var tempDir = Path.Combine(ModBase.pathTemp, "AiMod");
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            return $"创建临时目录失败：{ex.Message}";
        }
        var tempTarget = Path.Combine(tempDir, Path.GetFileName(primary.Path));
        var title = "更新 Mod：" + project.RawName + " - " + _VersionLabel(target);
        var error = await _DownloadFileAsync(title, project, target, tempTarget, ct);
        if (error is not null)
            return error;
        if (!File.Exists(tempTarget))
            return "下载已完成但临时文件不存在";

        // 5. 替换主文件；其余同项目旧文件不动（避免破坏用户布局）
        try
        {
            File.Move(tempTarget, primary.Path, true);
        }
        catch (Exception ex)
        {
            return $"替换文件失败：{ex.Message}";
        }

        var extra = installed.Count - 1;
        var result = $"更新完成：{Path.GetFileName(primary.Path)}\n{primaryLabel} → {_VersionLabel(target)}";
        if (extra > 0)
            result += $"\n另有 {extra} 个同项目旧文件未改动（如需清理可告知用户手动处理）。";
        return result;
    }

    private static bool _IsModFile(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".jar") || lower.EndsWith(".zip") ||
               lower.EndsWith(".jar.disabled") || lower.EndsWith(".zip.disabled");
    }

    #endregion
}
