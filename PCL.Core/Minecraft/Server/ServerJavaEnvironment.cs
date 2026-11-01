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

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Java;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 服务端运行环境检查：根据 Minecraft 版本找出所需的 Java 主版本，并从启动器已扫描的 Java 中选择。
/// </summary>
public static partial class ServerJavaEnvironment
{
    private static readonly Dictionary<string, int> RequiredJavaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>
    /// 根据 Minecraft 版本号推断最低 Java 主版本。作为无法读取版本 JSON 时的后备逻辑。
    /// </summary>
    public static int GetFallbackRequiredMajor(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('-', '+')[0].Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
            return 21;

        if (major >= 26)
            return 21;
        if (major != 1)
            return 21;
        if (minor >= 20)
            return 21;
        if (minor >= 18)
            return 17;
        if (minor == 17)
            return 16;
        return 8;
    }

    /// <summary>
    /// 读取 Mojang 版本 JSON 的 javaVersion.majorVersion，得到准确的最低 Java 要求。
    /// 26.x 等新版本可能要求高于 21 的 Java。
    /// </summary>
    public static async Task<int> GetRequiredMajorAsync(
        string minecraftVersion, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        lock (CacheLock)
        {
            if (RequiredJavaCache.TryGetValue(minecraftVersion, out var cached))
                return cached;
        }

        var fallback = GetFallbackRequiredMajor(minecraftVersion);
        try
        {
            using var ownedClient = client is null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
                : null;
            var http = client ?? ownedClient!;
            var manifestText = await http.GetStringAsync(
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", cancellationToken)
                .ConfigureAwait(false);
            var versionUrl = JsonNode.Parse(manifestText)?["versions"]?.AsArray()
                .FirstOrDefault(node => node?["id"]?.GetValue<string>() == minecraftVersion)?["url"]
                ?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(versionUrl))
            {
                var versionText = await http.GetStringAsync(versionUrl, cancellationToken).ConfigureAwait(false);
                var major = JsonNode.Parse(versionText)?["javaVersion"]?["majorVersion"]?.GetValue<int>();
                if (major is > 0)
                {
                    lock (CacheLock)
                        RequiredJavaCache[minecraftVersion] = major.Value;
                    return major.Value;
                }
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "Server", $"读取 Minecraft {minecraftVersion} 的 Java 要求失败，使用后备值。");
        }

        lock (CacheLock)
            RequiredJavaCache[minecraftVersion] = fallback;
        return fallback;
    }

    public static async Task<ServerJavaCheckResult> CheckAsync(
        string minecraftVersion,
        string preferredPath = "",
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var required = await GetRequiredMajorAsync(minecraftVersion, client, cancellationToken)
            .ConfigureAwait(false);
        var candidates = new List<JavaCandidate>();

        try
        {
            foreach (var entry in JavaService.JavaManager.GetSortedJavaList())
            {
                if (!entry.Installation.IsStillAvailable)
                    continue;
                candidates.Add(new JavaCandidate(
                    entry.Installation.JavaExePath,
                    entry.Installation.MajorVersion,
                    entry.Installation.ToDetailedString()));
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "Server", "启动器 Java 管理器尚未就绪，使用备用扫描。");
        }

        if (candidates.Count == 0)
            candidates.AddRange(ScanCommonJava());

        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            var major = ProbeJavaMajor(preferredPath);
            if (major > 0 && candidates.All(candidate =>
                    !candidate.Path.Equals(preferredPath, StringComparison.OrdinalIgnoreCase)))
                candidates.Insert(0, new JavaCandidate(preferredPath, major, $"自定义 Java {major} | {preferredPath}"));
        }

        var suitable = candidates
            .Where(candidate => candidate.MajorVersion >= required)
            .OrderBy(candidate => candidate.MajorVersion)
            .ThenByDescending(candidate => candidate.MajorVersion)
            .ToList();

        if (suitable.Count == 0)
        {
            var found = candidates.Count == 0
                ? "未在本机找到 Java。"
                : "已找到的 Java 版本：" +
                  string.Join("，", candidates.Select(candidate => $"Java {candidate.MajorVersion}"));
            return new ServerJavaCheckResult
            {
                Success = false,
                RequiredMajorVersion = required,
                Message = $"Minecraft {minecraftVersion} 需要 Java {required} 或更高版本。{found}",
                Candidates = candidates
            };
        }

        var selected = suitable[0];
        return new ServerJavaCheckResult
        {
            Success = true,
            SelectedJavaPath = selected.Path,
            RequiredMajorVersion = required,
            Message = $"已选择 Java {selected.MajorVersion}：{selected.Path}",
            Candidates = candidates
        };
    }

    public static int ProbeJavaMajor(string javaPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaPath, "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                return 0;
            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return 0;
            }

            var match = JavaVersionRegex().Match(output);
            if (!match.Success)
                return 0;
            var major = int.Parse(match.Groups[1].Value);
            if (major == 1 && int.TryParse(match.Groups[2].Value, out var legacy))
                return legacy;
            return major;
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "Server", $"探测 Java 版本失败：{javaPath}");
            return 0;
        }
    }

    private static IEnumerable<JavaCandidate> ScanCommonJava()
    {
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("JAVA_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct())
        {
            if (root is null)
                continue;
            var direct = Path.Combine(root, "bin", "java.exe");
            if (File.Exists(direct))
            {
                var major = ProbeJavaMajor(direct);
                if (major > 0)
                    yield return new JavaCandidate(direct, major, $"Java {major} | {direct}");
            }

            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var java = Path.Combine(child, "bin", "java.exe");
                if (!File.Exists(java))
                    continue;
                var major = ProbeJavaMajor(java);
                if (major > 0)
                    yield return new JavaCandidate(java, major, $"Java {major} | {java}");
            }
        }
    }

    [GeneratedRegex(@"version ""(?:1\.)?(\d+)(?:\.(\d+))?")]
    private static partial Regex JavaVersionRegex();
}
