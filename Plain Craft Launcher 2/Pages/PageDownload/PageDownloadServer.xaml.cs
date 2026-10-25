using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageDownloadServer
{
    private ModServerDownload.ServerType currentType = ModServerDownload.ServerType.Vanilla;
    private List<ModServerDownload.ServerVersionEntry> allVersions = [];

    public PageDownloadServer()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        SearchBox.Search += (_, _) => RenderVersions();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) RenderVersions();
        };
        ComboServerType.SelectionChanged += (_, _) =>
        {
            if (ComboServerType.SelectedItem is MyComboBoxItem item)
            {
                var t = (ModServerDownload.ServerType)ModBase.Val(item.Tag);
                if (t != currentType)
                {
                    currentType = t;
                    Reload();
                }
            }
        };
    }

    public void Reload()
    {
        HintError.Visibility = Visibility.Collapsed;
        PanVersions.Children.Clear();
        var loading = new MyLoading
        {
            Text = Lang.Text("Download.Server.Loading"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10)
        };
        PanVersions.Children.Add(loading);
        ModBase.RunInThread(() =>
        {
            List<ModServerDownload.ServerVersionEntry> versions;
            try
            {
                versions = ModServerDownload.GetVersions(currentType);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Server] 获取{currentType}服务端版本列表失败");
                ModBase.RunInUi(() =>
                {
                    PanVersions.Children.Clear();
                    HintError.Visibility = Visibility.Visible;
                    HintError.Text = Lang.Text("Download.Server.Error.ListFailed", ex.Message);
                });
                return;
            }

            ModBase.RunInUi(() =>
            {
                allVersions = versions;
                RenderVersions();
            });
        });
    }

    private void RenderVersions()
    {
        PanVersions.Children.Clear();
        var keyword = SearchBox.Text.Trim();
        var shown = string.IsNullOrEmpty(keyword)
            ? allVersions.Take(40).ToList()
            : allVersions.Where(v => v.Version.ContainsF(keyword, true)).Take(40).ToList();
        if (shown.Count == 0)
        {
            HintError.Visibility = Visibility.Visible;
            HintError.Text = Lang.Text("Download.Server.NoResult");
            return;
        }

        HintError.Visibility = Visibility.Collapsed;
        foreach (var entry in shown)
        {
            var item = new MyListItem
            {
                Title = entry.Version,
                Info = string.IsNullOrEmpty(entry.Detail)
                    ? Lang.Text("Download.Server.Type." + currentType)
                    : $"{Lang.Text("Download.Server.Type." + currentType)} · {entry.Detail}",
                Height = 40d,
                Tag = entry
            };
            var downloadButton = new MyIconButton { SvgIcon = "lucide/download", LogoScale = 1.1d };
            downloadButton.Click += (_, _) => ItemDownload_Click(entry);
            item.Buttons = [downloadButton];
            PanVersions.Children.Add(item);
        }
    }

    private void ItemDownload_Click(ModServerDownload.ServerVersionEntry entry)
    {
        // 选择保存目录
        var folder = SystemDialogs.SelectFolder(Lang.Text("Download.Server.SelectFolder"));
        if (string.IsNullOrEmpty(folder)) return;
        ModBase.RunInThread(() =>
        {
            try
            {
                ModServerDownload.Download(currentType, entry, folder);
                ModBase.RunInUi(() => HintService.Hint(
                    Lang.Text("Download.Server.DownloadStarted"), HintType.Success));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"下载{currentType}服务端失败（{entry.Version}）", ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Download.Server.Error.DownloadFailed", entry.Version));
            }
        });
    }
}
