using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;

namespace PCL;

/// <summary>
///     第三方平台（FTB / Technic / ATLauncher）整合包的搜索与安装。
///     与 CurseForge / Modrinth 的 CompProject 体系相互独立，避免侵入其二元源假设。
///     所有安装流程复用 McInstallLoader（原版 + Forge/Fabric/NeoForge 安装）与 LoaderDownload。
/// </summary>
public static class ModPackThirdParty
{
    public enum ThirdPartySource
    {
        Ftb = 1,
        Technic = 2,
        AtLauncher = 3
    }

    public class ThirdPartyPackInfo
    {
        /// <summary>平台内唯一标识（FTB: 数字 ID；Technic: slug；ATLauncher: safeName）。</summary>
        public string Id;

        /// <summary>平台。</summary>
        public ThirdPartySource Source;

        /// <summary>展示名。</summary>
        public string Name;

        /// <summary>作者。</summary>
        public string Author;

        /// <summary>简介。</summary>
        public string Description;

        /// <summary>图标 URL。</summary>
        public string IconUrl;
    }

    #region 搜索

    private static JsonObject FetchJson(string url)
    {
        return (JsonObject)ModBase.GetJson(Requester.Fetch(url, new FetchParam
        {
            Method = "GET",
            ContentType = "application/json",
            Timeout = 15000,
            UseBrowserUserAgent = true
        }));
    }

    /// <summary>
    ///     搜索指定平台的整合包。必须在工作线程执行。
    /// </summary>
    public static List<ThirdPartyPackInfo> Search(ThirdPartySource source, string keyword)
    {
        return source switch
        {
            ThirdPartySource.Ftb => SearchFtb(keyword),
            ThirdPartySource.Technic => SearchTechnic(keyword),
            ThirdPartySource.AtLauncher => SearchAtLauncher(keyword),
            _ => throw new NotSupportedException("Unknown source: " + source)
        };
    }

    private static List<ThirdPartyPackInfo> SearchFtb(string keyword)
    {
        // /public/modpack/all?term= 只返回 ID 列表，需要逐个取详情（限量 20，控制请求数）
        var ids = ((JsonArray)FetchJson(
                $"https://api.modpacks.ch/public/modpack/all?term={Uri.EscapeDataString(keyword)}")["packs"])
            .Take(20).ToList();
        var result = new List<ThirdPartyPackInfo>();
        foreach (var id in ids)
        {
            try
            {
                var detail = FetchJson($"https://api.modpacks.ch/public/modpack/{id}");
                if (detail["private"] is JsonNode priv && priv.GetValue<bool>())
                    continue;
                result.Add(new ThirdPartyPackInfo
                {
                    Id = id.ToString(),
                    Source = ThirdPartySource.Ftb,
                    Name = detail["name"]?.ToString() ?? $"FTB #{id}",
                    Author = detail["authors"] is JsonArray authors && authors.Count > 0
                        ? authors[0]["name"]?.ToString() ?? "FTB"
                        : "FTB",
                    Description = detail["synopsis"]?.ToString() ?? "",
                    IconUrl = (detail["art"] as JsonArray)?.FirstOrDefault(a =>
                        a["type"]?.ToString() == "square")?["url"]?.ToString()
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[ThirdPartyPack] 获取 FTB 整合包 #{id} 详情失败，已跳过", ModBase.LogLevel.Debug);
            }
        }

        return result;
    }

    private static List<ThirdPartyPackInfo> SearchTechnic(string keyword)
    {
        var data = FetchJson($"https://api.technicpack.net/search?q={Uri.EscapeDataString(keyword)}&build=api2");
        var result = new List<ThirdPartyPackInfo>();
        foreach (var pack in (JsonArray)data["modpacks"] ?? new JsonArray())
        {
            result.Add(new ThirdPartyPackInfo
            {
                Id = pack["slug"]?.ToString(),
                Source = ThirdPartySource.Technic,
                Name = pack["name"]?.ToString() ?? pack["slug"]?.ToString() ?? "Unknown",
                Author = "",
                Description = "",
                IconUrl = pack["iconUrl"]?.ToString()
            });
        }

        return result.Where(p => !string.IsNullOrWhiteSpace(p.Id)).ToList();
    }

    private static List<ThirdPartyPackInfo> SearchAtLauncher(string keyword)
    {
        // ATLauncher v1 API：/v1/packs/simple/all 返回全部公开包（name/safeName），本地过滤
        var data = FetchJson("https://api.atlauncher.com/v1/packs/simple/all");
        if (data["error"] is JsonNode err && err.GetValue<bool>())
            throw new Exception("ATLauncher API error: " + data["message"]);
        var result = new List<ThirdPartyPackInfo>();
        foreach (var pack in (JsonArray)data["data"] ?? new JsonArray())
        {
            var name = pack["name"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(keyword) &&
                !name.ContainsF(keyword, true) &&
                !(pack["safeName"]?.ToString() ?? "").ContainsF(keyword, true))
                continue;
            result.Add(new ThirdPartyPackInfo
            {
                Id = pack["safeName"]?.ToString(),
                Source = ThirdPartySource.AtLauncher,
                Name = name,
                Author = "ATLauncher",
                Description = ""
            });
        }

        return result.Where(p => !string.IsNullOrWhiteSpace(p.Id)).Take(40).ToList();
    }

    #endregion

    #region 安装

    /// <summary>
    ///     启动安装指定第三方整合包。必须在工作线程执行；返回后安装任务已在任务栏后台运行。
    /// </summary>
    public static void Install(ThirdPartyPackInfo pack)
    {
        switch (pack.Source)
        {
            case ThirdPartySource.Ftb:
                InstallFtb(pack);
                return;
            case ThirdPartySource.Technic:
                InstallTechnic(pack);
                return;
            case ThirdPartySource.AtLauncher:
                InstallAtLauncher(pack);
                return;
            default:
                throw new NotSupportedException("Unknown source: " + pack.Source);
        }
    }

    private static string GetInstanceName(string rawName)
    {
        var validate = new PCL.Core.Utils.Validate.FolderNameValidator(
            Path.Combine(ModFolder.mcFolderSelected, "versions"));
        if (validate.Validate(rawName).IsValid)
            return rawName;
        var name = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
            [validate]);
        if (string.IsNullOrEmpty(name))
            throw new ModBase.CancelledException();
        return name;
    }

    /// <summary>构造并启动安装加载器（整合包内容步骤 + 游戏/加载器安装 + 收尾）。</summary>
    private static void StartInstallLoader(string loaderName, string instanceName,
        List<LoaderBase> contentLoaders, ModDownloadLib.McInstallRequest request,
        Action finalizer)
    {
        // 重复任务检查
        if (ModLoader.loaderTaskbar.Any(l => (l.name ?? "") == loaderName))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        var mergeLoaders = ModDownloadLib.McInstallLoader(request);
        var loaders = new List<LoaderBase>
        {
            new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"), contentLoaders)
                { show = false, block = false, ProgressWeight = contentLoaders.Sum(l => l.ProgressWeight) },
            new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
                { show = false, ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight) },
            new LoaderTask<string, string>(Lang.Text("Minecraft.Download.Modpack.Stage.FinalizeFiles"), _ =>
                finalizer())
                { ProgressWeight = 0.1d, show = false }
        };
        var loader = new LoaderCombo<string>(loaderName, loaders) { OnStateChanged = ModDownloadLib.McInstallState };
        loader.Start(request.targetInstanceFolder);
        ModLoader.LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));
    }

    /// <summary>记录整合包元信息并设置图标。</summary>
    private static void FinalizeInstance(string versionFolder, string source, string version, string iconPath)
    {
        if (!string.IsNullOrEmpty(version))
            States.Instance.ModpackVersion[versionFolder] = version;
        States.Instance.ModpackSource[versionFolder] = source;
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            Directory.CreateDirectory(Path.Combine(versionFolder, "PCL"));
            File.Copy(iconPath, Path.Combine(versionFolder, "PCL", "Logo.png"), true);
            States.Instance.LogoPath[versionFolder] = @"PCL\Logo.png";
            States.Instance.IsLogoCustom[versionFolder] = true;
        }

        ModBase.Log($"[ThirdPartyPack] {source} 整合包安装完成：{versionFolder}");
    }

    /// <summary>将相对路径安全映射到实例目录内，越界返回 null。</summary>
    private static string MapIntoInstance(string versionFolder, string relativePath)
    {
        var targetPath = Path.Combine(versionFolder, relativePath.Replace('/', '\\'));
        return Path.GetFullPath(targetPath).StartsWithF(Path.GetFullPath(versionFolder), true) ? targetPath : null;
    }

    // ---------- FTB ----------

    private static void InstallFtb(ThirdPartyPackInfo pack)
    {
        var detail = FetchJson($"https://api.modpacks.ch/public/modpack/{pack.Id}");
        // 选择最新 release 版本
        var versions = detail["versions"] as JsonArray;
        var version = versions?.FirstOrDefault(v =>
            v["type"]?.ToString() == "release" && v["id"] is not null);
        version ??= versions?.FirstOrDefault(v => v["id"] is not null);
        if (version is null)
            throw new Exception(Lang.Text("Download.Comp.ThirdParty.Error.NoVersion", pack.Name));
        var versionId = version["id"].ToString();

        var versionJson = FetchJson($"https://api.modpacks.ch/public/modpack/{pack.Id}/{versionId}");
        // 解析目标：minecraft 版本 + 加载器
        string mcVersion = null, forgeVersion = null, fabricVersion = null, neoForgeVersion = null;
        foreach (var target in (JsonArray)versionJson["targets"] ?? new JsonArray())
        {
            var name = target["name"]?.ToString() ?? "";
            var value = target["version"]?.ToString();
            switch (name)
            {
                case "minecraft":
                    mcVersion = value;
                    break;
                case "forge":
                    forgeVersion = value;
                    break;
                case "fabric":
                case "fabric-loader":
                    fabricVersion = value;
                    break;
                case "neoforge":
                    neoForgeVersion = value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(mcVersion))
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Generic"));

        var instanceName = GetInstanceName(pack.Name);
        var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";

        // 逐文件下载列表（客户端安装：跳过 serveronly；sha1+size 双校验）
        var downloadFiles = new List<DownloadFile>();
        foreach (var file in (JsonArray)versionJson["files"] ?? new JsonArray())
        {
            if (file["serveronly"] is JsonNode so && so.GetValue<bool>())
                continue;
            var url = file["url"]?.ToString();
            var path = file["path"]?.ToString() ?? "";
            var fileName = file["name"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName)) continue;
            // path 为空（如 pack.json）落到实例根目录
            var relativePath = string.IsNullOrWhiteSpace(path) || path == "." || path == "./"
                ? fileName
                : path.TrimStart('.', '/').TrimEnd('/') + "/" + fileName;
            var targetPath = MapIntoInstance(versionFolder, relativePath);
            if (targetPath is null)
            {
                ModBase.Log($"[ThirdPartyPack] 已跳过路径越界的 FTB 文件：{relativePath}", ModBase.LogLevel.Debug);
                continue;
            }

            downloadFiles.Add(new DownloadFile([url], targetPath, new ModBase.FileChecker(
                actualSize: file["size"] is JsonNode sz ? sz.GetValue<long>() : -1,
                hash: file["sha1"]?.ToString())));
        }

        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = versionFolder,
            minecraftName = mcVersion,
            forgeVersion = forgeVersion,
            fabricVersion = fabricVersion,
            neoForgeVersion = neoForgeVersion
        };

        var contentLoaders = new List<LoaderBase>();
        if (downloadFiles.Count > 0)
            contentLoaders.Add(new LoaderDownload(
                Lang.Text("Download.Comp.ThirdParty.Stage.DownloadFiles"), downloadFiles)
            {
                ProgressWeight = downloadFiles.Count * 1.5d
            });

        var versionName = version["name"]?.ToString();
        StartInstallLoader(
            Lang.Text("Download.Comp.ThirdParty.Task.Install", "FTB", instanceName),
            instanceName, contentLoaders, request,
            () => FinalizeInstance(versionFolder, "FTB", versionName, null));
    }

    // ---------- Technic ----------

    private static void InstallTechnic(ThirdPartyPackInfo pack)
    {
        var detail = FetchJson($"https://api.technicpack.net/modpack/{pack.Id}?build=api2");
        var mcVersion = detail["minecraft"]?.ToString();
        if (string.IsNullOrEmpty(mcVersion))
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Generic"));

        var instanceName = GetInstanceName(detail["displayName"]?.ToString() ?? pack.Name);
        var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
        var installTemp = ModMain.RequestTaskTempFolder();

        var contentLoaders = new List<LoaderBase>();
        var solder = detail["solder"]?.ToString();
        if (string.IsNullOrEmpty(solder))
        {
            // 普通模式：url 即整合包 zip，内容（mods/ config/ 等）在根目录
            var zipUrl = detail["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(zipUrl))
                throw new Exception(Lang.Text("Download.Comp.ThirdParty.Error.NoDownload", pack.Name));
            var zipPath = Path.Combine(installTemp, "technic.zip");
            contentLoaders.Add(new LoaderDownload(
                Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
                [new DownloadFile([zipUrl], zipPath)]) { ProgressWeight = 3d });
            contentLoaders.Add(new LoaderTask<string, int>(
                Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"), _ =>
                {
                    // Technic zip 根目录即实例内容；bin/modpack.jar 为旧式 Forge 补丁，保留原样
                    ModBase.ExtractFile(zipPath, versionFolder);
                })
                { ProgressWeight = 2d });
        }
        else
        {
            // Solder 模式：manifest 列出逐个 mod zip（含 forge），逐一下载解压
            var packVersion = detail["version"]?.ToString();
            var manifest = FetchJson($"{solder.TrimEnd('/')}/modpack/{pack.Id}/{packVersion}?include=mods");
            string forgeVersion = null;
            var modZips = new List<DownloadFile>();
            foreach (var md in (JsonArray)manifest["mods"] ?? new JsonArray())
            {
                var modName = md["name"]?.ToString();
                var modVersion = md["version"]?.ToString();
                if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(modVersion)) continue;
                if (modName.ContainsF("forge", true))
                    forgeVersion = modVersion;
                modZips.Add(new DownloadFile(
                    [$"{solder.TrimEnd('/')}/modpack/{pack.Id}/{packVersion}/{modName}@{modVersion}"],
                    Path.Combine(installTemp, "technic", $"{modName}@{modVersion}.zip")));
            }

            contentLoaders.Add(new LoaderDownload(
                Lang.Text("Download.Comp.ThirdParty.Stage.DownloadFiles"), modZips)
            { ProgressWeight = modZips.Count * 1.5d });
            var modZipPath = modZips.Select(f => f.LocalPath).ToList();
            contentLoaders.Add(new LoaderTask<string, int>(
                Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"), _ =>
                {
                    foreach (var zipPath in modZipPath)
                        if (File.Exists(zipPath))
                            ModBase.ExtractFile(zipPath, versionFolder);
                })
                { ProgressWeight = modZips.Count / 2d });

            var request = new ModDownloadLib.McInstallRequest
            {
                targetInstanceName = instanceName,
                targetInstanceFolder = versionFolder,
                minecraftName = mcVersion,
                forgeVersion = forgeVersion
            };
            var versionName = detail["version"]?.ToString();
            StartInstallLoader(
                Lang.Text("Download.Comp.ThirdParty.Task.Install", "Technic", instanceName),
                instanceName, contentLoaders, request,
                () => FinalizeInstance(versionFolder, "Technic", versionName, null));
            return;
        }

        // 普通模式的收尾（solder 分支已 return）
        var requestNormal = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = versionFolder,
            minecraftName = mcVersion
        };
        var versionNameNormal = detail["version"]?.ToString();
        StartInstallLoader(
            Lang.Text("Download.Comp.ThirdParty.Task.Install", "Technic", instanceName),
            instanceName, contentLoaders, requestNormal,
            () => FinalizeInstance(versionFolder, "Technic", versionNameNormal, null));
    }

    // ---------- ATLauncher ----------

    private static void InstallAtLauncher(ThirdPartyPackInfo pack)
    {
        // v1 API：/v1/pack/{safeName} → versions；/v1/pack/{safeName}/version/{version} → 配置 zip 与 mods 清单
        var packJson = FetchJson($"https://api.atlauncher.com/v1/pack/{pack.Id}");
        if (packJson["error"] is JsonNode e && e.GetValue<bool>())
            throw new Exception("ATLauncher API error: " + packJson["message"]);
        var versions = packJson["versions"] as JsonArray;
        var versionName = versions?.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(versionName))
            throw new Exception(Lang.Text("Download.Comp.ThirdParty.Error.NoVersion", pack.Name));

        var versionJson = FetchJson($"https://api.atlauncher.com/v1/pack/{pack.Id}/version/{versionName}");
        if (versionJson["error"] is JsonNode e2 && e2.GetValue<bool>())
            throw new Exception("ATLauncher API error: " + versionJson["message"]);
        var data = versionJson["data"] as JsonObject ?? throw new Exception(
            Lang.Text("Download.Comp.ThirdParty.Error.NoDownload", pack.Name));

        var mcVersion = data["minecraft"]?["version"]?.ToString();
        if (string.IsNullOrEmpty(mcVersion))
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Generic"));
        string forgeVersion = null, fabricVersion = null, neoForgeVersion = null;
        var loaderName = data["minecraft"]?["modLoader"]?.ToString()?.ToLower();
        var loaderVersion = data["minecraft"]?["modLoaderVersion"]?.ToString();
        if (loaderName == "forge" && !string.IsNullOrEmpty(loaderVersion)) forgeVersion = loaderVersion;
        if (loaderName == "fabric" && !string.IsNullOrEmpty(loaderVersion)) fabricVersion = loaderVersion;
        if (loaderName == "neoforge" && !string.IsNullOrEmpty(loaderVersion)) neoForgeVersion = loaderVersion;

        var instanceName = GetInstanceName(pack.Name);
        var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
        var installTemp = ModMain.RequestTaskTempFolder();

        // configs.zip（实例内容：configs/ mods/ 等）
        var configUrl =
            $"https://download.nodecdn.net/containers/atl/official/packs/{pack.Id}/versions/{versionName}/configs.zip";
        var zipPath = Path.Combine(installTemp, "atl-configs.zip");
        var contentLoaders = new List<LoaderBase>();
        contentLoaders.Add(new LoaderDownload(
            Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            [new DownloadFile([configUrl], zipPath)]) { ProgressWeight = 3d });
        contentLoaders.Add(new LoaderTask<string, int>(
            Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"), _ =>
            {
                var extractDir = Path.Combine(installTemp, "atl-extract");
                ModBase.ExtractFile(zipPath, extractDir);
                // ATLauncher configs 解压后可能带 settings/ 包裹或不带，均兼容：整体复制进实例
                ModBase.CopyDirectory(extractDir + @"\", versionFolder);
            })
            { ProgressWeight = 2d });

        // mods 清单逐项下载（clientdownload 数组：{url, file: {name, path}}）
        var modFiles = new List<DownloadFile>();
        foreach (var entry in (JsonArray)data["clientdownload"] ?? new JsonArray())
        {
            var url = entry["url"]?.ToString();
            var file = entry["file"] as JsonObject;
            var path = file?["path"]?.ToString();
            var fileName = file?["name"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName)) continue;
            var relative = string.IsNullOrWhiteSpace(path)
                ? fileName
                : path.TrimStart('.', '/').TrimEnd('/') + "/" + fileName;
            var targetPath = MapIntoInstance(versionFolder, relative);
            if (targetPath is null)
            {
                ModBase.Log($"[ThirdPartyPack] 已跳过路径越界的 ATLauncher 文件：{relative}", ModBase.LogLevel.Debug);
                continue;
            }

            modFiles.Add(new DownloadFile([url], targetPath));
        }

        if (modFiles.Count > 0)
            contentLoaders.Add(new LoaderDownload(
                Lang.Text("Download.Comp.ThirdParty.Stage.DownloadFiles"), modFiles)
            { ProgressWeight = modFiles.Count * 1.5d });

        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = versionFolder,
            minecraftName = mcVersion,
            forgeVersion = forgeVersion,
            fabricVersion = fabricVersion,
            neoForgeVersion = neoForgeVersion
        };
        StartInstallLoader(
            Lang.Text("Download.Comp.ThirdParty.Task.Install", "ATLauncher", instanceName),
            instanceName, contentLoaders, request,
            () => FinalizeInstance(versionFolder, "ATLauncher", versionName, null));
    }

    #endregion
}
