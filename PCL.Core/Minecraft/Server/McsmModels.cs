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

namespace PCL.Core.Minecraft.Server;

public enum McsmErrorKind
{
    InvalidConfiguration,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    Api,
    Parse,
    Cancelled
}

public sealed class McsmApiException : Exception
{
    public McsmApiException(
        string message,
        McsmErrorKind kind,
        int? httpStatusCode = null,
        int? apiStatus = null,
        Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
        ApiStatus = apiStatus;
    }

    public McsmErrorKind Kind { get; }
    public int? HttpStatusCode { get; }
    public int? ApiStatus { get; }
}

public sealed class McsmUser
{
    public string Uuid { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Permission { get; set; }
    public string RegisterTime { get; set; } = "";
    public string LoginTime { get; set; } = "";
    public bool Open2Fa { get; set; }
    public bool IsInit { get; set; }
    public int InstanceCount { get; set; }
}

public sealed class McsmPage<T>
{
    public List<T> Items { get; } = [];
    public int Page { get; set; } = 1;
    public int PageSize { get; set; }
    public int MaxPage { get; set; } = 1;
    public int Total { get; set; }
}

public sealed class McsmSystemInfo
{
    public string Type { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Release { get; set; } = "";
    public double Uptime { get; set; }
    public long FreeMem { get; set; }
    public long TotalMem { get; set; }
    public double CpuUsage { get; set; }
    public double MemUsage { get; set; }
}

public sealed class McsmDaemon
{
    public string Uuid { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Prefix { get; set; } = "";
    public bool Available { get; set; }
    public string Remarks { get; set; } = "";
    public string Version { get; set; } = "";
    public int RunningInstances { get; set; }
    public int TotalInstances { get; set; }
    public McsmSystemInfo? System { get; set; }
}

public sealed class McsmOverview
{
    public string Version { get; set; } = "";
    public string SpecifiedDaemonVersion { get; set; } = "";
    public string PanelCwd { get; set; } = "";
    public long PanelMemory { get; set; }
    public McsmSystemInfo? System { get; set; }
    public int RemoteAvailable { get; set; }
    public int RemoteTotal { get; set; }
    public int RunningInstances { get; set; }
    public int TotalInstances { get; set; }
    public int Logined { get; set; }
    public List<McsmDaemon> Remotes { get; } = [];
}

public sealed class McsmInstance
{
    public string Uuid { get; set; } = "";
    public string DaemonId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public int Status { get; set; }
    public string StartCommand { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Type { get; set; } = "";
    public int Started { get; set; }
    public int AutoRestarted { get; set; }
    public long Space { get; set; }
    public int CurrentPlayers { get; set; } = -1;
    public int MaxPlayers { get; set; } = -1;
    public string Version { get; set; } = "";
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public int Pid { get; set; }
    public long ElapsedMilliseconds { get; set; }

    public string StatusText => Status switch
    {
        -1 => "忙碌",
        0 => "已停止",
        1 => "停止中",
        2 => "启动中",
        3 => "运行中",
        _ => "未知"
    };
}

public sealed class McsmFileEntry
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Time { get; set; } = "";
    public int Mode { get; set; }
    /// <summary>0 = 文件夹，1 = 文件。</summary>
    public int Type { get; set; }
    public bool IsDirectory => Type == 0;
}

public sealed class McsmFileList
{
    public List<McsmFileEntry> Items { get; } = [];
    public string AbsolutePath { get; set; } = "";
    public int Page { get; set; }
    public int Total { get; set; }
}

public sealed class McsmImage
{
    public string Id { get; set; } = "";
    public List<string> Tags { get; } = [];
    public long Size { get; set; }
    public long Created { get; set; }
    public string DisplayName => Tags.Count > 0 ? Tags[0] : Id.Length > 19 ? Id[..19] : Id;
}

public sealed class McsmContainer
{
    public string Id { get; set; } = "";
    public string Image { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Names { get; } = [];
}

public sealed class McsmSelectionOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public override string ToString() => Label;
}
