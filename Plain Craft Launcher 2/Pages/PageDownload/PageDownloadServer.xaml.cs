using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.UI;

namespace PCL;

public partial class PageDownloadServer
{
    private const string PaperApi = "https://api.papermc.io/v2/projects/paper";
    private const string PurpurApi = "https://api.purpurmc.org/v2/purpur";
    private const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BuildToolsUrl =
        "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly string[] Types = ["Paper", "Purpur", "Spigot"];

    private readonly List<(int Build, string Name)> _paperBuilds = [];
    private readonly List<int> _purpurBuilds = [];
    private bool _ready;

    public PageDownloadServer()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            foreach (var type in Types)
                ComboType.Items.Add(type);
            if (_pendingType is not null)
                ComboType.SelectedItem = _pendingType;
            else
                ComboType.SelectedIndex = 0;
        };
    }

    private string CurrentType => ComboType.SelectedItem?.ToString() ?? "Paper";

    private async void ComboType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboType.SelectedItem is null) return;
        await LoadVersionsAsync();
    }

    private async void ComboVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboVersion.SelectedItem is null) return;
        await LoadBuildsAsync();
    }

    private async void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => await LoadVersionsAsync();

    /// <summary>
    ///     由下载导航指定要显示的服务端类型。
    /// </summary>
    public void SetServerType(string type)
    {
        if (ComboType.Items.Count == 0)
        {
            _pendingType = type;
            return;
        }

        ComboType.SelectedItem = type;
    }

    private string? _pendingType;

    private async Task LoadVersionsAsync()
    {
        var type = CurrentType;
        ComboVersion.Items.Clear();
        ComboBuild.Items.Clear();
        HintInfo.Theme = MyHint.Themes.Blue;
        HintInfo.Text = "正在加载版本列表…";
        try
        {
            var versions = new List<string>();
            switch (type)
            {
                case "Paper":
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync(PaperApi));
                    versions.AddRange(doc.RootElement.GetProperty("versions").EnumerateArray()
                        .Select(v => v.GetString() ?? "").Where(v => v.Length > 0));
                    versions.Reverse();
                    break;
                }
                case "Purpur":
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync(PurpurApi));
                    versions.AddRange(doc.RootElement.GetProperty("versions").EnumerateArray()
                        .Select(v => v.GetString() ?? "").Where(v => v.Length > 0));
                    versions.Reverse();
                    break;
                }
                default:
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync(MojangManifest));
                    versions.AddRange(doc.RootElement.GetProperty("versions").EnumerateArray()
                        .Where(v => v.GetProperty("type").GetString() == "release")
                        .Select(v => v.GetProperty("id").GetString() ?? "").Where(v => v.Length > 0));
                    break;
                }
            }

            foreach (var version in versions)
                ComboVersion.Items.Add(version);
            HintInfo.Text = $"{type}：共 {versions.Count} 个版本，选择版本后可下载。";
            if (versions.Count > 0)
                ComboVersion.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            HintInfo.Theme = MyHint.Themes.Red;
            HintInfo.Text = $"加载版本列表失败：{ex.Message}";
        }
    }

    private async Task LoadBuildsAsync()
    {
        var type = CurrentType;
        var version = ComboVersion.SelectedItem?.ToString() ?? "";
        ComboBuild.Items.Clear();
        _paperBuilds.Clear();
        _purpurBuilds.Clear();
        ComboBuild.IsEnabled = true;
        BtnDownload.Text = "下载";

        if (type == "Spigot")
        {
            ComboBuild.IsEnabled = false;
            HintInfo.Theme = MyHint.Themes.Yellow;
            HintInfo.Text = "Spigot 不提供官方直链。请点击「下载 BuildTools.jar」，然后在安装了 Java 17+ 与 Git 的终端执行：\n" +
                            $"java -jar BuildTools.jar --rev {version}\n生成的文件会保存在 BuildTools 目录下。";
            return;
        }

        try
        {
            if (type == "Paper")
            {
                using var doc = JsonDocument.Parse(
                    await Http.GetStringAsync($"{PaperApi}/versions/{version}/builds"));
                foreach (var build in doc.RootElement.GetProperty("builds").EnumerateArray())
                {
                    var number = build.GetProperty("build").GetInt32();
                    var name = build.GetProperty("downloads").GetProperty("application")
                        .GetProperty("name").GetString() ?? $"paper-{version}-{number}.jar";
                    _paperBuilds.Add((number, name));
                }

                foreach (var build in Enumerable.Reverse(_paperBuilds))
                    ComboBuild.Items.Add($"Build {build.Build}");
            }
            else
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{PurpurApi}/{version}"));
                foreach (var build in doc.RootElement.GetProperty("builds").GetProperty("all").EnumerateArray())
                    _purpurBuilds.Add(int.Parse(build.GetString() ?? "0"));
                foreach (var build in Enumerable.Reverse(_purpurBuilds))
                    ComboBuild.Items.Add($"Build {build}");
            }

            if (ComboBuild.Items.Count > 0)
                ComboBuild.SelectedIndex = 0;
            HintInfo.Theme = MyHint.Themes.Blue;
            HintInfo.Text = $"{type} {version}：选择构建号后点击下载。";
        }
        catch (Exception ex)
        {
            HintInfo.Theme = MyHint.Themes.Red;
            HintInfo.Text = $"加载构建列表失败：{ex.Message}";
        }
    }

    private async void BtnDownload_Click(object sender, MouseButtonEventArgs e)
    {
        var type = CurrentType;
        var version = ComboVersion.SelectedItem?.ToString() ?? "";
        var buildIndex = ComboBuild.SelectedIndex;
        string url;
        string fileName;

        if (type == "Spigot")
        {
            url = BuildToolsUrl;
            fileName = "BuildTools.jar";
        }
        else if (type == "Paper")
        {
            if (buildIndex < 0 || _paperBuilds.Count == 0)
            {
                HintInfo.Theme = MyHint.Themes.Red;
                HintInfo.Text = "请先选择游戏版本与构建号。";
                return;
            }

            var build = _paperBuilds[_paperBuilds.Count - 1 - buildIndex];
            url = $"{PaperApi}/versions/{version}/builds/{build.Build}/downloads/{build.Name}";
            fileName = build.Name;
        }
        else
        {
            if (buildIndex < 0 || _purpurBuilds.Count == 0)
            {
                HintInfo.Theme = MyHint.Themes.Red;
                HintInfo.Text = "请先选择游戏版本与构建号。";
                return;
            }

            var build = _purpurBuilds[_purpurBuilds.Count - 1 - buildIndex];
            url = $"{PurpurApi}/{version}/{build}/download";
            fileName = $"purpur-{version}-{build}.jar";
        }

        var path = SystemDialogs.SelectSaveFile("选择保存位置", fileName, "Jar 文件 (*.jar)|*.jar");
        if (string.IsNullOrWhiteSpace(path)) return;

        BtnDownload.IsEnabled = false;
        try
        {
            HintInfo.Theme = MyHint.Themes.Blue;
            HintInfo.Text = $"正在下载 {fileName} …";
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0L;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    BtnDownload.Text = total > 0
                        ? $"下载中 {received * 100 / total}%"
                        : $"下载中 {received / 1024} KB";
                }
            }

            HintInfo.Theme = MyHint.Themes.Blue;
            HintInfo.Text = $"下载完成：{path}";
            ModBase.OpenExplorer(path);
        }
        catch (Exception ex)
        {
            HintInfo.Theme = MyHint.Themes.Red;
            HintInfo.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            BtnDownload.IsEnabled = true;
            BtnDownload.Text = "下载";
        }
    }
}
