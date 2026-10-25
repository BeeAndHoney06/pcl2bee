using System.IO;
using PCL.Core.App.Localization;
using PCL.Network;
using PCL.Network.Loaders;
using PCL.Core.UI;
using static PCL.ModLoader;

namespace PCL;

/// <summary>
///     服务端核心下载（借鉴 ATLauncher 的 Servers 页）。
///     支持 Vanilla / Paper / Purpur / Spigot / Forge / Fabric。
///     Vanilla/Forge/Fabric 复用现有的官方数据源；Paper/Purpur/Spigot 使用各自的官方 API。
/// </summary>
public static class ModServerDownload
{
    public enum ServerType
    {
        Vanilla = 0,
        Paper = 1,
        Purpur = 2,
        Spigot = 3,
        Forge = 4,
        Fabric = 5
    }

    public class ServerVersionEntry
    {
        /// <summary>显示用版本号（Minecraft 版本）。</summary>
        public string Version;

        /// <summary>附加信息（如 Paper build、Forge 推荐版）。</summary>
        public string Detail = "";

        /// <summary>直接下载 URL（Vanilla 服务端 / Paper / Purpur / Spigot / Forge 安装器 / Fabric 安装器）。</summary>
        public string Url;

        /// <summary>SHA256（Paper 提供）。</summary>
        public string Sha256;
    }

    private static JsonObject FetchJson(string url)
    {
        return (JsonObject)FetchJsonNode(url);
    }

    /// <summary>获取顶层数组 JSON（部分接口如 BMCLAPI forge 列表、Fabric game 版本返回数组）。</summary>
    private static JsonArray FetchJsonArray(string url)
    {
        return (JsonArray)FetchJsonNode(url);
    }

    private static JsonNode FetchJsonNode(string url)
    {
        // 镜像优先（BMCLAPI 国内可达性远好于 Mojang/Fabric 官方源），官方源作为 FallbackUrl
        // Requester.Fetch 内部对两个 URL 各重试 3 次，失败才抛出
        var mirror = ToBmclapiMirror(url);
        var primary = mirror ?? url; // 有镜像：镜像为主；无镜像（Paper/Purpur/Spigot）：官方直连
        var fallback = mirror is not null ? url : null;
        // PaperMC Fill v3 要求携带可识别的 UA，使用浏览器 UA（含产品标识已在 RequestSigning 中附加）
        return ModBase.GetJson(Requester.Fetch(primary, new FetchParam
        {
            Method = "GET",
            ContentType = "application/json",
            Timeout = 12000,
            UseBrowserUserAgent = true,
            FallbackUrl = fallback
        }));
    }

    /// <summary>将官方源 URL 转换为 BMCLAPI 镜像 URL；无镜像映射的源返回 null。</summary>
    private static string? ToBmclapiMirror(string url)
    {
        // 无映射的源（Paper/Purpur/Spigot 官方 API）直接返回 null
        var mappings = new[]
        {
            ("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
            ("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
            ("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/maven"),
            ("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta"),
            ("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven"),
            ("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven"),
            ("https://files.minecraftforge.net", "https://bmclapi2.bangbang93.com")
        };
        foreach (var (from, to) in mappings)
            if (url.StartsWithF(from, true))
                return to + url[from.Length..];
        return null;
    }

    /// <summary>
    ///     获取指定服务端类型的版本列表（推荐入口）。必须在工作线程执行。
    /// </summary>
    public static List<ServerVersionEntry> GetVersions(ServerType type)
    {
        return type switch
        {
            ServerType.Paper => GetPaperVersions(),
            ServerType.Purpur => GetPurpurVersions(),
            ServerType.Spigot => GetSpigotVersions(),
            ServerType.Vanilla => GetVanillaVersions(),
            ServerType.Forge => GetForgeVersions(),
            ServerType.Fabric => GetFabricVersions(),
            _ => throw new NotSupportedException("Unknown server type: " + type)
        };
    }

    // ---------- Vanilla（Mojang manifest，仅取有 server jar 的版本） ----------

    private static List<ServerVersionEntry> GetVanillaVersions()
    {
        var manifest = FetchJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var result = new List<ServerVersionEntry>();
        foreach (var entry in (JsonArray)manifest["versions"] ?? new JsonArray())
        {
            result.Add(new ServerVersionEntry
            {
                Version = entry["id"]?.ToString() ?? "",
                Detail = entry["type"]?.ToString() == "old_beta" || entry["type"]?.ToString() == "old_alpha"
                    ? Lang.Text("Download.Client.Type.Old")
                    : "",
                // 具体下载 URL 需按版本清单二次查询，下载时再解析（见 ResolveVanillaUrl）
                Url = entry["url"]?.ToString()
            });
        }

        return result.Take(300).ToList(); // manifest 很长，截取近期版本
    }

    /// <summary>从版本清单 JSON 解析服务端 jar 下载地址（下载时调用）。</summary>
    private static string ResolveVanillaUrl(string versionMetaUrl)
    {
        var meta = FetchJson(versionMetaUrl);
        var url = meta["downloads"]?["server"]?["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));
        return url;
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

    // ---------- Spigot（getbukkit 聚合下载，无列表 API → 用 Mojang 版本号映射） ----------

    private static List<ServerVersionEntry> GetSpigotVersions()
    {
        // getbukkit 未提供列表接口；以 Mojang release 版本为索引（Spigot 支持主流 release 版）
        var manifest = FetchJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var result = new List<ServerVersionEntry>();
        foreach (var entry in (JsonArray)manifest["versions"] ?? new JsonArray())
        {
            if (entry["type"]?.ToString() != "release") continue;
            var ver = entry["id"]?.ToString() ?? "";
            result.Add(new ServerVersionEntry
            {
                Version = ver,
                Url = $"https://download.getbukkit.org/spigot/spigot-{ver}.jar"
            });
        }

        return result.Take(80).ToList();
    }

    // ---------- Forge（BMCLAPI 列表接口，取每个 MC 版本最新的安装器） ----------

    private static List<ServerVersionEntry> GetForgeVersions()
    {
        // BMCLAPI 的 /forge/minecraft 返回支持的 MC 版本列表（国内快，PCL 原生同款源）
        var list = FetchJsonArray("https://bmclapi2.bangbang93.com/forge/minecraft");
        var result = new List<ServerVersionEntry>();
        foreach (var mcNode in list.AsArray())
        {
            var mc = mcNode?.ToString();
            if (string.IsNullOrWhiteSpace(mc)) continue;
            result.Add(new ServerVersionEntry
            {
                Version = mc,
                // 具体安装器版本在下载时按该 MC 版本取最新构建（避免列表阶段 N 次请求）
                Url = ""
            });
        }

        // 按版本号降序
        result.Sort((a, b) => McVersionComparer.CompareVersion(b.Version, a.Version));
        return result;
    }

    /// <summary>解析 Forge 指定 MC 版本最新构建的安装器下载地址（下载时调用，BMCLAPI maven 直链）。</summary>
    private static (string Url, string? Sha1) ResolveForge(string mcVersion)
    {
        var builds = FetchJsonArray($"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}");
        var latest = (builds.AsArray().FirstOrDefault() as JsonObject) ??
                     throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));
        var forgeVer = latest["version"]?.ToString();
        var full = $"{mcVersion}-{forgeVer}";
        // 老版本（如 1.7.10）的构建名带 mc 版本后缀
        if (builds.AsArray().Any(b => b?["version"]?.ToString() == $"{forgeVer}-{mcVersion}"))
            full = $"{mcVersion}-{forgeVer}-{mcVersion}";
        var sha1 = (latest["files"] as JsonArray)?.FirstOrDefault(f => f?["category"]?.ToString() == "installer")?["hash"]
            ?.ToString();
        return ($"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{full}/forge-{full}-installer.jar", sha1);
    }

    // ---------- Fabric（meta.fabricmc.net，取最新稳定安装器） ----------

    private static List<ServerVersionEntry> GetFabricVersions()
    {
        var game = FetchJsonArray("https://meta.fabricmc.net/v2/versions/game");
        var installer = FetchJsonArray("https://meta.fabricmc.net/v2/versions/installer");
        var installerVer = (installer[0] as JsonObject)?["version"]?.ToString() ?? "";
        var result = new List<ServerVersionEntry>();
        foreach (var entry in game)
        {
            if (entry["stable"] is JsonNode st && !st.GetValue<bool>()) continue;
            var ver = entry["version"]?.ToString() ?? "";
            result.Add(new ServerVersionEntry
            {
                Version = ver,
                Detail = $"installer {installerVer}",
                // Fabric 服务端安装器：server profile 需 loader 版本，这里用最新 loader
                Url = "" // 下载时解析（需 loader 版本号），见 ResolveFabric
            });
        }

        return result.Take(60).ToList();
    }

    private static (string InstallerUrl, string LoaderVersion) ResolveFabric(string mcVersion)
    {
        var loader = FetchJsonArray("https://meta.fabricmc.net/v2/versions/loader");
        var loaderVer = (loader[0] as JsonObject)?["version"]?.ToString() ?? "";
        var installer = FetchJsonArray("https://meta.fabricmc.net/v2/versions/installer");
        var installerVer = (installer[0] as JsonObject)?["version"]?.ToString() ?? "";
        var url = $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar";
        return (url, loaderVer);
    }

    #region 构建列表

    /// <summary>
    ///     获取指定服务端类型与游戏版本的构建列表（仅 Paper / Purpur 有构建概念）。
    ///     返回的构建号为从新到旧的字符串列表；其他类型返回空列表。必须在工作线程执行。
    /// </summary>
    public static List<string> GetBuilds(ServerType type, string version)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(version)) return result;
        switch (type)
        {
            case ServerType.Paper:
            {
                // Paper v3：/versions/{v}/builds → [{id, channel, downloads}]
                var builds = FetchJsonArray(
                    $"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds");
                foreach (var build in builds)
                {
                    var id = build?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
                }

                result.Reverse(); // 从新到旧
                break;
            }
            case ServerType.Purpur:
            {
                var info = FetchJson($"https://api.purpurmc.org/v2/purpur/{version}");
                foreach (var build in (JsonArray)info["builds"]?["all"] ?? new JsonArray())
                {
                    var id = build?.ToString();
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
                }

                result.Reverse();
                break;
            }
        }

        return result;
    }

    #endregion

    #region 下载

    /// <summary>一次服务端下载的解析结果。</summary>
    public class DownloadSpec
    {
        /// <summary>下载地址列表（镜像优先，官方回退）。</summary>
        public List<string> Urls = [];

        /// <summary>期望哈希（按长度自动识别 MD5/SHA1/SHA256），未知为 null。</summary>
        public string? Hash;

        /// <summary>建议文件名。</summary>
        public string FileName = "server.jar";

        /// <summary>附加说明（如 Spigot 的编译指引），无则为空。</summary>
        public string? Note;

        /// <summary>是否使用浏览器 UA 下载（镜像不需要产品 UA）。</summary>
        public bool UseBrowserUserAgent;

        /// <summary>自定义 UA（直连 PaperMC Fill v3 时必须携带可识别标识）。</summary>
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
            case ServerType.Vanilla:
            {
                // Mojang 版本清单需二次解析出 server jar 地址
                var manifest = FetchJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
                var entry = (JsonArray)manifest["versions"] ?? new JsonArray();
                var target = entry.FirstOrDefault(e => e?["id"]?.ToString() == version);
                var metaUrl = target?["url"]?.ToString();
                if (string.IsNullOrWhiteSpace(metaUrl))
                    throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));
                url = ResolveVanillaUrl(metaUrl);
                spec.FileName = $"server-{version}.jar";
                break;
            }
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
                // Spigot 无官方直链：提供官方 BuildTools 与编译指引
                url = "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";
                spec.FileName = "BuildTools.jar";
                spec.Note = Lang.Text("Download.Server.Spigot.Note", version);
                break;
            }
            case ServerType.Forge:
            {
                var (forgeUrl, forgeSha1) = ResolveForge(version);
                url = forgeUrl;
                spec.Hash = forgeSha1;
                var marker = "forge-";
                var part = url.Contains(marker) ? url[(url.IndexOf(marker) + marker.Length)..] : "installer";
                var full = part.Contains("-installer") ? part.Split("-installer")[0] : part;
                spec.FileName = $"forge-{full}-installer.jar";
                break;
            }
            case ServerType.Fabric:
            {
                var (installerUrl, _) = ResolveFabric(version);
                url = installerUrl;
                spec.FileName = installerUrl[(installerUrl.LastIndexOf('/') + 1)..];
                break;
            }
            default:
                throw new NotSupportedException("Unknown server type: " + type);
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));

        // 镜像优先 + 官方回退（DownloadFile 支持多 URL 自动换源）
        var mirrorUrl = ToBmclapiMirror(url);
        spec.Urls = mirrorUrl is not null ? [mirrorUrl, url] : [url];
        // PaperMC Fill v3 要求可识别的 UA：下载产物时以产品 UA 直连（镜像无此要求）
        spec.UseBrowserUserAgent = mirrorUrl is not null;
        if (mirrorUrl is null) spec.CustomUserAgent = $"PCL2-BeeEdition/{ModBase.versionBaseName}";
        return spec;
    }

    /// <summary>
    ///     按解析结果启动下载任务，返回已启动的加载器（进度可轮询 <c>loader.Progress</c>）。
    /// </summary>
    public static LoaderCombo<int> StartDownload(DownloadSpec spec, string targetPath)
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
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        loader.Start(0);
        return loader;
    }

    /// <summary>
    ///     下载服务端文件到指定目录（列表点选式入口，自动命名为官方文件名）。
    ///     返回已启动的加载器。
    /// </summary>
    public static LoaderCombo<int> Download(ServerType type, ServerVersionEntry entry, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var spec = Resolve(type, entry.Version);
        return StartDownload(spec, Path.Combine(targetFolder, spec.FileName));
    }

    #endregion
}
