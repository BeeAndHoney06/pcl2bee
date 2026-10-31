using System.IO;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;

namespace PCL;

/// <summary>
///     服务端核心下载：仅提供 Paper、Spigot、Purpur 三种插件服务端。
///     下载结果会保存到 Minecraft 文件夹下的 serverversion 文件夹中，并作为服务端实例被启动器识别。
/// </summary>
public static class ModServerDownload
{
    public enum ServerType
    {
        Paper = 1,
        Purpur = 2,
        Spigot = 3
    }

    /// <summary>服务端版本条目。</summary>
    public class ServerVersionEntry
    {
        /// <summary>显示用版本号（Minecraft 版本）。</summary>
        public string Version = "";

        /// <summary>附加信息（如构建号）。</summary>
        public string Detail = "";

        /// <summary>直接下载 URL。</summary>
        public string Url = "";

        /// <summary>SHA256（若接口提供）。</summary>
        public string Sha256 = "";
    }

    #region 网络

    private static JsonObject FetchJson(string url)
    {
        return (JsonObject)FetchJsonNode(url);
    }

    private static JsonArray FetchJsonArray(string url)
    {
        return (JsonArray)FetchJsonNode(url);
    }

    private static JsonNode FetchJsonNode(string url)
    {
        // PaperMC Fill v3 要求携带可识别的 UA，统一使用浏览器 UA（RequestSigning 会附加产品标识）
        return ModBase.GetJson(Requester.Fetch(url, new FetchParam
        {
            Method = "GET",
            ContentType = "application/json",
            Timeout = 12000,
            UseBrowserUserAgent = true
        }));
    }

    #endregion

    #region 版本列表

    /// <summary>
    ///     获取指定服务端类型的版本列表。必须在工作线程执行。
    /// </summary>
    public static List<ServerVersionEntry> GetVersions(ServerType type)
    {
        return type switch
        {
            ServerType.Paper => GetPaperVersions(),
            ServerType.Purpur => GetPurpurVersions(),
            ServerType.Spigot => GetSpigotVersions(),
            _ => throw new NotSupportedException("Unknown server type: " + type)
        };
    }

    // ---------- Paper（fill.papermc.io v3，官方 API） ----------

    private static List<ServerVersionEntry> GetPaperVersions()
    {
        var json = FetchJson("https://fill.papermc.io/v3/projects/paper");
        var versions = json["versions"]?.AsObject();
        var result = new List<ServerVersionEntry>();
        // versions 按大版本分组（"1.21": ["1.21.11", ...]），组内首项为最新；展平并跳过 pre/rc 快照
        foreach (var group in versions ?? new JsonObject())
        foreach (var ver in (JsonArray)group.Value ?? new JsonArray())
        {
            var text = ver?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(text) || text.Contains('-')) continue; // 跳过 -pre/-rc
            result.Add(new ServerVersionEntry { Version = text });
        }

        return result;
    }

    // ---------- Purpur（api.purpurmc.org v2，官方 API） ----------

    private static List<ServerVersionEntry> GetPurpurVersions()
    {
        var json = FetchJson("https://api.purpurmc.org/v2/purpur");
        var result = new List<ServerVersionEntry>();
        foreach (var ver in (JsonArray)json["versions"] ?? new JsonArray())
            result.Add(new ServerVersionEntry { Version = ver.ToString() });
        return result;
    }

    // ---------- Spigot（GetBukkit 聚合下载，无列表 API → 用 Mojang 版本号映射） ----------

    private static List<ServerVersionEntry> GetSpigotVersions()
    {
        var json = FetchJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var result = new List<ServerVersionEntry>();
        foreach (var entry in (JsonArray)json["versions"] ?? new JsonArray())
        {
            if (entry?["type"]?.ToString() != "release") continue;
            var ver = entry["id"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(ver)) continue;
            result.Add(new ServerVersionEntry
            {
                Version = ver,
                Url = $"https://download.getbukkit.org/spigot/spigot-{ver}.jar"
            });
        }

        return result.Take(80).ToList();
    }

    #endregion

    #region 下载

    /// <summary>一次服务端下载的解析结果。</summary>
    public class DownloadSpec
    {
        /// <summary>下载地址列表。</summary>
        public List<string> Urls = [];

        /// <summary>期望哈希（按长度自动识别 MD5/SHA1/SHA256），未知为 null。</summary>
        public string? Hash;

        /// <summary>建议文件名。</summary>
        public string FileName = "server.jar";

        /// <summary>是否使用浏览器 UA 下载。</summary>
        public bool UseBrowserUserAgent;

        /// <summary>自定义 UA。</summary>
        public string CustomUserAgent = "";
    }

    /// <summary>
    ///     解析某类型/版本/构建对应的下载信息（不发起下载）。必须在工作线程执行。
    /// </summary>
    public static DownloadSpec Resolve(ServerType type, string version, string? build = null)
    {
        var spec = new DownloadSpec();
        string url;
        switch (type)
        {
            case ServerType.Paper:
            {
                // Paper v3：指定构建或最新构建
                var api = string.IsNullOrWhiteSpace(build)
                    ? $"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds/latest"
                    : $"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds/{build}";
                var info = FetchJson(api);
                var download = info["downloads"]?["server:default"];
                url = download?["url"]?.ToString() ?? "";
                spec.Hash = download?["checksums"]?["sha256"]?.ToString();
                spec.FileName = download?["name"]?.ToString() ?? $"paper-{version}.jar";
                break;
            }
            case ServerType.Purpur:
            {
                // Purpur：指定构建，缺省取 latest
                var buildId = build;
                if (string.IsNullOrWhiteSpace(buildId))
                {
                    var info = FetchJson($"https://api.purpurmc.org/v2/purpur/{version}");
                    buildId = info["builds"]?["latest"]?.ToString();
                }

                url = $"https://api.purpurmc.org/v2/purpur/{version}/{buildId}/download";
                spec.FileName = $"purpur-{version}-{buildId}.jar";
                break;
            }
            case ServerType.Spigot:
            {
                // Spigot 无官方直链，使用 GetBukkit 提供的预编译核心
                url = $"https://download.getbukkit.org/spigot/spigot-{version}.jar";
                spec.FileName = $"spigot-{version}.jar";
                break;
            }
            default:
                throw new NotSupportedException("Unknown server type: " + type);
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));

        spec.Urls = [url];
        spec.UseBrowserUserAgent = true;
        spec.CustomUserAgent = $"PCL2-BeeEdition/{ModBase.versionBaseName}";
        return spec;
    }

    /// <summary>
    ///     按解析结果启动下载任务，返回已启动的加载器（进度可轮询 <c>loader.Progress</c>）。
    /// </summary>
    /// <param name="spec">解析结果。</param>
    /// <param name="targetPath">目标文件完整路径。</param>
    /// <param name="onFinish">下载完成后的回调（在加载器线程执行）。</param>
    public static LoaderCombo<int> StartDownload(DownloadSpec spec, string targetPath, Action? onFinish = null)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var loaders = new List<LoaderBase>
        {
            new LoaderDownload(Lang.Text("Download.Server.Task.Download"),
            [
                new DownloadFile(spec.Urls, targetPath,
                    string.IsNullOrEmpty(spec.Hash) ? null : new ModBase.FileChecker(hash: spec.Hash),
                    spec.UseBrowserUserAgent, spec.CustomUserAgent)
            ])
        };
        var loader = new LoaderCombo<int>(Lang.Text("Download.Server.Task.Download"), loaders)
        {
            OnStateChanged = state =>
            {
                ModDownloadLib.LoaderStateChangedHintOnly(state);
                if (state.State == ModBase.LoadState.Finished)
                    onFinish?.Invoke();
            }
        };
        loader.Start(0);
        return loader;
    }

    /// <summary>
    ///     生成服务端实例的默认名称，如 paper-1.21.4。
    /// </summary>
    public static string GetDefaultInstanceName(ServerType type, string version)
    {
        return $"{type.ToString().ToLowerInvariant()}-{version}";
    }

    /// <summary>
    ///     判断实例名称是否可用（非空、不含非法字符、不与已有文件冲突）。
    /// </summary>
    public static bool IsInstanceNameValid(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = Lang.Text("Download.Server.Error.NameEmpty");
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('\\') || name.Contains('/'))
        {
            error = Lang.Text("Download.Server.Error.NameInvalid", name);
            return false;
        }

        if (Directory.Exists(Path.Combine(ModServerInstance.RootFolder, name)) ||
            File.Exists(Path.Combine(ModServerInstance.RootFolder, name)))
        {
            error = Lang.Text("Download.Server.Error.NameExists", name);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     下载服务端核心到 serverversion/&lt;实例名称&gt; 文件夹，并写入实例元数据。
    ///     必须在工作线程执行。
    /// </summary>
    public static LoaderCombo<int> DownloadToInstance(ServerType type, ServerVersionEntry entry, string instanceName,
        Action? onFinish = null)
    {
        var spec = Resolve(type, entry.Version);
        var folder = Path.Combine(ModServerInstance.RootFolder, instanceName);
        Directory.CreateDirectory(folder);
        var jarPath = Path.Combine(folder, spec.FileName);
        ModBase.Log($"[Server] 即将下载 {type} {entry.Version} 到 {jarPath}");
        return StartDownload(spec, jarPath, () =>
        {
            try
            {
                ModServerInstance.WriteMeta(folder, new ModServerInstance.ServerMeta
                {
                    Core = type.ToString(),
                    GameVersion = entry.Version,
                    Build = entry.Detail,
                    Jar = spec.FileName
                });
                ModBase.Log($"[Server] 服务端实例已创建：{folder}");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, Lang.Text("Download.Server.Error.MetaFailed", folder), ModBase.LogLevel.Hint);
            }

            onFinish?.Invoke();
        });
    }

    #endregion
}

