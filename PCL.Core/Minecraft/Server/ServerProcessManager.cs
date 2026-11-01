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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Server;

public sealed class EulaRequiredException()
    : Exception("Minecraft EULA 尚未确认。启动服务器前必须由用户明确同意 EULA。");

public sealed class ServerStateChangedEventArgs(string serverId, ServerRuntimeState state, int? exitCode)
    : EventArgs
{
    public string ServerId { get; } = serverId;
    public ServerRuntimeState State { get; } = state;
    public int? ExitCode { get; } = exitCode;
}

public sealed class ServerLogEventArgs(string serverId, ServerLogLine line) : EventArgs
{
    public string ServerId { get; } = serverId;
    public ServerLogLine Line { get; } = line;
}

/// <summary>
/// 本地服务端进程管理。只管理由本启动器启动的进程；日志、命令与状态均按服务端目录隔离。
/// </summary>
public sealed class ServerProcessManager
{
    private const int MaxBufferedLines = 5000;

    private readonly ConcurrentDictionary<string, ServerProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    public static ServerProcessManager Instance { get; } = new();

    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public event EventHandler<ServerLogEventArgs>? LogReceived;

    public ServerRuntimeState GetState(string serverId) =>
        _processes.TryGetValue(serverId, out var process) ? process.State : ServerRuntimeState.Stopped;

    public bool IsRunning(string serverId) =>
        _processes.TryGetValue(serverId, out var process) && process.IsRunning;

    public IReadOnlyList<ServerLogLine> GetLogs(string serverId) =>
        _processes.TryGetValue(serverId, out var process)
            ? process.GetLogs()
            : [];

    public async Task StartAsync(ServerInstance instance, CancellationToken cancellationToken = default)
    {
        if (IsRunning(instance.Id))
            return;
        if (!Directory.Exists(instance.Directory))
            throw new DirectoryNotFoundException($"服务端目录不存在：{instance.Directory}");
        if (!ServerStore.IsEulaAccepted(instance.Directory))
            throw new EulaRequiredException();

        var javaPath = await ResolveJavaAsync(instance, cancellationToken).ConfigureAwait(false);
        var (fileName, arguments) = BuildLaunchCommand(instance, javaPath);

        var current = _processes.GetOrAdd(instance.Id, _ => new ServerProcess(instance.Id, this));
        try
        {
            current.Start(instance, fileName, arguments, javaPath);
            instance.LastStartedAt = DateTime.Now;
        }
        catch
        {
            current.FailStart("服务端进程启动失败。");
            throw;
        }
    }

    public async Task StopAsync(string serverId, TimeSpan? timeout = null)
    {
        if (!_processes.TryGetValue(serverId, out var process) || !process.IsRunning)
            return;

        process.RequestStop();
        var wait = timeout ?? TimeSpan.FromSeconds(20);
        try
        {
            await process.SendCommandAsync("stop", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "Server", $"向服务端 {serverId} 发送 stop 命令失败，将等待进程退出。");
        }

        if (!await process.WaitForExitAsync(wait).ConfigureAwait(false))
        {
            process.AppendLog("[PCL] 服务器未在超时时间内退出，正在强制结束进程。", true);
            process.Kill();
        }
    }

    public async Task RestartAsync(ServerInstance instance, CancellationToken cancellationToken = default)
    {
        await StopAsync(instance.Id).ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        await StartAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(string serverId, string command, CancellationToken cancellationToken = default)
    {
        if (!_processes.TryGetValue(serverId, out var process) || !process.IsRunning)
            throw new InvalidOperationException("服务器未在运行，无法发送命令。");
        var cleaned = command.Replace("\r", "").Replace("\n", "");
        if (string.IsNullOrWhiteSpace(cleaned))
            return;
        await process.SendCommandAsync(cleaned, cancellationToken).ConfigureAwait(false);
        process.AppendLog($"> {cleaned}", false, isCommandEcho: true);
    }

    public void ClearLogs(string serverId)
    {
        if (_processes.TryGetValue(serverId, out var process))
            process.ClearLogs();
    }

    /// <summary>
    /// PCL 退出时可以选择停止服务器；默认不停止，避免误杀用户正在运行的服务端。
    /// </summary>
    public async Task StopAllAsync(TimeSpan? timeout = null)
    {
        foreach (var id in _processes.Keys.ToList())
            await StopAsync(id, timeout).ConfigureAwait(false);
    }

    internal void RaiseStateChanged(string serverId, ServerRuntimeState state, int? exitCode = null) =>
        StateChanged?.Invoke(this, new ServerStateChangedEventArgs(serverId, state, exitCode));

    internal void RaiseLog(string serverId, ServerLogLine line) =>
        LogReceived?.Invoke(this, new ServerLogEventArgs(serverId, line));

    private static async Task<string> ResolveJavaAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(instance.JavaPath) && File.Exists(instance.JavaPath))
            return instance.JavaPath;

        var check = await ServerJavaEnvironment.CheckAsync(
            instance.MinecraftVersion, instance.JavaPath, null, cancellationToken).ConfigureAwait(false);
        if (!check.Success)
            throw new ServerEnvironmentException(check.Message);
        instance.JavaPath = check.SelectedJavaPath;
        return check.SelectedJavaPath;
    }

    private static (string FileName, string Arguments) BuildLaunchCommand(ServerInstance instance, string javaPath)
    {
        var memory = $"-Xms{Math.Max(256, instance.MinMemoryMb)}M -Xmx{Math.Max(512, instance.MemoryMb)}M " +
                     "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8";
        var extra = string.IsNullOrWhiteSpace(instance.ExtraArgs) ? "" : instance.ExtraArgs.Trim() + " ";
        if (instance.LaunchMode == "forgeRun")
        {
            var argsFile = FindForgeArgsFile(instance.Directory);
            if (argsFile is not null)
            {
                var jvmArgs = Path.Combine(instance.Directory, "user_jvm_args.txt");
                if (!File.Exists(jvmArgs))
                {
                    File.WriteAllText(jvmArgs,
                        $"# Generated by PCL CE Server Manager\r\n-Xms{instance.MinMemoryMb}M\r\n-Xmx{instance.MemoryMb}M\r\n" +
                        "-Dfile.encoding=UTF-8\r\n-Dstdout.encoding=UTF-8\r\n-Dstderr.encoding=UTF-8\r\n",
                        new UTF8Encoding(false));
                }

                return (javaPath, $"@{QuoteArgument(jvmArgs)} @{QuoteArgument(argsFile)} nogui");
            }

            if (File.Exists(Path.Combine(instance.Directory, "run.bat")))
                return ("cmd.exe", "/c run.bat nogui");
            throw new FileNotFoundException(
                "未找到 Forge/NeoForge 的 win_args.txt 或 run.bat，请重新安装服务端。");
        }

        return (javaPath,
            $"{memory} {extra}-jar {QuoteArgument(instance.JarFile)} nogui");
    }

    public static string? FindForgeArgsFile(string directory)
    {
        var libraries = Path.Combine(directory, "libraries");
        if (!Directory.Exists(libraries))
            return null;

        try
        {
            return Directory.EnumerateFiles(libraries, "win_args.txt", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed class ServerProcess(string serverId, ServerProcessManager owner)
    {
        private readonly object _sync = new();
        private readonly List<ServerLogLine> _logs = [];
        private Process? _process;
        private StreamWriter? _logWriter;
        private bool _stopRequested;
        private ServerRuntimeState _state = ServerRuntimeState.Stopped;

        public ServerRuntimeState State
        {
            get { lock (_sync) return _state; }
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                    return _process is { HasExited: false };
            }
        }

        public void Start(ServerInstance instance, string fileName, string arguments, string javaPath)
        {
            lock (_sync)
            {
                if (_process is { HasExited: false })
                    throw new InvalidOperationException("服务器已在运行。");
                _stopRequested = false;
                _logs.Clear();
                SetState(ServerRuntimeState.Starting);

                var logDirectory = Path.Combine(instance.Directory, "logs");
                Directory.CreateDirectory(logDirectory);
                _logWriter = new StreamWriter(
                    new FileStream(Path.Combine(logDirectory, "latest.log"), FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                _logWriter.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} PCL CE 启动服务器 =====");

                var startInfo = new ProcessStartInfo(fileName, arguments)
                {
                    WorkingDirectory = instance.Directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false)
                };
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, args) => OnOutput(args.Data, false);
                process.ErrorDataReceived += (_, args) => OnOutput(args.Data, true);
                process.Exited += (_, _) => OnExited();
                _process = process;

                AppendLog($"[PCL] 工作目录：{instance.Directory}", false);
                AppendLog($"[PCL] Java：{javaPath}", false);
                AppendLog($"[PCL] 启动命令：{fileName} {arguments}", false);
                if (!process.Start())
                    throw new InvalidOperationException("无法启动服务端进程。");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                SetState(ServerRuntimeState.Running);
                AppendLog("[PCL] 服务器进程已启动。", false);
            }
        }

        public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            Process? process;
            lock (_sync)
                process = _process;
            if (process is null || process.HasExited)
                throw new InvalidOperationException("服务器未在运行。");
            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void RequestStop()
        {
            lock (_sync)
            {
                _stopRequested = true;
                if (State == ServerRuntimeState.Running)
                    SetState(ServerRuntimeState.Stopping);
            }
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            Process? process;
            lock (_sync)
                process = _process;
            if (process is null)
                return true;
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return !process.HasExited;
            }
        }

        public void Kill()
        {
            Process? process;
            lock (_sync)
                process = _process;
            try
            {
                process?.Kill(true);
            }
            catch (Exception ex)
            {
                LogWrapper.Debug(ex, "Server", $"强制结束服务端 {serverId} 失败");
            }
        }

        public void FailStart(string message)
        {
            lock (_sync)
            {
                AppendLog("[PCL] " + message, true);
                SetState(ServerRuntimeState.Crashed);
                try
                {
                    _logWriter?.Dispose();
                }
                catch
                {
                    // ignore
                }

                _logWriter = null;
                if (_process is not null)
                {
                    try { _process.Dispose(); } catch { /* ignore */ }
                    _process = null;
                }
            }
        }

        public void AppendLog(string text, bool isError, bool isCommandEcho = false)
        {
            if (string.IsNullOrEmpty(text))
                return;
            var line = new ServerLogLine
            {
                Time = DateTime.Now,
                Text = text,
                IsError = isError
            };
            lock (_sync)
            {
                _logs.Add(line);
                if (_logs.Count > MaxBufferedLines)
                    _logs.RemoveRange(0, _logs.Count - MaxBufferedLines);
                try
                {
                    _logWriter?.WriteLine(text);
                }
                catch
                {
                    // 日志写入失败不应影响服务器运行
                }
            }

            owner.RaiseLog(serverId, line);
        }

        public IReadOnlyList<ServerLogLine> GetLogs()
        {
            lock (_sync)
                return _logs.ToArray();
        }

        public void ClearLogs()
        {
            lock (_sync)
                _logs.Clear();
        }

        private void OnOutput(string? text, bool isError)
        {
            if (text is null)
                return;
            AppendLog(text, isError && !text.Contains("[INFO]", StringComparison.OrdinalIgnoreCase));
        }

        private void OnExited()
        {
            Process? process;
            lock (_sync)
            {
                process = _process;
                if (process is null)
                    return;
                var exitCode = process.ExitCode;
                var crashed = !_stopRequested && exitCode != 0;
                SetState(crashed ? ServerRuntimeState.Crashed : ServerRuntimeState.Stopped, exitCode);
                AppendLog(crashed
                    ? $"[PCL] 服务器进程异常退出，退出码：{exitCode}"
                    : $"[PCL] 服务器进程已退出，退出码：{exitCode}", crashed);
                try
                {
                    _logWriter?.Flush();
                    _logWriter?.Dispose();
                }
                catch
                {
                    // ignore
                }

                _logWriter = null;
                try { process.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }

        private void SetState(ServerRuntimeState state, int? exitCode = null)
        {
            _state = state;
            owner.RaiseStateChanged(serverId, state, exitCode);
        }
    }
}
