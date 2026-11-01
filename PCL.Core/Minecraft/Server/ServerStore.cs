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
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 管理 PCL/Servers 下的服务端目录。每个服务端都有独立的子目录与 server.json。
/// </summary>
public sealed class ServerStore
{
    private const string MetaFileName = "server.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonCompat.SerializerOptions)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ServerStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Paths.Data, "Servers"));
    }

    private static ServerStore? _shared;
    public static ServerStore Shared => _shared ??= new ServerStore();

    public string Root { get; }

    public void EnsureRoot() => Directory.CreateDirectory(Root);

    public IReadOnlyList<ServerInstance> List()
    {
        EnsureRoot();
        var result = new List<ServerInstance>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(directory);
            var instance = Load(id);
            if (instance is not null)
                result.Add(instance);
        }

        return result
            .OrderByDescending(instance => instance.CreatedAt)
            .ToList();
    }

    public ServerInstance? Load(string id)
    {
        try
        {
            var directory = GetDirectory(id);
            var metaPath = Path.Combine(directory, MetaFileName);
            if (!File.Exists(metaPath))
                return null;

            var instance = JsonSerializer.Deserialize<ServerInstance>(
                File.ReadAllText(metaPath), SerializerOptions);
            if (instance is null)
                return null;

            instance.Id = id;
            instance.Directory = directory;
            instance.EulaAccepted = IsEulaAccepted(directory);
            return instance;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Server", $"读取服务端元数据失败：{id}");
            return null;
        }
    }

    public ServerInstance Create(ServerInstance instance)
    {
        EnsureRoot();
        if (string.IsNullOrWhiteSpace(instance.Id))
            instance.Id = CreateId(instance.Name);
        if (Directory.Exists(GetDirectory(instance.Id)) || File.Exists(GetDirectory(instance.Id)))
            throw new IOException($"服务端目录已存在：{instance.Id}");

        var directory = GetDirectory(instance.Id);
        System.IO.Directory.CreateDirectory(directory);
        instance.Directory = directory;
        instance.CreatedAt = DateTime.Now;
        Save(instance);
        return instance;
    }

    public void Save(ServerInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Id))
            throw new ArgumentException("服务端 ID 不能为空", nameof(instance));
        var directory = GetDirectory(instance.Id);
        System.IO.Directory.CreateDirectory(directory);
        instance.Directory = directory;

        var metaPath = Path.Combine(directory, MetaFileName);
        var tempPath = metaPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(instance, SerializerOptions));
        File.Move(tempPath, metaPath, true);
    }

    public void Delete(string id, bool deleteFiles = true)
    {
        var directory = GetDirectory(id);
        if (deleteFiles)
        {
            if (!IsInsideRoot(directory))
                throw new InvalidOperationException("拒绝删除 Servers 目录之外的路径。");
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, true);
            return;
        }

        var metaPath = Path.Combine(directory, MetaFileName);
        if (File.Exists(metaPath))
            File.Delete(metaPath);
    }

    public string GetDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id is "." or ".." ||
            id.Contains(Path.DirectorySeparatorChar) ||
            id.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("服务端 ID 非法", nameof(id));

        var path = Path.GetFullPath(Path.Combine(Root, id));
        if (!IsInsideRoot(path))
            throw new ArgumentException("服务端路径越界", nameof(id));
        return path;
    }

    public string CreateId(string? name)
    {
        var baseId = MakeSafeName(name);
        if (string.IsNullOrWhiteSpace(baseId))
            baseId = "server";
        var id = baseId;
        var index = 2;
        while (Directory.Exists(Path.Combine(Root, id)) || File.Exists(Path.Combine(Root, id)))
            id = $"{baseId}-{index++}";
        return id;
    }

    public static string MakeSafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim()
            .Select(c => invalid.Contains(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c)
            .ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length > 48 ? result[..48].TrimEnd() : result;
    }

    public static bool IsEulaAccepted(string serverDirectory)
    {
        var path = Path.Combine(serverDirectory, "eula.txt");
        if (!File.Exists(path))
            return false;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                continue;
            var index = trimmed.IndexOf('=');
            if (index <= 0)
                continue;
            if (trimmed[..index].Trim().Equals("eula", StringComparison.OrdinalIgnoreCase) &&
                trimmed[(index + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 只在用户明确确认 EULA 后调用。
    /// </summary>
    public static void AcceptEula(string serverDirectory, bool accepted)
    {
        var path = Path.Combine(serverDirectory, "eula.txt");
        var text = accepted
            ? "# Accepted by the user in PCL CE. Do not edit unless you agree to the Minecraft EULA.\r\neula=true\r\n"
            : "# The Minecraft EULA has not been accepted yet.\r\neula=false\r\n";
        File.WriteAllText(path, text);
    }

    public static int FindFreePort(int start = 25565)
    {
        for (var port = Math.Clamp(start, 1024, 65535); port <= 65535; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，继续尝试下一个
            }
        }

        return start;
    }

    private bool IsInsideRoot(string path)
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
