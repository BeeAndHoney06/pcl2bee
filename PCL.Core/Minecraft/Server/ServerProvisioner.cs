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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using System.Diagnostics;
using System.Text;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Server;

public sealed class ServerEnvironmentException(string message) : Exception(message);

/// <summary>
/// 服务端一键部署：解析下载计划、安装器安装、写入基础配置。
/// 不会写入 eula=true；EULA 只由用户首次启动前明确确认。
/// </summary>
public static class ServerProvisioner
{
    public static async Task<ServerDownloadPlan> CreatePlanAsync(
        ServerCreateOptions options,
        ServerStore? store = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
            options.Name = $"{options.Kind} {options.MinecraftVersion}";
        if (string.IsNullOrWhiteSpace(options.MinecraftVersion))
            throw new ArgumentException("Minecraft 版本不能为空。", nameof(options));

        store ??= ServerStore.Shared;
        var javaCheck = await ServerJavaEnvironment.CheckAsync(
            options.MinecraftVersion, options.JavaPath, httpClient, cancellationToken).ConfigureAwait(false);
        if (!javaCheck.Success)
            throw new ServerEnvironmentException(javaCheck.Message);
        options.JavaPath = javaCheck.SelectedJavaPath;

        var id = store.CreateId(options.Name);
        var directory = store.GetDirectory(id);
        Directory.CreateDirectory(directory);

        var catalog = new ServerCatalog(httpClient);
        var plan = await catalog.ResolveAsync(options, directory, cancellationToken).ConfigureAwait(false);
        plan.ServerId = id;
        plan.JavaPath = javaCheck.SelectedJavaPath;
        return plan;
    }

    /// <summary>
    /// 下载完成后进行安装与配置。失败时保留目录以便重试，但不会写入 EULA 同意。
    /// </summary>
    public static async Task<ServerInstance> ConfigureAsync(
        ServerDownloadPlan plan,
        ServerCreateOptions options,
        ServerStore? store = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        store ??= ServerStore.Shared;
        var directory = plan.ServerDirectory;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "backups"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));

        var launchMode = plan.LaunchMode;
        var installerJarFile = "";
        if (plan.NeedsInstaller)
        {
            var installerPath = Path.Combine(directory, plan.Artifacts[0].FileName);
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("服务端安装器不存在，请重试下载。", installerPath);

            log?.Invoke($"正在运行 {plan.Kind} 安装器，这可能需要几分钟…");
            IReadOnlyList<string> installerArguments = plan.Kind == ServerKind.Fabric
                ?
                [
                    "server",
                    "-mcversion", plan.MinecraftVersion,
                    "-loader", plan.PackageVersion,
                    "-downloadMinecraft"
                ]
                : ["--installServer"];
            var exitCode = await RunInstallerAsync(
                plan.JavaPath, directory, installerPath, installerArguments, log, cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"{plan.Kind} 安装器退出码为 {exitCode}，请查看日志。");

            if (plan.Kind == ServerKind.Fabric)
            {
                launchMode = "jar";
                installerJarFile = Directory.EnumerateFiles(directory, "fabric-server-launch*.jar",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";
                if (string.IsNullOrWhiteSpace(installerJarFile))
                    throw new InvalidOperationException("Fabric 安装器没有生成 fabric-server-launch.jar。");
                log?.Invoke("Fabric 安装器已完成，将使用 fabric-server-launch.jar 启动。");
            }
            else
            {
                launchMode = DetectForgeLaunchMode(directory);
                if (launchMode == "jar")
                    log?.Invoke("已检测到 Forge/NeoForge 服务端启动文件，将使用 jar 启动。");
                else
                    log?.Invoke("已生成 run.bat，将使用安装器生成的启动脚本。");
            }
        }

        var jarFile = installerJarFile;
        if (string.IsNullOrWhiteSpace(jarFile))
        {
            jarFile = plan.Artifacts.FirstOrDefault(artifact =>
                artifact.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !artifact.FileName.Contains("installer", StringComparison.OrdinalIgnoreCase))?.FileName ?? "";
        }

        if (launchMode == "jar" && string.IsNullOrWhiteSpace(jarFile))
        {
            jarFile = Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null &&
                                        !name.Contains("installer", StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        if (launchMode == "jar" && string.IsNullOrWhiteSpace(jarFile))
            throw new InvalidOperationException("没有找到可用的服务端 jar。");

        var instance = new ServerInstance
        {
            Id = plan.ServerId,
            Name = options.Name,
            Kind = plan.Kind,
            MinecraftVersion = plan.MinecraftVersion,
            PackageVersion = plan.PackageVersion,
            JarFile = jarFile,
            LaunchMode = launchMode,
            JavaPath = plan.JavaPath,
            MemoryMb = Math.Clamp(options.MemoryMb, 512, 65536),
            MinMemoryMb = Math.Clamp(options.MinMemoryMb, 256, Math.Clamp(options.MemoryMb, 512, 65536)),
            ExtraArgs = options.ExtraArgs?.Trim() ?? "",
            Port = options.Port > 0 ? options.Port : ServerStore.FindFreePort(),
            CreatedAt = DateTime.Now
        };

        WriteServerProperties(directory, instance);
        // 明确写入 false；绝不自动同意 EULA。
        if (!File.Exists(Path.Combine(directory, "eula.txt")))
            ServerStore.AcceptEula(directory, false);
        WriteStartScript(directory, instance);
        store.Save(instance);
        log?.Invoke($"服务器已创建：{instance.Name}（{directory}）");
        return instance;
    }

    public static async Task<int> RunInstallerAsync(
        string javaPath,
        string workingDirectory,
        string installerPath,
        IReadOnlyList<string> arguments,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                log?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                log?.Invoke(args.Data);
        };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Java 安装器。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("服务端安装器运行超过 15 分钟，已终止。");
        }

        return process.ExitCode;
    }

    public static string DetectForgeLaunchMode(string directory)
    {
        if (File.Exists(Path.Combine(directory, "run.bat")) ||
            File.Exists(Path.Combine(directory, "run.sh")))
            return "forgeRun";

        var candidate = Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
            {
                var name = Path.GetFileName(path);
                return !name.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                       (name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase));
            });
        return candidate is null ? "forgeRun" : "jar";
    }

    public static void WriteServerProperties(string directory, ServerInstance instance)
    {
        var path = Path.Combine(directory, "server.properties");
        var values = new Dictionary<string, string>
        {
            ["server-port"] = instance.Port.ToString(),
            ["motd"] = instance.Name.Replace("\r", " ").Replace("\n", " "),
            ["max-players"] = "20",
            ["online-mode"] = "true",
            ["level-name"] = "world",
            ["enable-command-block"] = "false",
            ["view-distance"] = "10",
            ["simulation-distance"] = "10",
            ["white-list"] = "false",
            ["allow-flight"] = "false",
            ["enable-status"] = "true"
        };

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || trimmed.Length == 0)
                    continue;
                var index = trimmed.IndexOf('=');
                if (index > 0)
                    existing[trimmed[..index].Trim()] = trimmed[(index + 1)..];
            }
        }

        foreach (var pair in values)
            existing[pair.Key] = pair.Value;

        var builder = new StringBuilder();
        builder.AppendLine("# Minecraft server properties");
        builder.AppendLine("# Generated by PCL CE Server Manager");
        foreach (var pair in existing.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"{pair.Key}={pair.Value}");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    public static void WriteStartScript(string directory, ServerInstance instance)
    {
        string command;
        if (instance.LaunchMode == "forgeRun")
        {
            var jvmArgsPath = Path.Combine(directory, "user_jvm_args.txt");
            File.WriteAllText(jvmArgsPath,
                $"# Generated by PCL CE Server Manager\r\n-Xms{instance.MinMemoryMb}M\r\n-Xmx{instance.MemoryMb}M\r\n" +
                "-Dfile.encoding=UTF-8\r\n-Dstdout.encoding=UTF-8\r\n-Dstderr.encoding=UTF-8\r\n" +
                (string.IsNullOrWhiteSpace(instance.ExtraArgs) ? "" : instance.ExtraArgs + "\r\n"),
                new UTF8Encoding(false));
            var argsFile = ServerProcessManager.FindForgeArgsFile(directory);
            command = argsFile is not null
                ? "@echo off\r\ncd /d \"%~dp0\"\r\n" +
                  $"\"{instance.JavaPath}\" @user_jvm_args.txt @\"{argsFile}\" nogui\r\npause\r\n"
                : "@echo off\r\ncd /d \"%~dp0\"\r\ncall run.bat nogui\r\npause\r\n";
        }
        else
        {
            command =
                "@echo off\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                $"\"{instance.JavaPath}\" -Xms{instance.MinMemoryMb}M -Xmx{instance.MemoryMb}M " +
                "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 " +
                (string.IsNullOrWhiteSpace(instance.ExtraArgs) ? "" : instance.ExtraArgs + " ") +
                $"-jar \"{instance.JarFile}\" nogui\r\n" +
                "pause\r\n";
        }

        var startPath = Path.Combine(directory, "start.bat");
        File.WriteAllText(startPath, command, Encoding.GetEncoding("GB18030"));
    }
}
