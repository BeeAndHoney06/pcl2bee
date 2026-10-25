using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;

namespace PCL;

public static class UpdateManager
{
    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        new UpdatesMirrorChyanModel()
    });

    /// <summary>GitHub Release 最新版查询地址（Bee Edition 唯一官方分发渠道）。</summary>
    private const string ReleaseApiUrl = "https://api.github.com/repos/BeeAndHoney06/pcl2bee/releases/latest";

    /// <summary>
    ///     从 GitHub Release 获取最新稳定版的下载信息。
    ///     返回 (下载 URL, SHA256)；Release 产物自带 digest 元数据时使用之，否则 SHA256 为 null。
    /// </summary>
    private static (string Url, string? Sha256) GetLatestReleaseAsset(bool isArm64)
    {
        var assetName = isArm64 ? "Plain.Craft.Launcher.2.arm64.exe" : "Plain.Craft.Launcher.2.x64.exe";
        var json = (JsonObject)ModBase.GetJson(Requester.Fetch(ReleaseApiUrl, new FetchParam
        {
            Method = "GET",
            ContentType = "application/json",
            Timeout = 15000,
            UseBrowserUserAgent = true
        }));
        var assets = json["assets"] as JsonArray;
        foreach (var asset in assets ?? new JsonArray())
        {
            if (asset["name"]?.ToString() != assetName) continue;
            var url = asset["browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url)) continue;
            // GitHub 2025 起为 Release 产物提供 sha256 digest 元数据
            var digest = asset["digest"]?.ToString();
            var sha256 = digest?.StartsWithF("sha256:", true) == true ? digest["sha256:".Length..].ToUpper() : null;
            return (url, sha256);
        }

        throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     直接从 GitHub Release 下载；如果正在使用的版本就是最新版，则直接复制。
    /// </summary>
    internal static void DownloadLatestPCL(ModLoader.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPCLPath = Path.Combine(ModBase.pathTemp, "CE-Latest.exe");
        var isArm64 = SystemInfo.IsArm64System;
        var (url, remoteSha256) = GetLatestReleaseAsset(isArm64);

        // 已有缓存且校验一致
        if (remoteSha256 is not null && File.Exists(latestPCLPath) &&
            (ModBase.GetFileSHA256(latestPCLPath) ?? "") == remoteSha256)
        {
            ModBase.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        // 正在使用的版本符合要求，直接拿来用（本机架构与目标一致时）
        if (!isArm64 == !SystemInfo.IsArm64System)
        {
            var selfSha256 = ModBase.GetFileSHA256(Basics.ExecutablePath);
            if (remoteSha256 is not null && selfSha256 == remoteSha256)
            {
                ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
                return;
            }
        }

        var loaders = new List<ModLoader.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Update.Task.DownloadLatestStable"),
                [new DownloadFile([url], latestPCLPath,
                    new ModBase.FileChecker(hash: remoteSha256))])
        };
        var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }
}
