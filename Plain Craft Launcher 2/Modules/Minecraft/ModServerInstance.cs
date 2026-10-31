using System.Diagnostics;
using System.IO;
using System.Text;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     服务端实例：保存在 Minecraft 文件夹下的 serverversion 文件夹中，每个子文件夹为一个实例。
///     实例文件夹内包含服务端核心 jar 与 <see cref="MetadataFileName" /> 元数据文件。
/// </summary>
public static class ModServerInstance
{
    /// <summary>
    ///     服务端实例的根文件夹名称。
    /// </summary>
    public const string FolderName = "serverversion";

    /// <summary>
    ///     实例元数据文件名。
    /// </summary>
    public const string MetadataFileName = ".pclserver.json";

    /// <summary>
    ///     服务端默认分配的内存（MiB）。
    /// </summary>
    public const int DefaultMemoryMb = 2048;

    /// <summary>
    ///     服务端最小分配的内存（MiB）。
    /// </summary>
    public const int MinMemoryMb = 512;

    /// <summary>
    ///     正在运行的服务端实例，键为实例文件夹。
    /// </summary>
    private static readonly Dictionary<string, ModWatcher.Watcher> runningServers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     正在启动、尚未创建进程的服务端实例。
    /// </summary>
    private static readonly HashSet<string> startingServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     服务端实例的根文件夹。
    /// </summary>
    public static string RootFolder => Path.Combine(ModFolder.mcFolderSelected, FolderName);

    /// <summary>
    ///     服务端实例的元数据。
    /// </summary>
    public class ServerMeta
    {
        /// <summary>服务端核心名称，如 Paper、Spigot、Purpur。</summary>
        public string Core = "";

        /// <summary>对应的 Minecraft 版本。</summary>
        public string GameVersion = "";

        /// <summary>构建号（若适用）。</summary>
        public string Build = "";

        /// <summary>核心 jar 的文件名。</summary>
        public string Jar = "";
    }

    #region 元数据

    /// <summary>
    ///     读取指定实例文件夹的元数据，若不存在则返回 null。
    /// </summary>
    public static ServerMeta? ReadMeta(string folder)
    {
        try
        {
            var path = Path.Combine(folder, MetadataFileName);
            if (!File.Exists(path))
                return null;
            var json = ModBase.GetJson(ModBase.ReadFile(path));
            return new ServerMeta
            {
                Core = (json["core"] ?? "").ToString(),
                GameVersion = (json["gameVersion"] ?? "").ToString(),
                Build = (json["build"] ?? "").ToString(),
                Jar = (json["jar"] ?? "").ToString()
            };
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Select.Server.Error.ReadMeta", folder), ModBase.LogLevel.Hint);
            return null;
        }
    }

    /// <summary>
    ///     写入指定实例文件夹的元数据。
    /// </summary>
    public static void WriteMeta(string folder, ServerMeta meta)
    {
        var json = new JsonObject
        {
            ["core"] = meta.Core,
            ["gameVersion"] = meta.GameVersion,
            ["build"] = meta.Build,
            ["jar"] = meta.Jar
        };
        ModBase.WriteFile(Path.Combine(folder, MetadataFileName), json.ToString());
    }

    #endregion

    #region 核心文件

    /// <summary>
    ///     在实例文件夹中定位服务端核心 jar。
    /// </summary>
    public static string? FindJar(string folder, ServerMeta? meta = null)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;
            if (meta is not null && !string.IsNullOrWhiteSpace(meta.Jar))
            {
                var declared = Path.Combine(folder, meta.Jar);
                if (File.Exists(declared))
                    return declared;
            }

            // 回退：取文件夹中第一个 jar（优先排除安装器与构建工具）
            return new DirectoryInfo(folder).EnumerateFiles("*.jar")
                .OrderBy(file => file.Name.ContainsF("installer", true))
                .ThenBy(file => file.Name.ContainsF("buildtools", true))
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Select.Server.Error.ReadMeta", folder), ModBase.LogLevel.Hint);
            return null;
        }
    }

    /// <summary>
    ///     从核心文件名猜测服务端类型（如 paper-1.21.4-123.jar → Paper）。
    /// </summary>
    public static string DetectCoreFromFileName(string fileName)
    {
        foreach (var core in new[] { "Paper", "Purpur", "Spigot" })
            if (fileName.ContainsF(core, true))
                return core;
        return Lang.Text("Select.Server.Core.Unknown");
    }

    /// <summary>
    ///     获取服务端核心对应的图标。
    /// </summary>
    public static string GetCoreLogo(string? core)
    {
        if (!string.IsNullOrWhiteSpace(core) && core.ContainsF("Paper", true))
            return ModBase.pathImage + "Blocks/CobbleStone.png";
        if (!string.IsNullOrWhiteSpace(core) && core.ContainsF("Purpur", true))
            return ModBase.pathImage + "Blocks/CommandBlock.png";
        if (!string.IsNullOrWhiteSpace(core) && core.ContainsF("Spigot", true))
            return ModBase.pathImage + "Blocks/RedstoneBlock.png";
        return ModBase.pathImage + "Blocks/CommandBlock.png";
    }

    #endregion

    #region 实例列表

    /// <summary>
    ///     扫描 serverversion 文件夹中的全部服务端实例。
    /// </summary>
    public static List<McInstance> ScanInstances()
    {
        var result = new List<McInstance>();
        try
        {
            var root = RootFolder;
            if (!Directory.Exists(root))
                Directory.CreateDirectory(root); // 保证 serverversion 文件夹始终存在
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
            {
                try
                {
                    var meta = ReadMeta(folder.FullName);
                    if (meta is null && !folder.EnumerateFiles("*.jar").Any())
                        continue; // 既没有元数据也没有核心，视为普通文件夹

                    var instance = new McInstance(folder.FullName)
                    {
                        IsServer = true,
                        ServerCore = meta?.Core ?? "",
                        ServerVersion = meta?.GameVersion ?? ""
                    };
                    instance.Load();
                    result.Add(instance);
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, Lang.Text("Select.Server.Error.ReadMeta", folder.FullName),
                        ModBase.LogLevel.Hint);
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Select.Server.Error.Scan"), ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Select.Server.Error.Scan"));
        }

        return result;
    }

    /// <summary>
    ///     将服务端实例合并到客户端实例列表的最前方（独立为一类卡片）。
    /// </summary>
    public static Dictionary<McInstanceCardType, List<McInstance>> MergeInto(
        Dictionary<McInstanceCardType, List<McInstance>> source)
    {
        if (source.ContainsKey(McInstanceCardType.Server))
            return source;

        var servers = ScanInstances();
        if (servers.Count == 0)
            return source;

        var result = new Dictionary<McInstanceCardType, List<McInstance>>
        {
            [McInstanceCardType.Server] = servers
        };
        foreach (var pair in source)
            result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>
    ///     判断指定的服务端实例是否正在运行。
    /// </summary>
    public static bool IsRunning(McInstance instance)
    {
        if (instance is null)
            return false;
        lock (runningServers)
            return runningServers.ContainsKey(instance.PathInstance);
    }

    #endregion

    #region 启动

    /// <summary>
    ///     启动服务端实例，并把控制台接入实时日志页面。
    /// </summary>
    public static void Start(McInstance instance)
    {
        if (instance is null || !instance.IsServer)
            return;
        if (!instance.IsLoaded)
            instance.Load();
        if (instance.state == McInstanceState.Error)
        {
            HintService.Hint(Lang.Text("Select.Server.Error.CannotLaunch", instance.Name), HintType.Error);
            return;
        }

        lock (runningServers)
        {
            if (runningServers.ContainsKey(instance.PathInstance) || startingServers.Contains(instance.PathInstance))
            {
                HintService.Hint(Lang.Text("Select.Server.Error.AlreadyRunning", instance.Name), HintType.Error);
                return;
            }
        }

        lock (runningServers)
            startingServers.Add(instance.PathInstance);

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var folder = instance.PathInstance;
                Directory.CreateDirectory(folder);
                EnsureEula(folder);

                var jarPath = FindJar(folder, ReadMeta(folder));
                if (string.IsNullOrEmpty(jarPath))
                {
                    HintService.Hint(Lang.Text("Select.Server.Error.NoCore", instance.Name), HintType.Error);
                    return;
                }

                instance.ServerJarPath = jarPath;

                // 选择 Java（跟随实例 Java 设置，否则按服务端所需版本自动挑选）
                JavaEntry? java;
                var minJava = GetRequiredJavaVersion(instance.ServerVersion);
                lock (ModJava.javaLock)
                    java = ModJava.JavaSelect("$$", minJava, null, instance);
                if (java is null)
                {
                    HintService.Hint(Lang.Text("Select.Server.Error.NoJava"), HintType.Error);
                    return;
                }

                var process = StartProcess(java.Installation.JavaExePath, jarPath, folder);
                var loader = new ModLoader.LoaderTask<Process, int>("Server Console", _ => { });
                loader.input = process;
                var watcher = new ModWatcher.Watcher(loader, instance, "", "", true, true);
                watcher.State = ModWatcher.Watcher.MinecraftState.Running;

                lock (runningServers)
                {
                    startingServers.Remove(instance.PathInstance);
                    runningServers[instance.PathInstance] = watcher;
                }

                watcher.GameExit += () =>
                {
                    lock (runningServers)
                        runningServers.Remove(instance.PathInstance);
                };

                // 接入实时日志页面（控制台与日志共用同一个界面）
                if (ModMain.frmLogLeft is null)
                    ModBase.RunInUiWait(() => ModMain.frmLogLeft = new PageLogLeft());
                if (ModMain.frmLogRight is null)
                    ModBase.RunInUiWait(() =>
                    {
                        ModAnimation.AniControlEnabled += 1;
                        ModMain.frmLogRight = new PageLogRight();
                        ModAnimation.AniControlEnabled -= 1;
                    });
                ModMain.frmLogLeft.Add(watcher);
                ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.GameLog));
                HintService.Hint(Lang.Text("Select.Server.Launched", instance.Name), HintType.Success);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, Lang.Text("Select.Server.Error.LaunchFailed", instance.Name),
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Select.Server.Error.LaunchFailed", instance.Name));
            }
            finally
            {
                lock (runningServers)
                    startingServers.Remove(instance.PathInstance);
            }
        }, "Server Launch " + instance.Name);
    }

    /// <summary>
    ///     根据服务端对应的 Minecraft 版本推算所需的最低 Java 版本。
    /// </summary>
    private static Version? GetRequiredJavaVersion(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion) || !Version.TryParse(mcVersion, out var version))
            return null;
        if (version >= new Version(1, 20, 5))
            return new Version(21, 0);
        if (version >= new Version(1, 18))
            return new Version(17, 0);
        if (version >= new Version(1, 17))
            return new Version(16, 0);
        return new Version(1, 8);
    }

    /// <summary>
    ///     创建服务端进程。
    /// </summary>
    private static Process StartProcess(string javaPath, string jarPath, string folder)
    {
        var memory = Math.Max(DefaultMemoryMb, MinMemoryMb);
        var arguments =
            $"-Xms{MinMemoryMb}M -Xmx{memory}M -Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 " +
            $"-Dsun.stderr.encoding=UTF-8 -jar \"{Path.GetFileName(jarPath)}\" nogui";
        var info = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = arguments,
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8
        };
        var process = Process.Start(info) ??
                      throw new Exception(Lang.Text("Select.Server.Error.ProcessNotStarted"));
        ModBase.Log($"[Server] 已启动服务端进程：{javaPath} {arguments}（工作目录：{folder}）");
        return process;
    }

    /// <summary>
    ///     确保实例文件夹中存在已同意 EULA 的 eula.txt。
    /// </summary>
    private static void EnsureEula(string folder)
    {
        var path = Path.Combine(folder, "eula.txt");
        if (File.Exists(path) && File.ReadAllText(path).ContainsF("eula=true", true))
            return;
        ModBase.WriteFile(path,
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\r\n" +
            "eula=true\r\n");
        ModBase.Log($"[Server] 已自动写入 eula.txt：{folder}");
        HintService.Hint(Lang.Text("Select.Server.EulaAccepted"), HintType.Info);
    }

    #endregion
}

