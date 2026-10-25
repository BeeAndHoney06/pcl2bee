using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.UI;

namespace PCL;

public partial class PageDownloadServer
{
    private ModServerDownload.ServerType currentType = ModServerDownload.ServerType.Vanilla;
    private List<ModServerDownload.ServerVersionEntry> allVersions = [];

    public PageDownloadServer()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PanBack.ScrollToHome();
            if (allVersions.Count == 0) Reload();
        };
        // 类型单选按钮组
        var typeNames = new[]
        {
            (ModServerDownload.ServerType.Vanilla, "Download.Server.Type.Vanilla"),
            (ModServerDownload.ServerType.Paper, "Download.Server.Type.Paper"),
            (ModServerDownload.ServerType.Purpur, "Download.Server.Type.Purpur"),
            (ModServerDownload.ServerType.Spigot, "Download.Server.Type.Spigot"),
            (ModServerDownload.ServerType.Forge, "Download.Server.Type.Forge"),
            (ModServerDownload.ServerType.Fabric, "Download.Server.Type.Fabric")
        };
        foreach (var (type, key) in typeNames)
        {
            var radio = new MyRadioBox
            {
                Text = Lang.Text(key), Height = 24d, Margin = new Thickness(0, 0, 18, 8),
                Tag = type, Checked = type == ModServerDownload.ServerType.Vanilla
            };
            radio.Check += (_, _) => SwitchType(type);
            PanTypeButtons.Children.Add(radio);
        }

        // 搜索过滤
        SearchBox.Search += (_, _) => { if (allVersions.Count > 0) RenderVersions(); };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && allVersions.Count > 0) RenderVersions();
        };
    }

    /// <summary>切换服务端类型并刷新。</summary>
    public void SwitchType(ModServerDownload.ServerType type)
    {
        if (type == currentType && allVersions.Count > 0) return;
        currentType = type;
        Reload();
    }

    public void Reload()
    {
        PanLoad.Visibility = Visibility.Visible;
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
                RenderVersions();
            });
        });
    }

    /// <summary>按原版游戏下载页的样式渲染：最新版卡片置顶 + 其余按主版本分组折叠。</summary>
    private void RenderVersions()
    {
        PanLoad.Visibility = Visibility.Collapsed;
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

        if (versions.Count == 0) return;

        // 按主版本号分组（1.21.4 → “1.21”；无点归“其他”）
        var groups = new List<(string Key, List<ModServerDownload.ServerVersionEntry> Items)>();
        foreach (var entry in allVersions)
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
        var item = new MyListItem
        {
            Logo = ServerTypeLogo(currentType),
            SnapsToDevicePixels = true,
            Title = McFormatter.FormatVersion(entry.Version).Replace("_", " "),
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Info = string.IsNullOrEmpty(entry.Detail)
                ? Lang.Text("Download.Server.Type." + currentType)
                : $"{Lang.Text("Download.Server.Type." + currentType)} · {entry.Detail}",
            Tag = entry
        };
        item.Click += ItemDownload_Click;
        return item;
    }

    private static string ServerTypeLogo(ModServerDownload.ServerType type)
    {
        return type switch
        {
            ModServerDownload.ServerType.Paper => ModBase.pathImage + "Blocks/CobbleStone.png",
            ModServerDownload.ServerType.Purpur => ModBase.pathImage + "Blocks/CommandBlock.png",
            ModServerDownload.ServerType.Spigot => ModBase.pathImage + "Blocks/RedstoneBlock.png",
            ModServerDownload.ServerType.Forge => ModBase.pathImage + "Blocks/Anvil.png",
            ModServerDownload.ServerType.Fabric => ModBase.pathImage + "Blocks/Fabric.png",
            _ => ModBase.pathImage + "Blocks/Grass.png"
        };
    }

    private void ItemDownload_Click(object sender, MouseButtonEventArgs e)
    {
        // 防御：Tag 非预期类型时不崩溃（例如控件被复用或结构被改动）
        if (sender is not MyListItem { Tag: ModServerDownload.ServerVersionEntry entry })
        {
            ModBase.Log("[Server] 下载入口的条目数据异常，已忽略本次点击", ModBase.LogLevel.Developer);
            return;
        }

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
