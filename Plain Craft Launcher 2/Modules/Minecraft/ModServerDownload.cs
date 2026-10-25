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
        return (JsonObject)ModBase.GetJson(Requester.Fetch(url, new FetchParam
        {
            Method = "GET",
            ContentType = "application/json",
            Timeout = 15000,
            UseBrowserUserAgent = true
        }));
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
        // versions 按大版本分组（"1.21": [...]），展平各组内最新稳定版
        foreach (var group in versions ?? new JsonObject())
        foreach (var ver in (JsonArray)group.Value ?? new JsonArray())
        {
            // 跳过 pre/rc 快照（含 '-' 的为非稳定版）
            if (ver.ToString().Contains('-')) continue;
            result.Add(new ServerVersionEntry { Version = ver.ToString() });
            break; // 每组只取最新稳定版（数组首项为最新）
        }

        return result;
    }

    /// <summary>解析 Paper 指定版本最新构建的下载信息（下载时调用）。</summary>
    private static (string Url, string Sha256) ResolvePaper(string version)
    {
        var build = FetchJson($"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds/latest");
        var download = build["downloads"]?["server:default"];
        var url = download?["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));
        var sha256 = download?["checksums"]?["sha256"]?.ToString();
        return (url, sha256);
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

    // ---------- Forge（官方 promotions，取推荐/最新安装器） ----------

    private static List<ServerVersionEntry> GetForgeVersions()
    {
        // 复用 PCL 已缓存的 Forge 版本列表（dlForgeListLoader 的结构太重，这里直接用 promotions + maven 索引）
        var promos = FetchJson("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");
        var promo = promos["promos"] as JsonObject;
        var recommended = new Dictionary<string, string>();
        var latest = new Dictionary<string, string>();
        foreach (var kv in promo ?? new JsonObject())
        {
            if (kv.Key.EndsWithF("-recommended", true)) recommended[kv.Key.Split('-')[0]] = kv.Value.ToString();
            if (kv.Key.EndsWithF("-latest", true)) latest[kv.Key.Split('-')[0]] = kv.Value.ToString();
        }

        var result = new List<ServerVersionEntry>();
        foreach (var mc in recommended.Keys.OrderByDescending(v => v, Comparer<string>.Create((a, b) => McVersionComparer.CompareVersion(a, b))))
        {
            var forgeVer = recommended[mc];
            result.Add(new ServerVersionEntry
            {
                Version = mc,
                Detail = forgeVer,
                Url = ForgeInstallerUrl(mc, forgeVer)
            });
        }

        foreach (var mc in latest.Keys.Where(k => !recommended.ContainsKey(k))
                     .OrderByDescending(v => v, Comparer<string>.Create((a, b) => McVersionComparer.CompareVersion(a, b))))
        {
            var forgeVer = latest[mc];
            result.Add(new ServerVersionEntry
            {
                Version = mc,
                Detail = forgeVer,
                Url = ForgeInstallerUrl(mc, forgeVer)
            });
        }

        return result;
    }

    /// <summary>构造 Forge 安装器 jar 下载地址（maven 结构）。</summary>
    private static string ForgeInstallerUrl(string mcVersion, string forgeVersion)
    {
        var full = $"{mcVersion}-{forgeVersion}";
        // 部分老版本带 -1.7.10 之类的后缀，直接拼接
        return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
    }

    // ---------- Fabric（meta.fabricmc.net，取最新稳定安装器） ----------

    private static List<ServerVersionEntry> GetFabricVersions()
    {
        var game = FetchJson("https://meta.fabricmc.net/v2/versions/game").AsArray();
        var installer = FetchJson("https://meta.fabricmc.net/v2/versions/installer");
        var installerVer = (installer[0] as JsonObject)?["version"]?.ToString() ?? "";
        var result = new List<ServerVersionEntry>();
        foreach (var entry in (JsonArray)game ?? new JsonArray())
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
        var loader = FetchJson("https://meta.fabricmc.net/v2/versions/loader");
        var loaderVer = (loader[0] as JsonObject)?["version"]?.ToString() ?? "";
        var installer = FetchJson("https://meta.fabricmc.net/v2/versions/installer");
        var installerVer = (installer[0] as JsonObject)?["version"]?.ToString() ?? "";
        var url = $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar";
        return (url, loaderVer);
    }

    #region 下载

    /// <summary>
    ///     下载服务端文件到指定目录。Vanilla/Paper 在此时解析真实 URL。
    ///     返回启动的任务加载器。
    /// </summary>
    public static ModLoader.LoaderCombo<int> Download(ServerType type, ServerVersionEntry entry, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        string url = entry.Url;
        string sha256 = entry.Sha256;

        // 需要二次解析的源
        if (type == ServerType.Vanilla && !string.IsNullOrEmpty(url) && url.StartsWithF("http") &&
            url.Contains("piston-meta"))
            url = ResolveVanillaUrl(url);
        else if (type == ServerType.Paper)
        {
            var (u, s) = ResolvePaper(entry.Version);
            url = u;
            sha256 = s;
        }
        else if (type == ServerType.Purpur)
        {
            // Purpur：取该版本最新构建
            var info = FetchJson($"https://api.purpurmc.org/v2/purpur/{entry.Version}");
            var latestBuild = info["builds"]?["latest"]?.ToString() ?? "";
            url = $"https://api.purpurmc.org/v2/purpur/{entry.Version}/{latestBuild}/download";
        }
        else if (type == ServerType.Fabric)
        {
            var (installerUrl, _) = ResolveFabric(entry.Version);
            url = installerUrl;
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Download.Server.Error.NoServerJar"));

        // 文件名：按类型与版本生成
        var fileName = type switch
        {
            ServerType.Vanilla => $"server-{entry.Version}.jar",
            ServerType.Paper => $"paper-{entry.Version}.jar",
            ServerType.Purpur => $"purpur-{entry.Version}.jar",
            ServerType.Spigot => $"spigot-{entry.Version}.jar",
            ServerType.Forge => $"forge-{entry.Version}-{entry.Detail}-installer.jar",
            ServerType.Fabric => $"fabric-installer-{entry.Version}.jar",
            _ => $"server-{entry.Version}.jar"
        };
        var targetPath = Path.Combine(targetFolder, fileName);

        var loaders = new List<LoaderBase>
        {
            new LoaderDownload(Lang.Text("Download.Server.Task.Download"),
                [new DownloadFile([url], targetPath,
                    string.IsNullOrEmpty(sha256) ? null : new ModBase.FileChecker(hash: sha256))])
        };
        var loader = new LoaderCombo<int>(Lang.Text("Download.Server.Task.Download"), loaders)
        {
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        loader.Start(0);
        return loader;
    }

    #endregion
}
