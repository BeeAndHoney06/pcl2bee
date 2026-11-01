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

using System.Text;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 本地服务端目录的文件浏览与编辑。所有路径都被限制在服务端目录内。
/// </summary>
public sealed class ServerFileService
{
    public const long MaxTextFileBytes = 4 * 1024 * 1024;

    public IReadOnlyList<ServerFileEntry> List(string serverDirectory, string relativePath = "")
    {
        var directory = ResolveDirectory(serverDirectory, relativePath);
        var result = new List<ServerFileEntry>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Name.Equals("backups", StringComparison.OrdinalIgnoreCase) && relativePath.Length == 0)
                continue;
            result.Add(new ServerFileEntry
            {
                Name = entry.Name,
                FullPath = entry.FullName,
                IsDirectory = entry is DirectoryInfo,
                Size = entry is FileInfo file ? file.Length : 0,
                Modified = entry.LastWriteTime
            });
        }

        return result
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadText(string serverDirectory, string relativePath)
    {
        var path = ResolveFile(serverDirectory, relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("文件不存在。", path);
        if (info.Length > MaxTextFileBytes)
            throw new InvalidOperationException("文件过大，无法在启动器中编辑（上限 4 MB）。");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Take(8000).Any(value => value == 0))
            throw new InvalidOperationException("该文件可能是二进制文件，无法以文本方式编辑。");
        return Decode(bytes);
    }

    public void WriteText(string serverDirectory, string relativePath, string content)
    {
        var path = ResolveFile(serverDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public void Delete(string serverDirectory, string relativePath)
    {
        var path = ResolveFile(serverDirectory, relativePath);
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else if (File.Exists(path))
            File.Delete(path);
        else
            throw new FileNotFoundException("文件不存在。", path);
    }

    public void CreateDirectory(string serverDirectory, string relativePath)
    {
        var path = ResolveFile(serverDirectory, relativePath);
        if (File.Exists(path))
            throw new IOException("同名文件已存在。");
        Directory.CreateDirectory(path);
    }

    public void Rename(string serverDirectory, string relativePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.Contains(Path.DirectorySeparatorChar) || newName.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("新名称非法。", nameof(newName));
        var source = ResolveFile(serverDirectory, relativePath);
        var target = Path.Combine(Path.GetDirectoryName(source)!, newName);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("目标名称已存在。");
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    public void CopyIn(string serverDirectory, string relativePath, string sourcePath)
    {
        var target = ResolveFile(serverDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target, true);
    }

    public void CopyOut(string serverDirectory, string relativePath, string targetPath)
    {
        var source = ResolveFile(serverDirectory, relativePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("文件不存在。", source);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        File.Copy(source, targetPath, true);
    }

    public string ResolveDirectory(string serverDirectory, string relativePath)
    {
        var path = ResolveFile(serverDirectory, relativePath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("目录不存在。");
        return path;
    }

    public string ResolveFile(string serverDirectory, string relativePath)
    {
        var root = Path.GetFullPath(serverDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalized = (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("路径超出服务端目录。");
        return path;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}
