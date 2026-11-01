// ─────────────────────────────────────────────────────────────────────────────
// 服务器管理模块：移植自 ErieneNeko/PCL-CE-ServerControlPad
//   https://github.com/ErieneNeko/PCL-CE-ServerControlPad
//   原作者：ErieneNeko    许可：Apache License 2.0
//   本文件在原作基础上做了适配修改（命名空间/本地化/对话框 API 对齐本项目）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.IO.Net;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 从各服务端官方接口读取版本与下载地址。
/// 所有地址均来自官方 API / Maven，不伪造接口。
/// </summary>
public sealed class ServerCatalog
{
    private const string UserAgent = "PCL-CE-ServerManager";
    private static readonly HttpClient FallbackClient = CreateFallbackClient();

    private readonly HttpClient _client;

    public ServerCatalog(HttpClient? client = null)
    {
        // 版本元数据接口使用独立的 SocketsHttpHandler 客户端。
        // 启动器共享客户端启用了 DoH / 自定义连接逻辑，在部分网络下访问
        // maven.neoforged.net 会出现 TLS EOF；独立客户端可以直接回退到系统 DNS。
        _client = client ?? FallbackClient;
    }

    public async Task<IReadOnlyList<ServerVersionOption>> GetGameVersionsAsync(
        ServerKind kind, CancellationToken cancellationToken = default)
    {
        return kind switch
        {
            ServerKind.Vanilla => await GetVanillaVersionsAsync(cancellationToken).ConfigureAwait(false),
            ServerKind.Paper => await GetPaperVersionsAsync(cancellationToken).ConfigureAwait(false),
            ServerKind.Purpur => await GetPurpurVersionsAsync(cancellationToken).ConfigureAwait(false),
            ServerKind.Fabric => await GetFabricGameVersionsAsync(cancellationToken).ConfigureAwait(false),
            ServerKind.Forge => await GetForgeVersionsAsync(cancellationToken).ConfigureAwait(false),
            ServerKind.NeoForge => await GetNeoForgeVersionsAsync(cancellationToken).ConfigureAwait(false),
            _ => []
        };
    }

    public async Task<IReadOnlyList<ServerVersionOption>> GetPackageVersionsAsync(
        ServerKind kind, string minecraftVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return [];

        return kind switch
        {
            ServerKind.Paper => await GetPaperBuildsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ServerKind.Purpur => await GetPurpurBuildsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ServerKind.Fabric => await GetFabricLoadersAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ServerKind.Forge => await GetForgeBuildAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ServerKind.NeoForge => await GetNeoForgeBuildsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            _ => []
        };
    }

    public async Task<ServerDownloadPlan> ResolveAsync(
        ServerCreateOptions options, string serverDirectory, CancellationToken cancellationToken = default)
    {
        var plan = new ServerDownloadPlan
        {
            Kind = options.Kind,
            MinecraftVersion = options.MinecraftVersion,
            PackageVersion = options.PackageVersion,
            JavaPath = options.JavaPath,
            ServerDirectory = serverDirectory,
            ServerName = options.Name
        };

        switch (options.Kind)
        {
            case ServerKind.Vanilla:
            {
                var versionUrl = await GetVanillaVersionUrlAsync(options.MinecraftVersion, cancellationToken)
                    .ConfigureAwait(false);
                if (versionUrl is null)
                    throw new InvalidOperationException($"未找到 Minecraft {options.MinecraftVersion} 的官方服务端。");
                var json = await GetJsonAsync(versionUrl, cancellationToken).ConfigureAwait(false);
                var server = json?["downloads"]?["server"];
                var url = server?["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException(
                        $"Minecraft {options.MinecraftVersion} 没有提供官方服务端下载。");
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = url,
                    FileName = "server.jar",
                    Sha1 = server?["sha1"]?.GetValue<string>() ?? "",
                    Size = server?["size"]?.GetValue<long>() ?? -1
                });
                plan.LaunchMode = "jar";
                break;
            }
            case ServerKind.Paper:
            {
                var builds = await GetPaperBuildsRawAsync(options.MinecraftVersion, cancellationToken)
                    .ConfigureAwait(false);
                if (builds.Count == 0)
                    throw new InvalidOperationException($"Paper 没有适用于 Minecraft {options.MinecraftVersion} 的构建。");
                var selected = SelectPaperBuild(builds, options.PackageVersion);
                var download = selected?["downloads"]?["server:default"];
                var url = download?["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("Paper 构建缺少服务端下载地址。");
                plan.PackageVersion = selected?["id"]?.GetValue<int>().ToString() ?? options.PackageVersion;
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = url,
                    FileName = "server.jar",
                    Sha256 = download?["checksums"]?["sha256"]?.GetValue<string>() ?? "",
                    Size = download?["size"]?.GetValue<long>() ?? -1
                });
                plan.LaunchMode = "jar";
                break;
            }
            case ServerKind.Purpur:
            {
                var info = await GetPurpurVersionInfoAsync(options.MinecraftVersion, cancellationToken)
                    .ConfigureAwait(false);
                var builds = info?["builds"]?["all"]?.AsArray().Select(node => node?.GetValue<string>() ?? "")
                    .Where(value => value.Length > 0).ToList() ?? [];
                if (builds.Count == 0)
                    throw new InvalidOperationException($"Purpur 没有适用于 Minecraft {options.MinecraftVersion} 的构建。");
                var selected = string.IsNullOrWhiteSpace(options.PackageVersion)
                    ? info?["builds"]?["latest"]?.GetValue<string>() ?? builds[^1]
                    : options.PackageVersion;
                if (!builds.Contains(selected))
                    selected = builds[^1];
                plan.PackageVersion = selected;
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(options.MinecraftVersion)}/{Uri.EscapeDataString(selected)}/download",
                    FileName = "server.jar"
                });
                plan.LaunchMode = "jar";
                break;
            }
            case ServerKind.Fabric:
            {
                var loader = options.PackageVersion;
                if (string.IsNullOrWhiteSpace(loader))
                {
                    var loaders = await GetFabricLoadersAsync(options.MinecraftVersion, cancellationToken)
                        .ConfigureAwait(false);
                    loader = loaders.FirstOrDefault()?.Version
                             ?? throw new InvalidOperationException("Fabric 没有可用的 Loader 版本。");
                }

                plan.PackageVersion = loader;
                // Fabric 官方已不再提供 /server/jar 直达文件，改用官方安装器的 server 子命令。
                var installerUrl = await GetFabricInstallerUrlAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(installerUrl))
                    throw new InvalidOperationException("未读取到 Fabric 官方安装器地址。");
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = installerUrl,
                    FileName = "fabric-installer.jar"
                });
                plan.LaunchMode = "jar";
                break;
            }
            case ServerKind.Forge:
            {
                var forgeVersion = options.PackageVersion;
                if (string.IsNullOrWhiteSpace(forgeVersion))
                {
                    var builds = await GetForgeBuildAsync(options.MinecraftVersion, cancellationToken)
                        .ConfigureAwait(false);
                    forgeVersion = builds.FirstOrDefault()?.Version;
                }

                if (string.IsNullOrWhiteSpace(forgeVersion))
                    throw new InvalidOperationException($"Forge 没有适用于 Minecraft {options.MinecraftVersion} 的版本。");
                plan.PackageVersion = forgeVersion;
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Uri.EscapeDataString(options.MinecraftVersion)}-{Uri.EscapeDataString(forgeVersion)}/forge-{Uri.EscapeDataString(options.MinecraftVersion)}-{Uri.EscapeDataString(forgeVersion)}-installer.jar",
                    FileName = "forge-installer.jar"
                });
                plan.LaunchMode = "forgeRun";
                break;
            }
            case ServerKind.NeoForge:
            {
                var neoVersion = options.PackageVersion;
                if (string.IsNullOrWhiteSpace(neoVersion))
                {
                    var builds = await GetNeoForgeBuildsAsync(options.MinecraftVersion, cancellationToken)
                        .ConfigureAwait(false);
                    neoVersion = builds.FirstOrDefault()?.Version;
                }

                if (string.IsNullOrWhiteSpace(neoVersion))
                    throw new InvalidOperationException(
                        $"NeoForge 没有适用于 Minecraft {options.MinecraftVersion} 的版本。");
                plan.PackageVersion = neoVersion;
                var legacyNeoForge = neoVersion.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase);
                plan.Artifacts.Add(new ServerDownloadArtifact
                {
                    Url = legacyNeoForge
                        ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{Uri.EscapeDataString(neoVersion)}/forge-{Uri.EscapeDataString(neoVersion)}-installer.jar"
                        : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Uri.EscapeDataString(neoVersion)}/neoforge-{Uri.EscapeDataString(neoVersion)}-installer.jar",
                    FileName = "neoforge-installer.jar"
                });
                plan.LaunchMode = "forgeRun";
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Kind));
        }

        if (plan.Artifacts.Count == 0)
            throw new InvalidOperationException("没有解析到可下载的服务端文件。");
        return plan;
    }

    #region Vanilla

    private async Task<IReadOnlyList<ServerVersionOption>> GetVanillaVersionsAsync(CancellationToken ct)
    {
        var manifest = await GetJsonAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct).ConfigureAwait(false);
        var versions = manifest?["versions"]?.AsArray() ?? [];
        return versions
            .Where(node => node?["type"]?.GetValue<string>() == "release")
            .Select(node => new ServerVersionOption
            {
                Version = node?["id"]?.GetValue<string>() ?? "",
                Label = node?["id"]?.GetValue<string>() ?? ""
            })
            .Where(option => option.Version.Length > 0)
            .ToList();
    }

    private async Task<string?> GetVanillaVersionUrlAsync(string minecraftVersion, CancellationToken ct)
    {
        var manifest = await GetJsonAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct).ConfigureAwait(false);
        return manifest?["versions"]?.AsArray()
            .FirstOrDefault(node => node?["id"]?.GetValue<string>() == minecraftVersion)?["url"]?.GetValue<string>();
    }

    #endregion

    #region Paper

    private async Task<IReadOnlyList<ServerVersionOption>> GetPaperVersionsAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", ct).ConfigureAwait(false);
        var versionsNode = json?["versions"] as JsonObject;
        var result = new List<ServerVersionOption>();
        if (versionsNode is null)
            return result;

        foreach (var group in versionsNode)
        {
            if (group.Value is not JsonArray groupVersions)
                continue;
            foreach (var node in groupVersions)
            {
                var version = node?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(version) || result.Any(item => item.Version == version))
                    continue;
                result.Add(new ServerVersionOption
                {
                    Version = version,
                    Label = version,
                    IsStable = !version.Contains("-pre", StringComparison.OrdinalIgnoreCase) &&
                               !version.Contains("-rc", StringComparison.OrdinalIgnoreCase) &&
                               !version.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return result
            .OrderByDescending(option => option.IsStable)
            .ThenByDescending(option => option.Version, ServerVersionComparer.Instance)
            .ToList();
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetPaperBuildsAsync(string mcVersion, CancellationToken ct)
    {
        var builds = await GetPaperBuildsRawAsync(mcVersion, ct).ConfigureAwait(false);
        return builds.Select(node => new ServerVersionOption
            {
                Version = node?["id"]?.GetValue<int>().ToString() ?? "",
                Label = $"#{node?["id"]?.GetValue<int>()} ({node?["channel"]?.GetValue<string>() ?? "UNKNOWN"})",
                IsStable = node?["channel"]?.GetValue<string>() == "STABLE"
            })
            .Where(option => option.Version.Length > 0)
            .OrderByDescending(option => option.IsStable)
            .ThenByDescending(option => int.TryParse(option.Version, out var build) ? build : 0)
            .ToList();
    }

    private async Task<JsonArray> GetPaperBuildsRawAsync(string mcVersion, CancellationToken ct)
    {
        var json = await GetJsonAsync(
            $"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(mcVersion)}/builds",
            ct).ConfigureAwait(false);
        return json as JsonArray ?? [];
    }

    private static JsonNode? SelectPaperBuild(JsonArray builds, string packageVersion)
    {
        if (!string.IsNullOrWhiteSpace(packageVersion) &&
            int.TryParse(packageVersion, out var buildId))
        {
            var exact = builds.FirstOrDefault(node => node?["id"]?.GetValue<int>() == buildId);
            if (exact is not null)
                return exact;
        }

        return builds.FirstOrDefault(node => node?["channel"]?.GetValue<string>() == "STABLE")
               ?? builds.FirstOrDefault();
    }

    #endregion

    #region Purpur

    private async Task<IReadOnlyList<ServerVersionOption>> GetPurpurVersionsAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://api.purpurmc.org/v2/purpur", ct).ConfigureAwait(false);
        return (json?["versions"]?.AsArray() ?? [])
            .Select(node => node?.GetValue<string>() ?? "")
            .Where(version => version.Length > 0)
            .OrderByDescending(version => version, ServerVersionComparer.Instance)
            .Select(version => new ServerVersionOption { Version = version, Label = version })
            .ToList();
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetPurpurBuildsAsync(string mcVersion, CancellationToken ct)
    {
        var info = await GetPurpurVersionInfoAsync(mcVersion, ct).ConfigureAwait(false);
        return (info?["builds"]?["all"]?.AsArray() ?? [])
            .Select(node => node?.GetValue<string>() ?? "")
            .Where(build => build.Length > 0)
            .Reverse()
            .Select(build => new ServerVersionOption { Version = build, Label = $"#{build}" })
            .ToList();
    }

    private async Task<JsonNode?> GetPurpurVersionInfoAsync(string mcVersion, CancellationToken ct)
    {
        return await GetJsonAsync(
            $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(mcVersion)}", ct).ConfigureAwait(false);
    }

    #endregion

    #region Fabric

    private async Task<IReadOnlyList<ServerVersionOption>> GetFabricGameVersionsAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/game", ct).ConfigureAwait(false);
        return (json?.AsArray() ?? [])
            .Where(node => node?["stable"]?.GetValue<bool>() == true)
            .Select(node => node?["version"]?.GetValue<string>() ?? "")
            .Where(version => version.Length > 0)
            .Select(version => new ServerVersionOption { Version = version, Label = version })
            .ToList();
    }

    private async Task<string> GetFabricInstallerUrlAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct).ConfigureAwait(false);
        var array = json?.AsArray() ?? [];
        foreach (var node in array)
        {
            if (node?["stable"]?.GetValue<bool>() == true)
            {
                var stableUrl = node?["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(stableUrl))
                    return stableUrl;
            }
        }

        return array.FirstOrDefault()?["url"]?.GetValue<string>() ?? "";
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetFabricLoadersAsync(string mcVersion, CancellationToken ct)
    {
        var json = await GetJsonAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}", ct)
            .ConfigureAwait(false);
        return (json?.AsArray() ?? [])
            .Select(node => new
            {
                Version = node?["loader"]?["version"]?.GetValue<string>() ?? "",
                Stable = node?["loader"]?["stable"]?.GetValue<bool>() ?? true
            })
            .Where(item => item.Version.Length > 0)
            .Select(item => new ServerVersionOption
            {
                Version = item.Version,
                Label = item.Version + (item.Stable ? "" : " (beta)"),
                IsStable = item.Stable
            })
            .ToList();
    }

    #endregion

    #region Forge / NeoForge

    private async Task<IReadOnlyList<ServerVersionOption>> GetForgeVersionsAsync(CancellationToken ct)
    {
        var promos = await GetForgePromosAsync(ct).ConfigureAwait(false);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in promos)
        {
            if (!property.Key.EndsWith("-recommended", StringComparison.OrdinalIgnoreCase) &&
                !property.Key.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
                continue;
            var mcVersion = property.Key[..property.Key.LastIndexOf('-')];
            if (!versions.ContainsKey(mcVersion))
                versions[mcVersion] = property.Value?.GetValue<string>() ?? "";
        }

        return versions.Keys
            .OrderByDescending(version => version, ServerVersionComparer.Instance)
            .Select(version => new ServerVersionOption { Version = version, Label = version })
            .ToList();
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetForgeBuildAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            var all = await GetMavenVersionsAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct)
                .ConfigureAwait(false);
            var prefix = mcVersion + "-";
            var versions = all
                .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(version => version[prefix.Length..])
                .Where(version => version.Length > 0)
                .OrderByDescending(version => version, ServerVersionComparer.Instance)
                .Select(version => new ServerVersionOption { Version = version, Label = version })
                .ToList();
            if (versions.Count > 0)
                return versions;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Server", $"读取 Forge {mcVersion} 构建列表失败，尝试使用推荐版本。");
        }

        var promos = await GetForgePromosAsync(ct).ConfigureAwait(false);
        var recommended = promos[$"{mcVersion}-recommended"]?.GetValue<string>()
                          ?? promos[$"{mcVersion}-latest"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(recommended)
            ? []
            : [new ServerVersionOption { Version = recommended, Label = recommended }];
    }

    private async Task<JsonObject> GetForgePromosAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)
            .ConfigureAwait(false);
        return json?["promos"] as JsonObject ?? [];
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetNeoForgeVersionsAsync(CancellationToken ct)
    {
        var all = new List<string>();
        try
        {
            all.AddRange(await GetNeoForgeAllVersionsAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Server", "读取 NeoForge 新版版本列表失败。");
        }

        try
        {
            var legacy = await GetNeoForgeLegacyVersionsAsync(ct).ConfigureAwait(false);
            if (legacy.Count > 0)
                all.Add("1.20.1-legacy");
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Server", "读取 NeoForge 1.20.1 旧版版本列表失败。");
        }

        if (all.Count == 0)
            throw new InvalidOperationException("读取 NeoForge 版本列表失败，请检查网络后重试。");

        return all
            .Select(version => version == "1.20.1-legacy"
                ? new ServerVersionOption { Version = "1.20.1", Label = "1.20.1 (legacy)" }
                : new ServerVersionOption
                {
                    Version = NeoForgeToMinecraftVersion(version),
                    Label = NeoForgeToMinecraftVersion(version)
                })
            .Where(option => option.Version.Length > 0)
            .GroupBy(option => option.Version)
            .Select(group => group.First())
            .OrderByDescending(option => option.Version, ServerVersionComparer.Instance)
            .ToList();
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetNeoForgeBuildsAsync(string mcVersion, CancellationToken ct)
    {
        if (mcVersion == "1.20.1")
        {
            var legacy = await GetNeoForgeLegacyVersionsAsync(ct).ConfigureAwait(false);
            return legacy
                .OrderByDescending(version => version, ServerVersionComparer.Instance)
                .Select(version => new ServerVersionOption { Version = version, Label = version })
                .ToList();
        }

        var all = await GetNeoForgeAllVersionsAsync(ct).ConfigureAwait(false);
        return all
            .Where(version => NeoForgeToMinecraftVersion(version) == mcVersion)
            .OrderByDescending(version => version, ServerVersionComparer.Instance)
            .Select(version => new ServerVersionOption { Version = version, Label = version })
            .ToList();
    }

    private async Task<List<string>> GetNeoForgeAllVersionsAsync(CancellationToken ct) =>
        await GetMavenVersionsAsync(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// NeoForge 1.20.1 沿用 Forge 的 47.x 版本号，并且位于 net/neoforged/forge 构件下。
    /// </summary>
    private async Task<List<string>> GetNeoForgeLegacyVersionsAsync(CancellationToken ct) =>
        (await GetMavenVersionsAsync(
                "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml", ct)
            .ConfigureAwait(false))
        .Where(version => version.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
        .ToList();

    private async Task<List<string>> GetMavenVersionsAsync(string url, CancellationToken ct)
    {
        var xml = await GetStringAsync(url, ct).ConfigureAwait(false);
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);
            return document.Descendants("version")
                .Select(node => node.Value.Trim())
                .Where(version => version.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解析 Maven 版本列表失败：{url}", ex);
        }
    }

    /// <summary>
    /// NeoForge 的版本号规则：20.2.x → 1.20.2，21.1.x → 1.21.1，26.3.x → 26.3；
    /// 1.20.1-47.x 为 NeoForge 1.20.1 旧版。
    /// </summary>
    public static string NeoForgeToMinecraftVersion(string neoVersion)
    {
        if (neoVersion.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
            return "1.20.1";
        var cleaned = neoVersion.Split('+')[0];
        var parts = cleaned.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return "";
        if (major >= 26)
            return $"{major}.{minor}";
        if (minor == 0)
            return $"1.{major}";
        return $"1.{major}.{minor}";
    }

    #endregion

    #region HTTP

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonNode.Parse(text);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        Exception? lastError = null;
        var clients = ReferenceEquals(_client, FallbackClient)
            ? new[] { FallbackClient }
            : new[] { _client, FallbackClient };
        foreach (var client in clients)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd(UserAgent);
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true
                    };
                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = new TimeoutException("请求官方版本接口超时。");
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }

                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"读取服务端版本信息失败：{lastError?.Message}", lastError);
    }

    private static HttpClient CreateFallbackClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-CE", "server"));
        return client;
    }

    #endregion
}

/// <summary>
/// 兼容 1.x 与 26.x 之类版本号的大小比较。
/// </summary>
public sealed class ServerVersionComparer : IComparer<string>
{
    public static ServerVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xVersion = Parse(x);
        var yVersion = Parse(y);
        if (xVersion is not null && yVersion is not null)
        {
            var compare = xVersion.CompareTo(yVersion);
            if (compare != 0) return compare;
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static Version? Parse(string value)
    {
        var cleaned = value.Split('-', '+')[0];
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}
