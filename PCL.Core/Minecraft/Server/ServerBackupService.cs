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

using System.IO.Compression;
using System.Text;

namespace PCL.Core.Minecraft.Server;

/// <summary>
/// 服务端文件备份与恢复。备份文件存放在服务端目录的 backups 子目录中。
/// </summary>
public sealed class ServerBackupService
{
    public string BackupDirectory(string serverDirectory) => Path.Combine(serverDirectory, "backups");

    public IReadOnlyList<ServerBackupEntry> List(string serverDirectory)
    {
        var directory = BackupDirectory(serverDirectory);
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ServerBackupEntry
                {
                    FileName = info.Name,
                    FullPath = info.FullName,
                    Size = info.Length,
                    CreatedAt = info.CreationTime
                };
            })
            .OrderByDescending(entry => entry.CreatedAt)
            .ToList();
    }

    public string Create(string serverDirectory, string? label = null)
    {
        var backupDirectory = BackupDirectory(serverDirectory);
        Directory.CreateDirectory(backupDirectory);
        var prefix = string.IsNullOrWhiteSpace(label) ? "" : ServerStore.MakeSafeName(label) + "-";
        var fileName = $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(backupDirectory, fileName);
        var index = 2;
        while (File.Exists(path))
            path = Path.Combine(backupDirectory, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}-{index++}.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(serverDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(serverDirectory, file);
            if (relative.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("backups", StringComparison.OrdinalIgnoreCase))
                continue;

            var entry = archive.CreateEntry(relative.Replace(Path.DirectorySeparatorChar, '/'),
                CompressionLevel.Optimal);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = entry.Open();
            source.CopyTo(target);
        }

        return path;
    }

    public string Restore(string serverDirectory, string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在。", backupPath);
        if (!Path.GetFullPath(backupPath).StartsWith(
                Path.GetFullPath(BackupDirectory(serverDirectory)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能恢复当前服务器 backups 目录中的备份。");

        // 恢复前再备份一次当前状态，避免误操作导致数据不可挽回。
        var safety = Create(serverDirectory, "pre-restore");
        var tempDirectory = Path.Combine(serverDirectory, $".restore-temp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(tempDirectory, entry.FullName));
                    if (!destination.StartsWith(Path.GetFullPath(tempDirectory) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("备份文件包含非法路径。");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }

            // 删除除 backups 与临时目录外的内容。
            foreach (var file in Directory.EnumerateFiles(serverDirectory, "*", SearchOption.TopDirectoryOnly))
                File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(serverDirectory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals("backups", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(directory).StartsWith(".restore-temp-", StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.Delete(directory, true);
            }

            foreach (var file in Directory.EnumerateFiles(tempDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(tempDirectory, file);
                var destination = Path.Combine(serverDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, true);
            }

            return safety;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
            catch
            {
                // 忽略清理失败
            }
        }
    }

    public void Delete(string serverDirectory, string backupPath)
    {
        var full = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(BackupDirectory(serverDirectory)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除当前服务器 backups 目录中的备份。");
        File.Delete(full);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    public static string ReadAllTextShared(string path) =>
        File.ReadAllText(path, Encoding.UTF8);
}
