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

using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 支持自动部署的 Minecraft 服务端类型。
/// </summary>
public enum ServerKind
{
    Vanilla,
    Paper,
    Purpur,
    Fabric,
    Forge,
    NeoForge
}

/// <summary>
/// 本地服务端进程的运行状态。
/// </summary>
public enum ServerRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed
}

/// <summary>
/// 服务端目录中的 server.json 元数据。
/// 不包含 EULA 是否已同意的状态：该状态始终以磁盘上的 eula.txt 为准，
/// 以免出现“元数据已同意但用户从未确认”的情况。
/// </summary>
public sealed class ServerInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ServerKind Kind { get; set; } = ServerKind.Vanilla;
    public string MinecraftVersion { get; set; } = "";
    /// <summary>服务端构建号、Fabric Loader 版本或 Forge/NeoForge 版本。</summary>
    public string PackageVersion { get; set; } = "";
    public string JarFile { get; set; } = "server.jar";
    /// <summary>jar：直接以 -jar 启动；forgeRun：使用安装器生成的 run.bat 启动。</summary>
    public string LaunchMode { get; set; } = "jar";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 2048;
    public int MinMemoryMb { get; set; } = 512;
    public string ExtraArgs { get; set; } = "";
    public int Port { get; set; } = 25565;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastStartedAt { get; set; }

    /// <summary>
    ///     外部服务端目录（非 PCL/Servers 下，例如某个 MC 实例目录）。
    ///     为空表示由 <see cref="ServerStore" /> 管理的标准目录。
    /// </summary>
    public string ExternalDirectory { get; set; } = "";

    [JsonIgnore] public string Directory { get; set; } = "";
    [JsonIgnore] public ServerRuntimeState RuntimeState { get; set; } = ServerRuntimeState.Stopped;
    [JsonIgnore] public bool EulaAccepted { get; set; }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>
/// 一个待下载的服务端文件。
/// </summary>
public sealed class ServerDownloadArtifact
{
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; } = -1;
}

/// <summary>
/// 解析服务端版本后得到的部署计划。
/// </summary>
public sealed class ServerDownloadPlan
{
    public ServerKind Kind { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public string ServerDirectory { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string LaunchMode { get; set; } = "jar";
    public List<ServerDownloadArtifact> Artifacts { get; } = [];
    public bool NeedsInstaller => Kind is ServerKind.Forge or ServerKind.NeoForge or ServerKind.Fabric;
}

/// <summary>
/// 新建服务端时的用户选项。
/// </summary>
public sealed class ServerCreateOptions
{
    public string Name { get; set; } = "";
    public ServerKind Kind { get; set; } = ServerKind.Vanilla;
    public string MinecraftVersion { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 2048;
    public int MinMemoryMb { get; set; } = 512;
    public int Port { get; set; } = 25565;
    public string ExtraArgs { get; set; } = "";
}

public sealed class ServerVersionOption
{
    public string Version { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsStable { get; set; } = true;
}

public sealed class ServerJavaCheckResult
{
    public bool Success { get; set; }
    public string SelectedJavaPath { get; set; } = "";
    public int RequiredMajorVersion { get; set; }
    public string Message { get; set; } = "";
    public IReadOnlyList<JavaCandidate> Candidates { get; set; } = [];
}

public sealed record JavaCandidate(string Path, int MajorVersion, string Description);

public sealed class ServerFileEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

public sealed class ServerBackupEntry
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ServerLogLine
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public bool IsError { get; set; }
}
