using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     服务端核心下载页（Paper / Spigot / Purpur）。
///     界面与安装包下载保持一致：先选择游戏版本，再填写实例名称并开始下载。
/// </summary>
public partial class PageDownloadServer
{
    private readonly ModServerDownload.ServerType serverType;
    private List<ModServerDownload.ServerVersionEntry> allVersions = [];
    private ModServerDownload.ServerVersionEntry? selectedEntry;
    private bool isBusy;

    public PageDownloadServer(ModServerDownload.ServerType type)
    {
        serverType = type;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PanBack.ScrollToHome();
            if (allVersions.Count == 0)
                Reload();
        };
        BtnBack.Click += (_, _) => ExitSelectPage();
        SearchBox.Search += (_, _) => RenderVersions();
        SearchBox.TextChanged += (_, _) => RenderVersions();
        TextInstanceName.TextChanged += (_, _) => ValidateName();
        TextInstanceName.KeyDown += TextInstanceName_KeyDown;
    }

    /// <summary>
    ///     当前页面对应的服务端类型。
    /// </summary>
    public ModServerDownload.ServerType ServerType => serverType;

    #region 加载

    /// <summary>
    ///     重新加载版本列表。
    /// </summary>
    public void Reload()
    {
        PanLoad.Visibility = Visibility.Visible;
        PanVersions.Visibility = Visibility.Visible;
        ExitSelectPage();
        ModBase.RunInThread(() =>
        {
            List<ModServerDownload.ServerVersionEntry> versions;
            try
            {
                versions = ModServerDownload.GetVersions(serverType);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Server] 获取 {serverType} 版本列表失败");
                ModBase.RunInUi(() =>
                {
                    PanLoad.Visibility = Visibility.Collapsed;
                    PanMain.Children.Clear();
                    PanMain.Children.Add(new MyHint
                    {
                        Text = Lang.Text("Download.Server.Error.ListFailed", ex.Message), Theme = MyHint.Themes.Red,
                        HasBorder = true, Margin = new Thickness(0, 0, 0, 15)
                    });
                });
                return;
            }

            ModBase.RunInUi(() =>
            {
                allVersions = versions;
                PanLoad.Visibility = Visibility.Collapsed;
                RenderVersions();
            });
        });
    }

    #endregion

    #region 选择与命名

    private void EnterSelectPage(ModServerDownload.ServerVersionEntry entry)
    {
        selectedEntry = entry;
        PanVersions.Visibility = Visibility.Collapsed;
        PanSelect.Visibility = Visibility.Visible;
        PanBack.ScrollToHome();
        ImgLogo.Source = ModServerInstance.GetCoreLogo(serverType.ToString());
        LabInstanceInfo.Text = Lang.Text("Download.Server.Instance.Info",
            Lang.Text("Download.Server.Type." + serverType), entry.Version);
        TextInstanceName.Text = ModServerDownload.GetDefaultInstanceName(serverType, entry.Version);
        TextInstanceName.Focus();
        TextInstanceName.SelectionStart = TextInstanceName.Text.Length;
        ValidateName();
    }

    private void ExitSelectPage()
    {
        selectedEntry = null;
        PanSelect.Visibility = Visibility.Collapsed;
        PanVersions.Visibility = Visibility.Visible;
        HintName.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    ///     校验实例名称，并同步下载按钮的可用状态。
    /// </summary>
    private void ValidateName()
    {
        var valid = ModServerDownload.IsInstanceNameValid(TextInstanceName.Text, out var error);
        BtnDownload.IsEnabled = valid && !isBusy;
        if (!valid && !string.IsNullOrWhiteSpace(TextInstanceName.Text))
        {
            HintName.Text = error;
            HintName.Visibility = Visibility.Visible;
        }
        else
        {
            HintName.Visibility = Visibility.Collapsed;
        }
    }

    private void TextInstanceName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (BtnDownload.IsEnabled)
            StartDownload();
    }

    private void BtnDownload_Click(object sender, MouseButtonEventArgs e)
    {
        StartDownload();
    }

    private void StartDownload()
    {
        if (isBusy || selectedEntry is null)
            return;
        var instanceName = TextInstanceName.Text.Trim();
        if (!ModServerDownload.IsInstanceNameValid(instanceName, out _))
        {
            ValidateName();
            return;
        }

        isBusy = true;
        BtnDownload.IsEnabled = false;
        var entry = selectedEntry;
        ModBase.RunInThread(() =>
        {
            try
            {
                ModServerDownload.DownloadToInstance(serverType, entry, instanceName, () =>
                {
                    // 下载完成：刷新实例列表，使服务端实例可被直接识别与启动
                    ModBase.RunInUi(() =>
                    {
                        ModInstanceList.mcInstanceListLoader.Start(ModFolder.mcFolderSelected, true);
                        HintService.Hint(Lang.Text("Download.Server.DownloadFinished", instanceName),
                            HintType.Success);
                        isBusy = false;
                        ExitSelectPage();
                        ValidateName();
                    });
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, Lang.Text("Download.Server.Error.DownloadFailed", entry.Version),
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Download.Server.Error.DownloadFailed", entry.Version));
                ModBase.RunInUi(() =>
                {
                    isBusy = false;
                    ValidateName();
                });
            }
        });
    }

    private void BtnWeb_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite(serverType switch
        {
            ModServerDownload.ServerType.Paper => "https://papermc.io/downloads/paper",
            ModServerDownload.ServerType.Purpur => "https://purpurmc.org/downloads",
            _ => "https://www.spigotmc.org/"
        });
    }

    #endregion

    #region 版本列表渲染

    /// <summary>
    ///     按安装包下载页的样式渲染：最新版卡片置顶 + 其余按主版本分组折叠。
    /// </summary>
    private void RenderVersions()
    {
        if (PanVersions.Visibility != Visibility.Visible)
            return;
        PanMain.Children.Clear();

        // 搜索过滤
        var keyword = SearchBox.Text.Trim();
        var versions = allVersions;
        if (!string.IsNullOrEmpty(keyword))
        {
            versions = allVersions.Where(v => v.Version.ContainsF(keyword, true)).ToList();
            if (versions.Count == 0)
            {
                PanMain.Children.Add(new MyHint
                {
                    Text = Lang.Text("Download.Server.NoResult"), Theme = MyHint.Themes.Yellow, HasBorder = true
                });
                return;
            }
        }

        if (versions.Count == 0)
            return;

        // 按主版本号分组（1.21.4 → “1.21”；无点归“其他”）
        var groups = new List<(string Key, List<ModServerDownload.ServerVersionEntry> Items)>();
        foreach (var entry in versions)
        {
            var key = GetGroupKey(entry.Version);
            var existing = groups.FirstOrDefault(g => g.Key == key);
            if (existing.Key == key && existing.Items is not null)
                existing.Items.Add(entry);
            else
                groups.Add((key, [entry]));
        }

        // 最新版卡片（不折叠，取前 3）
        var latestCard = new MyCard
            { Title = Lang.Text("Download.Server.Latest.Title"), Margin = new Thickness(0, 0, 0, 15) };
        var latestPan = new StackPanel
        {
            Margin = new Thickness(20, MyCard.SwapedHeight, 18, 0), VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(0d, 0d), Tag = versions.Take(3).ToList()
        };
        MyCard.StackInstall(ref latestPan, stack =>
        {
            foreach (var entry in (List<ModServerDownload.ServerVersionEntry>)stack.Tag)
                stack.Children.Add(BuildListItem(entry));
        });
        latestCard.Children.Add(latestPan);
        PanMain.Children.Add(latestCard);

        // 分组折叠卡片
        foreach (var (key, items) in groups)
        {
            var card = new MyCard { Title = $"{key} ({items.Count})", Margin = new Thickness(0, 0, 0, 15) };
            var stack = new StackPanel
            {
                Margin = new Thickness(20, MyCard.SwapedHeight, 18, 0), VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = new TranslateTransform(0d, 0d), Tag = items
            };
            card.Children.Add(stack);
            card.SwapControl = stack;
            card.InstallMethod = s =>
            {
                foreach (var entry in (List<ModServerDownload.ServerVersionEntry>)s.Tag)
                    s.Children.Add(BuildListItem(entry));
            };
            card.IsSwapped = true;
            PanMain.Children.Add(card);
        }
    }

    private static string GetGroupKey(string version)
    {
        var parts = version.Split('.');
        if (parts.Length <= 2) return version.Contains('.') ? version : Lang.Text("Download.Server.Group.Other");
        return $"{parts[0]}.{parts[1]}";
    }

    private MyListItem BuildListItem(ModServerDownload.ServerVersionEntry entry)
    {
        var typeName = Lang.Text("Download.Server.Type." + serverType);
        var item = new MyListItem
        {
            Logo = ModServerInstance.GetCoreLogo(serverType.ToString()),
            SnapsToDevicePixels = true,
            Title = entry.Version,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Info = string.IsNullOrEmpty(entry.Detail) ? typeName : $"{typeName} · {entry.Detail}",
            Tag = entry
        };
        item.Click += (_, _) => EnterSelectPage(entry);
        return item;
    }

    #endregion
}
