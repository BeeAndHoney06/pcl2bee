using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageDownloadServer
{
    /// <summary>下拉项与枚举的固定顺序（不随语言变化）。</summary>
    private static readonly ModServerDownload.ServerType[] TypeOrder =
    [
        ModServerDownload.ServerType.Vanilla,
        ModServerDownload.ServerType.Paper,
        ModServerDownload.ServerType.Purpur,
        ModServerDownload.ServerType.Spigot,
        ModServerDownload.ServerType.Forge,
        ModServerDownload.ServerType.Fabric
    ];

    private bool _ready;
    private bool _suppressEvents;
    private string? _pendingType;
    private ModServerDownload.ServerType CurrentType =>
        ComboType.SelectedIndex >= 0 && ComboType.SelectedIndex < TypeOrder.Length
            ? TypeOrder[ComboType.SelectedIndex]
            : ModServerDownload.ServerType.Vanilla;

    public PageDownloadServer()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            _suppressEvents = true;
            foreach (var type in TypeOrder)
                ComboType.Items.Add(Lang.Text($"Download.Server.Type.{type}"));
            // 恢复由导航指定的类型；否则默认 Paper（插件服最常用）
            var target = _pendingType ?? "Paper";
            _pendingType = null;
            ComboType.SelectedIndex = Math.Max(0, Array.IndexOf(TypeOrder,
                Enum.TryParse<ModServerDownload.ServerType>(target, true, out var parsed)
                    ? parsed
                    : ModServerDownload.ServerType.Paper));
            _suppressEvents = false;
            _ = ReloadAsync();
        };
    }

    /// <summary>由下载导航指定要显示的服务端类型。</summary>
    public void SetServerType(string type)
    {
        if (!_ready || ComboType.Items.Count == 0)
        {
            _pendingType = type;
            return;
        }

        var index = Enum.TryParse<ModServerDownload.ServerType>(type, true, out var parsed)
            ? Array.IndexOf(TypeOrder, parsed)
            : -1;
        if (index >= 0 && ComboType.SelectedIndex != index)
            ComboType.SelectedIndex = index;
        else
            _ = ReloadAsync();
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => _ = ReloadAsync();

    private async void ComboType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_ready) return;
        await ReloadAsync();
    }

    private async void ComboVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_ready) return;
        await LoadBuildsAsync();
    }

    private void SetHint(string text, MyHint.Themes theme = MyHint.Themes.Blue)
    {
        HintInfo.Theme = theme;
        HintInfo.Text = text;
    }

    /// <summary>加载版本列表。</summary>
    private async Task ReloadAsync()
    {
        var type = CurrentType;
        _suppressEvents = true;
        ComboVersion.Items.Clear();
        ComboBuild.Items.Clear();
        _suppressEvents = false;
        SetHint(Lang.Text("Download.Server.Hint.Loading"));
        try
        {
            var versions = await Task.Run(() => ModServerDownload.GetVersions(type));
            _suppressEvents = true;
            foreach (var entry in versions)
                ComboVersion.Items.Add(entry.Version);
            _suppressEvents = false;
            if (versions.Count > 0)
            {
                ComboVersion.SelectedIndex = 0; // 触发构建列表加载
            }
            else
            {
                SetHint(Lang.Text("Download.Server.NoResult"), MyHint.Themes.Yellow);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Server] 获取 {type} 服务端版本列表失败");
            SetHint(Lang.Text("Download.Server.Error.ListFailed", ex.Message), MyHint.Themes.Red);
        }
    }

    /// <summary>加载构建列表（仅 Paper / Purpur 有构建概念）。</summary>
    private async Task LoadBuildsAsync()
    {
        var type = CurrentType;
        var version = ComboVersion.SelectedItem?.ToString() ?? "";
        _suppressEvents = true;
        ComboBuild.Items.Clear();
        _suppressEvents = false;
        var hasBuilds = type is ModServerDownload.ServerType.Paper or ModServerDownload.ServerType.Purpur;
        LabBuild.Visibility = hasBuilds ? Visibility.Visible : Visibility.Collapsed;
        ComboBuild.Visibility = hasBuilds ? Visibility.Visible : Visibility.Collapsed;
        // 无构建概念时整行塌缩，避免留下空白行
        RowBuild.Height = hasBuilds ? GridLength.Auto : new GridLength(0);
        if (!hasBuilds)
        {
            SetHint(Lang.Text("Download.Server.Hint.ReadyType", Lang.Text($"Download.Server.Type.{type}")));
            return;
        }

        SetHint(Lang.Text("Download.Server.Hint.LoadingBuilds"));
        try
        {
            var builds = await Task.Run(() => ModServerDownload.GetBuilds(type, version));
            _suppressEvents = true;
            foreach (var build in builds)
                ComboBuild.Items.Add(Lang.Text("Download.Server.BuildItem", build));
            _suppressEvents = false;
            if (builds.Count > 0)
                ComboBuild.SelectedIndex = 0;
            SetHint(Lang.Text("Download.Server.Hint.BuildsLoaded",
                Lang.Text($"Download.Server.Type.{type}"), version, builds.Count));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Server] 获取 {type} {version} 构建列表失败");
            SetHint(Lang.Text("Download.Server.Error.BuildsFailed", ex.Message), MyHint.Themes.Red);
        }
    }

    private void BtnDownload_Click(object sender, MouseButtonEventArgs e)
    {
        var type = CurrentType;
        var version = ComboVersion.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(version))
        {
            SetHint(Lang.Text("Download.Server.Error.SelectVersion"), MyHint.Themes.Red);
            return;
        }

        var build = ComboBuild.SelectedIndex >= 0
            ? ComboBuild.Items[ComboBuild.SelectedIndex]?.ToString()
            : null;
        // 构建项带本地化前缀，取原始构建号
        if (build is not null)
        {
            var raw = ComboBuild.Items[ComboBuild.SelectedIndex]?.ToString() ?? "";
            var idx = raw.LastIndexOf(' ');
            build = idx >= 0 ? raw[(idx + 1)..] : raw;
        }

        ModBase.RunInNewThread(() =>
        {
            ModServerDownload.DownloadSpec spec;
            try
            {
                spec = ModServerDownload.Resolve(type, version, build);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Server] 解析 {type} {version} 下载信息失败");
                ModBase.RunInUi(() => SetHint(
                    Lang.Text("Download.Server.Error.ResolveFailed", ex.Message), MyHint.Themes.Red));
                return;
            }

            // 保存位置：沿用原实现的「另存为」交互
            var path = ModBase.RunInUiWait(() => SystemDialogs.SelectSaveFile(
                Lang.Text("Download.Server.SelectFolder"), spec.FileName,
                Lang.Text("Download.Server.FileFilter")));
            if (string.IsNullOrWhiteSpace(path)) return;

            ModBase.RunInUiWait(() =>
            {
                BtnDownload.IsEnabled = false;
                SetHint(spec.Note ?? Lang.Text("Download.Server.Hint.Downloading", spec.FileName));
            });
            try
            {
                var loader = ModServerDownload.StartDownload(spec, path);
                // 按钮实时显示进度（保留原实现的交互）
                while (loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting)
                {
                    var percent = (int)Math.Round(loader.Progress * 100);
                    ModBase.RunInUi(() =>
                        BtnDownload.Text = Lang.Text("Download.Server.Button.Downloading", percent));
                    Thread.Sleep(120);
                }

                if (loader.State == ModBase.LoadState.Finished)
                {
                    ModBase.RunInUi(() => SetHint(
                        Lang.Text("Download.Server.Hint.Downloaded", path)));
                    ModBase.OpenExplorer(path);
                }
                else
                {
                    ModBase.RunInUi(() => SetHint(
                        Lang.Text("Download.Server.Error.DownloadFailed",
                            loader.Error?.Message ?? ""), MyHint.Themes.Red));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Server] 下载 {type} {version} 服务端失败");
                ModBase.RunInUi(() => SetHint(
                    Lang.Text("Download.Server.Error.DownloadFailed", ex.Message), MyHint.Themes.Red));
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    BtnDownload.IsEnabled = true;
                    BtnDownload.Text = Lang.Text("Download.Server.Button.Download");
                });
            }
        }, "Server Download");
    }
}
