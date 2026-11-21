using System.IO;
using System.IO.Compression;
using System.Net;
using FluentValidation;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Server;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;

namespace PCL;

/// <summary>
///     整合包服务端下载：从 CurseForge / Modrinth 获取提供有服务端文件的整合包。
///     下载完成后由用户填写实例名称，整合包会被解压到 serverversion/&lt;实例名称&gt; 文件夹，
///     并写入服务端实例元数据，从而可以被启动器直接识别与启动。
/// </summary>
public static class ModServerPackDownload
{
    /// <summary>整合包来源。</summary>
    public enum PackSource
    {
        CurseForge = 1,
        Modrinth = 2
    }

    /// <summary>整合包工程信息。</summary>
    public class ServerPackProject
    {
        public PackSource Source;
        public string Id = "";
        public string Slug = "";
        public string Name = "";
        public string Description = "";
        public string LogoUrl = "";
        public long DownloadCount;
        public string Website = "";
        public List<string> GameVersions = [];
        public string Loader = "";
    }

    /// <summary>整合包的一个可下载文件（CurseForge 的服务端包或 Modrinth 的 .mrpack）。</summary>
    public class ServerPackFile
    {
        public ServerPackProject? Project;
        public string FileId = "";
        public string FileName = "";
        public string DisplayName = "";
        public string GameVersion = "";
        public string Loader = "";
        public List<string> Urls = [];
        public string Hash = "";
        public long Size = -1;
        public DateTime ReleaseDate;
        public bool IsServerPack;
    }

    /// <summary>解压后从整合包中解析出的服务端信息。</summary>
    private sealed record PackInfo(string GameVersion, string Loader, string LoaderVersion);

    #region 网络

    private static JsonNode FetchJson(string url, int timeout = 20000)
    {
        // CurseForge 官方 API 需要 API Key，直接请求会返回 403。
        // 统一走启动器的 DlModRequest：它会按设置自动切换到 mcimirror 镜像并重试。
        return ModDownload.DlModRequest<JsonObject>(url);
    }

    private static JsonArray FetchJsonArray(string url)
    {
        return ModDownload.DlModRequest<JsonArray>(url);
    }

    private static string GetString(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static long GetLong(JsonNode? node, string key)
    {
        try
        {
            var value = node?[key];
            return value is null ? 0L : value.ToObject<long>();
        }
        catch
        {
            return 0L;
        }
    }

    /// <summary>将 CurseForge 的 ModLoaderType 转换为显示用的加载器名称。</summary>
    private static string LoaderNameFromCurseForge(long modLoader)
    {
        return modLoader switch
        {
            1 => "Forge",
            4 => "Fabric",
            5 => "Quilt",
            6 => "NeoForge",
            _ => ""
        };
    }

    /// <summary>从版本/分类列表中推断加载器名称。</summary>
    private static string LoaderNameFromTags(IEnumerable<string> tags)
    {
        var list = tags.Select(tag => tag.ToLowerInvariant()).ToList();
        if (list.Contains("neoforge")) return "NeoForge";
        if (list.Contains("forge")) return "Forge";
        if (list.Contains("quilt")) return "Quilt";
        if (list.Contains("fabric")) return "Fabric";
        if (list.Contains("paper")) return "Paper";
        if (list.Contains("purpur")) return "Purpur";
        return "";
    }

    private static DateTime ParseDate(string text)
    {
        return DateTime.TryParse(text, out var date) ? date : DateTime.MinValue;
    }

    #endregion

    #region 搜索

    /// <summary>
    ///     搜索带有服务端文件的整合包。必须在工作线程执行。
    /// </summary>
    /// <param name="source">来源站点。</param>
    /// <param name="keyword">搜索关键词，可为空。</param>
    /// <param name="offset">已经加载的条目数量。</param>
    /// <param name="pageSize">本次加载的最大条目数量。</param>
    /// <param name="hasMore">是否还有更多结果。</param>
    public static List<ServerPackProject> SearchProjects(PackSource source, string? keyword, int offset, int pageSize,
        out bool hasMore)
    {
        hasMore = false;
        var result = new List<ServerPackProject>();
        switch (source)
        {
            case PackSource.CurseForge:
            {
                var url =
                    "https://api.curseforge.com/v1/mods/search?gameId=432&classId=4471&sortField=2&sortOrder=desc" +
                    $"&pageSize={pageSize}&index={offset}";
                if (!string.IsNullOrWhiteSpace(keyword))
                    url += "&searchFilter=" + WebUtility.UrlEncode(keyword);
                var json = FetchJson(url);
                foreach (var item in (json["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var project = new ServerPackProject
                    {
                        Source = PackSource.CurseForge,
                        Id = GetString(item, "id"),
                        Slug = GetString(item, "slug"),
                        Name = GetString(item, "name"),
                        Description = GetString(item, "summary"),
                        LogoUrl = GetString(item["logo"], "thumbnailUrl"),
                        DownloadCount = GetLong(item, "downloadCount"),
                        Website = GetString(item["links"], "websiteUrl")
                    };
                    if (item["latestFilesIndexes"] is JsonArray indexes)
                    {
                        foreach (var index in indexes.OfType<JsonObject>())
                        {
                            var version = GetString(index, "gameVersion");
                            if (version.Length > 0 && !project.GameVersions.Contains(version))
                                project.GameVersions.Add(version);
                            if (project.Loader.Length == 0)
                                project.Loader = LoaderNameFromCurseForge(GetLong(index, "modLoader"));
                        }
                    }

                    if (project.Id.Length > 0)
                        result.Add(project);
                }

                hasMore = result.Count >= pageSize;
                break;
            }
            case PackSource.Modrinth:
            {
                var url = "https://api.modrinth.com/v2/search?index=downloads&facets=[[\"project_type:modpack\"]]" +
                          $"&limit={pageSize}&offset={offset}";
                if (!string.IsNullOrWhiteSpace(keyword))
                    url += "&query=" + WebUtility.UrlEncode(keyword);
                var json = FetchJson(url);
                foreach (var item in (json["hits"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var project = new ServerPackProject
                    {
                        Source = PackSource.Modrinth,
                        Id = GetString(item, "project_id"),
                        Slug = GetString(item, "slug"),
                        Name = GetString(item, "title"),
                        Description = GetString(item, "description"),
                        LogoUrl = GetString(item, "icon_url"),
                        DownloadCount = GetLong(item, "downloads"),
                        Website = "https://modrinth.com/modpack/" + GetString(item, "slug")
                    };
                    if (item["versions"] is JsonArray versions)
                        project.GameVersions.AddRange(versions.Select(version => version?.ToString() ?? "")
                            .Where(version => version.Length > 0));
                    if (item["categories"] is JsonArray categories)
                        project.Loader = LoaderNameFromTags(categories.Select(category => category?.ToString() ?? ""));
                    if (project.Id.Length > 0)
                        result.Add(project);
                }

                hasMore = offset + result.Count < GetLong(json, "total_hits");
                break;
            }
        }

        return result;
    }

    /// <summary>
    ///     获取指定整合包的可下载文件列表。必须在工作线程执行。
    /// </summary>
    public static List<ServerPackFile> GetFiles(ServerPackProject project)
    {
        return project.Source switch
        {
            PackSource.CurseForge => GetCurseForgeFiles(project),
            PackSource.Modrinth => GetModrinthFiles(project),
            _ => []
        };
    }

    #endregion

    #region 文件列表

    private static List<ServerPackFile> GetCurseForgeFiles(ServerPackProject project)
    {
        var json = FetchJson($"https://api.curseforge.com/v1/mods/{project.Id}/files?pageSize=10000");
        var data = (json["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var byId = new Dictionary<long, JsonObject>();
        foreach (var file in data)
        {
            var fileId = GetLong(file, "id");
            if (fileId > 0)
                byId[fileId] = file;
        }

        var result = new List<ServerPackFile>();
        foreach (var file in data)
        {
            var serverPackId = GetLong(file, "serverPackFileId");
            if (serverPackId <= 0)
                continue; // 该文件没有提供独立的服务端包
            if (!byId.TryGetValue(serverPackId, out var serverFile))
            {
                serverFile = FetchJson($"https://api.curseforge.com/v1/mods/{project.Id}/files/{serverPackId}")["data"]
                    as JsonObject;
                if (serverFile is null)
                    continue;
            }

            result.Add(BuildCurseForgeFile(project, serverFile, true));
        }

        return result.OrderByDescending(file => file.ReleaseDate).ToList();
    }

    private static ServerPackFile BuildCurseForgeFile(ServerPackProject project, JsonObject file, bool isServerPack)
    {
        var fileId = GetLong(file, "id");
        var fileName = GetString(file, "fileName");
        var gameVersions = (file["gameVersions"] as JsonArray ?? new JsonArray())
            .Select(version => version?.ToString() ?? "").ToList();
        var gameVersion = gameVersions.FirstOrDefault(version =>
                              version.Length > 0 && char.IsDigit(version[0]) && version.Contains('.')) ??
                          gameVersions.FirstOrDefault() ?? project.GameVersions.FirstOrDefault() ?? "";
        var loader = LoaderNameFromTags(gameVersions);
        if (loader.Length == 0)
            loader = project.Loader;

        var hash = "";
        if (file["hashes"] is JsonArray hashes)
        {
            var sha1 = hashes.OfType<JsonObject>().FirstOrDefault(item => GetLong(item, "algo") == 1);
            hash = sha1?["value"]?.ToString() ?? "";
        }

        return new ServerPackFile
        {
            Project = project,
            FileId = fileId.ToString(),
            FileName = fileName,
            DisplayName = GetString(file, "displayName"),
            GameVersion = gameVersion,
            Loader = loader,
            Urls = BuildCurseForgeUrls(fileId, fileName, GetString(file, "downloadUrl")),
            Hash = hash,
            Size = GetLong(file, "fileLength"),
            ReleaseDate = ParseDate(GetString(file, "fileDate")),
            IsServerPack = isServerPack
        };
    }

    /// <summary>
    ///     构造 CurseForge 文件的下载地址。
    ///     接口未直接返回 downloadUrl 时，使用官方 CDN 的固定路径格式兜底。
    /// </summary>
    private static List<string> BuildCurseForgeUrls(long fileId, string fileName, string downloadUrl)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(downloadUrl))
            urls.AddRange(ModDownload.DlSourceModDownloadGet(downloadUrl));
        if (fileId > 0 && !string.IsNullOrWhiteSpace(fileName))
        {
            var folder = fileId / 1000;
            var remainder = fileId % 1000;
            var escapedName = Uri.EscapeDataString(fileName);
            var cdn = $"https://edge.forgecdn.net/files/{folder}/{remainder}/{escapedName}";
            urls.AddRange(ModDownload.DlSourceModDownloadGet(cdn));
            urls.Add(cdn);
        }

        return urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct().ToList();
    }

    private static List<ServerPackFile> GetModrinthFiles(ServerPackProject project)
    {
        var json = FetchJsonArray($"https://api.modrinth.com/v2/project/{project.Id}/version?include_changelog=false");
        var result = new List<ServerPackFile>();
        foreach (var version in (json as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var files = (version["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var file = files.FirstOrDefault(item =>
                           GetString(item, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) ??
                       files.FirstOrDefault();
            if (file is null)
                continue;
            var gameVersions = (version["game_versions"] as JsonArray ?? new JsonArray())
                .Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToList();
            var loader = LoaderNameFromTags((version["loaders"] as JsonArray ?? new JsonArray())
                .Select(item => item?.ToString() ?? ""));
            if (loader.Length == 0)
                loader = project.Loader;
            var urls = (file["urls"] as JsonArray ?? new JsonArray())
                .Select(item => item?.ToString() ?? "").Where(item => item.Length > 0)
                .SelectMany(ModDownload.DlSourceModDownloadGet).Distinct().ToList();
            result.Add(new ServerPackFile
            {
                Project = project,
                FileId = GetString(version, "id"),
                FileName = GetString(file, "filename"),
                DisplayName = GetString(version, "name"),
                GameVersion = gameVersions.FirstOrDefault() ?? project.GameVersions.FirstOrDefault() ?? "",
                Loader = loader,
                Urls = urls,
                Hash = GetString(file["hashes"], "sha1"),
                Size = GetLong(file, "size"),
                ReleaseDate = ParseDate(GetString(version, "date_published")),
                IsServerPack = true
            });
        }

        return result.OrderByDescending(file => file.ReleaseDate).ToList();
    }

    #endregion

    #region 下载与安装

    /// <summary>
    ///     下载整合包并在下载完成后引导用户填写实例名称、解压到 serverversion 文件夹。
    /// </summary>
    /// <param name="file">要下载的文件。</param>
    /// <param name="onInstalled">实例创建成功后的回调，参数为实例名称。回调在后台线程执行。</param>
    /// <param name="onFailed">用户取消或安装失败时的回调，在后台线程执行。</param>
    public static LoaderCombo<int> Download(ServerPackFile file, Action<string> onInstalled, Action? onFailed = null)
    {
        if (file.Urls.Count == 0)
            throw new InvalidOperationException(Lang.Text("Download.ServerPack.Error.NoUrl"));
        var tempFolder = ModMain.RequestTaskTempFolder();
        var archiveName = SanitizeFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(archiveName))
            archiveName = file.IsServerPack ? "serverpack.zip" : "modpack.mrpack";
        var archivePath = Path.Combine(tempFolder, archiveName);
        var checker = string.IsNullOrWhiteSpace(file.Hash) ? null : new ModBase.FileChecker(hash: file.Hash);
        var download = new LoaderDownload(Lang.Text("Download.ServerPack.Task.Download"),
            [new DownloadFile(file.Urls, archivePath, checker)]);
        var loaders = new List<LoaderBase> { download };
        var loader = new LoaderCombo<int>(Lang.Text("Download.ServerPack.Task.Title", file.Project?.Name ?? ""), loaders)
        {
            OnStateChanged = state =>
            {
                ModDownloadLib.LoaderStateChangedHintOnly(state);
                if (state.State == ModBase.LoadState.Finished)
                    ModBase.RunInThread(() => Install(file, archivePath, onInstalled, onFailed));
            }
        };
        loader.Start(0);
        return loader;
    }

    private static void Install(ServerPackFile file, string archivePath, Action<string> onInstalled, Action? onFailed)
    {
        string? folder = null;
        try
        {
            // 下载完成后弹出窗口填写实例名称
            var name = AskInstanceName(file);
            if (string.IsNullOrWhiteSpace(name))
            {
                ModBase.Log("[ServerPack] 用户取消填写实例名称，已取消安装");
                onFailed?.Invoke();
                return;
            }

            folder = Path.Combine(ModServerInstance.RootFolder, name);
            if (!ModServerDownload.IsInstanceNameValid(name, out var error))
                throw new IOException(error ?? Lang.Text("Download.ServerPack.Error.NameInvalid"));

            Directory.CreateDirectory(folder);
            ModBase.Log($"[ServerPack] 正在解压整合包服务端到 {folder}");
            var info = Extract(file, archivePath, folder);
            var core = EnsureServerCore(folder, info.GameVersion, info.Loader, info.LoaderVersion);
            ModServerInstance.WriteMeta(folder, new ModServerInstance.ServerMeta
            {
                Core = core.Core,
                GameVersion = info.GameVersion,
                Build = info.LoaderVersion,
                Jar = core.Jar,
                Launch = core.Launch
            });
            ModBase.Log($"[ServerPack] 整合包服务端实例已创建：{folder}");
            onInstalled?.Invoke(name);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerPack] 安装整合包服务端失败", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Download.ServerPack.Error.InstallFailed", ex.Message));
            try
            {
                if (folder is not null && Directory.Exists(folder) &&
                    !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch
            {
                // 忽略清理失败
            }

            onFailed?.Invoke();
        }
    }

    /// <summary>
    ///     弹出输入框让用户填写实例名称；取消时返回 null。
    /// </summary>
    private static string? AskInstanceName(ServerPackFile file)
    {
        string? name = null;
        ModBase.RunInUiWait(() =>
        {
            name = ModMain.MyMsgBoxInput(
                Lang.Text("Download.ServerPack.Name.Title"),
                Lang.Text("Download.ServerPack.Name.Message", file.Project?.Name ?? "", file.DisplayName),
                GetDefaultInstanceName(file),
                [new InstanceNameValidator()],
                hintText: Lang.Text("Download.ServerPack.Name.Hint"));
        });
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>生成默认的实例名称，如 prominence-server。</summary>
    public static string GetDefaultInstanceName(ServerPackFile file)
    {
        var baseName = file.Project?.Slug;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = file.Project?.Name;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "serverpack";
        var safe = SanitizeFileName(baseName);
        return string.IsNullOrWhiteSpace(safe) ? "serverpack" : safe + "-server";
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>实例名称校验：复用服务端下载页的规则，并提供本地化提示。</summary>
    private sealed class InstanceNameValidator : AbstractValidator<string>
    {
        public InstanceNameValidator()
        {
            RuleFor(name => name).Custom((name, context) =>
            {
                if (!ModServerDownload.IsInstanceNameValid(name, out var error))
                    context.AddFailure(error ?? Lang.Text("Download.ServerPack.Error.NameInvalid"));
            });
        }
    }

    #endregion

    #region 解压

    private static PackInfo Extract(ServerPackFile file, string archivePath, string folder)
    {
        if (IsModrinthPack(archivePath))
            return ExtractModrinthPack(archivePath, folder);
        // CurseForge 的服务端包是完整压缩包，直接整体解压
        ModBase.ExtractFile(archivePath, folder);
        FlattenSingleRoot(folder);
        return new PackInfo(file.GameVersion, file.Loader, "");
    }

    private static bool IsModrinthPack(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.GetEntry("modrinth.index.json") is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     解压 Modrinth 的 .mrpack：仅提取服务端需要的文件与 overrides，并返回加载器信息。
    /// </summary>
    private static PackInfo ExtractModrinthPack(string archivePath, string folder)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var indexEntry = archive.GetEntry("modrinth.index.json") ??
                         throw new InvalidDataException("modrinth.index.json");
        var index = ModBase.GetJson(ModBase.ReadFile(indexEntry.Open())) as JsonObject ??
                    throw new InvalidDataException("modrinth.index.json");

        var root = Path.GetFullPath(folder);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // 先应用 overrides，再应用 server-overrides，以保证服务端覆盖优先
        ExtractOverrideDirectory(archive, folder, rootWithSeparator, "overrides/");
        ExtractOverrideDirectory(archive, folder, rootWithSeparator, "server-overrides/");

        // 下载 index 中列出的服务端文件（跳过仅客户端所需的文件）
        foreach (var node in (index["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var relative = GetString(node, "path").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            if (string.Equals(GetString(node["env"], "server"), "unsupported", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsClientOnlyPath(relative))
                continue;
            var downloads = (node["downloads"] as JsonArray ?? new JsonArray())
                .Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToList();
            if (downloads.Count == 0)
                continue;
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                var urls = downloads.SelectMany(ModDownload.DlSourceModDownloadGet).Distinct().ToList();
                ModNet.NetDownloadByLoader(urls, target);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[ServerPack] 下载整合包文件失败：{relative}", ModBase.LogLevel.Hint);
            }
        }

        var dependencies = index["dependencies"] as JsonObject ?? new JsonObject();
        var gameVersion = GetString(dependencies, "minecraft");
        var loader = "";
        var loaderVersion = "";
        if (GetString(dependencies, "fabric-loader").Length > 0)
        {
            loader = "Fabric";
            loaderVersion = GetString(dependencies, "fabric-loader");
        }
        else if (GetString(dependencies, "quilt-loader").Length > 0)
        {
            loader = "Quilt";
            loaderVersion = GetString(dependencies, "quilt-loader");
        }
        else if (GetString(dependencies, "neoforge").Length > 0)
        {
            loader = "NeoForge";
            loaderVersion = GetString(dependencies, "neoforge");
        }
        else if (GetString(dependencies, "forge").Length > 0)
        {
            loader = "Forge";
            loaderVersion = GetString(dependencies, "forge");
        }

        return new PackInfo(gameVersion, loader, loaderVersion);
    }

    #endregion

    #region 目录整理

    private static void ExtractOverrideDirectory(ZipArchive archive, string folder, string rootWithSeparator,
        string prefix)
    {
        foreach (var entry in archive.Entries)
        {
            var fullName = entry.FullName.Replace('\\', '/');
            if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = fullName[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
                continue;
            var target = Path.GetFullPath(Path.Combine(folder, relative));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                continue; // 防止路径穿越
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static bool IsClientOnlyPath(string path)
    {
        return path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     有些服务端包解压后多套了一层同名根文件夹，这里把唯一子文件夹的内容上提到实例根目录。
    /// </summary>
    private static void FlattenSingleRoot(string folder)
    {
        try
        {
            var directories = Directory.GetDirectories(folder);
            var files = Directory.GetFiles(folder);
            if (directories.Length != 1 || files.Length != 0)
                return;
            var inner = directories[0];
            var looksLikeServerPack = Directory.Exists(Path.Combine(inner, "mods")) ||
                                      Directory.EnumerateFiles(inner, "*.jar").Any() ||
                                      File.Exists(Path.Combine(inner, "startserver.bat")) ||
                                      File.Exists(Path.Combine(inner, "start.bat")) ||
                                      File.Exists(Path.Combine(inner, "run.bat"));
            if (!looksLikeServerPack)
                return;
            foreach (var directory in Directory.GetDirectories(inner))
                Directory.Move(directory, Path.Combine(folder, Path.GetFileName(directory)));
            foreach (var file in Directory.GetFiles(inner))
                File.Move(file, Path.Combine(folder, Path.GetFileName(file)), true);
            Directory.Delete(inner);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ServerPack] 整理整合包目录结构失败", ModBase.LogLevel.Debug);
        }
    }

    #endregion

    #region 服务端核心

    /// <summary>
    ///     确保实例文件夹中存在可用的服务端核心。
    ///     如果整合包自带了核心则直接使用；否则按加载器自动下载并安装对应的核心。
    /// </summary>
    private static (string Core, string Launch, string Jar) EnsureServerCore(string folder, string gameVersion,
        string loader, string loaderVersion)
    {
        var coreName = string.IsNullOrWhiteSpace(loader)
            ? Lang.Text("Download.ServerPack.Type.Modpack")
            : $"{loader} {Lang.Text("Download.ServerPack.Type.Modpack")}";
        var jar = ModServerInstance.FindJar(folder);
        if (!string.IsNullOrEmpty(jar) && !IsInstallerJar(jar))
            return (coreName, "jar", Path.GetFileName(jar));

        try
        {
            var kind = MapServerKind(loader);
            if (kind is null)
                throw new NotSupportedException(Lang.Text("Download.ServerPack.Error.UnsupportedLoader", loader));
            var javaCheck = ServerJavaEnvironment.CheckAsync(gameVersion, "", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!javaCheck.Success)
                throw new InvalidOperationException(javaCheck.Message);

            var options = new ServerCreateOptions
            {
                Name = Path.GetFileName(folder),
                Kind = kind.Value,
                MinecraftVersion = gameVersion,
                PackageVersion = loaderVersion,
                JavaPath = javaCheck.SelectedJavaPath,
                MemoryMb = ModServerInstance.DefaultMemoryMb,
                MinMemoryMb = ModServerInstance.MinMemoryMb
            };
            var plan = new ServerCatalog().ResolveAsync(options, folder).GetAwaiter().GetResult();
            plan.ServerId = Path.GetFileName(folder);
            plan.JavaPath = javaCheck.SelectedJavaPath;
            foreach (var artifact in plan.Artifacts)
            {
                var target = Path.Combine(folder, artifact.FileName);
                if (File.Exists(target))
                    continue;
                ModNet.NetDownloadByLoader(artifact.Url, target);
            }

            var instance = ServerProvisioner
                .ConfigureAsync(plan, options, new ServerStore(ModServerInstance.RootFolder))
                .GetAwaiter().GetResult();
            var launch = string.IsNullOrWhiteSpace(instance.LaunchMode) ? "jar" : instance.LaunchMode;
            var finalJar = ModServerInstance.FindJar(folder);
            var jarName = finalJar is null || IsInstallerJar(finalJar) ? "" : Path.GetFileName(finalJar);
            return (coreName, launch, jarName);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Download.ServerPack.Error.CoreFailed", gameVersion), ModBase.LogLevel.Hint);
            HintService.Hint(Lang.Text("Download.ServerPack.CoreMissing", Path.GetFileName(folder)), HintType.Info);
            return (coreName, "jar", "");
        }
    }

    private static ServerKind? MapServerKind(string loader)
    {
        return loader switch
        {
            "Fabric" => ServerKind.Fabric,
            "Forge" => ServerKind.Forge,
            "NeoForge" => ServerKind.NeoForge,
            "Paper" => ServerKind.Paper,
            "Purpur" => ServerKind.Purpur,
            "Quilt" => null,
            _ => ServerKind.Vanilla
        };
    }

    private static bool IsInstallerJar(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("buildtools", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
