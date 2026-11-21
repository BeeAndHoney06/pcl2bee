using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     整合包服务端下载页：从 CurseForge / Modrinth 选择整合包。
///     下载完成后弹出窗口填写实例名称，并将整合包解压到 serverversion/&lt;实例名称&gt;。
/// </summary>
public partial class PageDownloadServerPack
{
    private const int PageSize = 20;

    private readonly List<ModServerPackDownload.ServerPackProject> projects = [];
    private int curseForgeOffset;
    private int modrinthOffset;
    private bool curseForgeHasMore;
    private bool modrinthHasMore;
    private bool isLoading;
    private bool isDownloading;
    private string keyword = "";

    public PageDownloadServerPack()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PanBack.ScrollToHome();
            if (projects.Count == 0)
                Reload();
        };
        BtnBack.Click += (_, _) => ExitFilePage();
        SearchBox.Search += (_, _) => Reload();
        SearchBox.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text) && keyword.Length > 0)
                Reload();
        };
        ComboSource.SelectionChanged += (_, _) => Reload();
        BtnMore.Click += (_, _) => LoadProjects();
    }

    /// <summary>来源筛选：0 为全部，1 为 CurseForge，2 为 Modrinth。</summary>
    private int SelectedSource =>
        ComboSource.SelectedItem is MyComboBoxItem { Tag: not null } item ? (int)ModBase.Val(item.Tag) : 0;

    #region 加载列表

    /// <summary>重新加载整合包列表。</summary>
    public void Reload()
    {
        if (isLoading)
            return;
        keyword = SearchBox.Text?.Trim() ?? "";
        projects.Clear();
        curseForgeOffset = 0;
        modrinthOffset = 0;
        curseForgeHasMore = false;
        modrinthHasMore = false;
        PanFileList.Children.Clear();
        PanFiles.Visibility = Visibility.Collapsed;
        PanProjects.Visibility = Visibility.Visible;
        PanProjectList.Children.Clear();
        HintError.Visibility = Visibility.Collapsed;
        BtnMore.Visibility = Visibility.Collapsed;
        PanLoad.Visibility = Visibility.Visible;
        LoadProjects();
    }

    private void LoadProjects()
    {
        if (isLoading)
            return;
        isLoading = true;
        BtnMore.IsEnabled = false;
        var source = SelectedSource;
        var offsetCurseForge = curseForgeOffset;
        var offsetModrinth = modrinthOffset;
        var searchKeyword = keyword;
        ModBase.RunInThread(() =>
        {
            var added = new List<ModServerPackDownload.ServerPackProject>();
            var curseForgeCount = 0;
            var modrinthCount = 0;
            Exception? error = null;
            try
            {
                if (source is 0 or 1)
                {
                    var list = ModServerPackDownload.SearchProjects(ModServerPackDownload.PackSource.CurseForge,
                        searchKeyword, offsetCurseForge, PageSize, out var hasMore);
                    curseForgeHasMore = hasMore;
                    curseForgeCount = list.Count;
                    added.AddRange(list);
                }
                else
                {
                    curseForgeHasMore = false;
                }

                if (source is 0 or 2)
                {
                    var list = ModServerPackDownload.SearchProjects(ModServerPackDownload.PackSource.Modrinth,
                        searchKeyword, offsetModrinth, PageSize, out var hasMore);
                    modrinthHasMore = hasMore;
                    modrinthCount = list.Count;
                    added.AddRange(list);
                }
                else
                {
                    modrinthHasMore = false;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ModBase.RunInUi(() =>
            {
                isLoading = false;
                PanLoad.Visibility = Visibility.Collapsed;
                curseForgeOffset = offsetCurseForge + curseForgeCount;
                modrinthOffset = offsetModrinth + modrinthCount;
                projects.AddRange(added);
                RenderProjects(added, error);
                BtnMore.IsEnabled = true;
                BtnMore.Visibility = curseForgeHasMore || modrinthHasMore
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            });
        });
    }

    #endregion

    #region 渲染列表

    private void RenderProjects(List<ModServerPackDownload.ServerPackProject> added, Exception? error)
    {
        if (error is not null)
        {
            ModBase.Log(error, "[ServerPack] 获取整合包列表失败");
            HintError.Text = Lang.Text("Download.ServerPack.Error.ListFailed", error.Message);
            HintError.Visibility = Visibility.Visible;
            if (projects.Count == 0)
                PanProjectList.Children.Clear();
            return;
        }

        HintError.Visibility = Visibility.Collapsed;
        if (projects.Count == 0)
        {
            PanProjectList.Children.Clear();
            PanProjectList.Children.Add(new MyHint
            {
                Text = Lang.Text("Download.ServerPack.NoResult"), Theme = MyHint.Themes.Yellow, HasBorder = true
            });
            return;
        }

        foreach (var project in added)
            PanProjectList.Children.Add(BuildProjectItem(project));
    }

    private MyListItem BuildProjectItem(ModServerPackDownload.ServerPackProject project)
    {
        var sourceLabel = Lang.Text(project.Source == ModServerPackDownload.PackSource.CurseForge
            ? "Download.ServerPack.Source.CurseForge"
            : "Download.ServerPack.Source.Modrinth");
        var info = sourceLabel;
        if (!string.IsNullOrWhiteSpace(project.Description))
            info += " · " + project.Description;
        var item = new MyListItem
        {
            SvgIcon = "lucide/package",
            SnapsToDevicePixels = true,
            Title = project.Name,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Info = info,
            Tag = project
        };
        item.Click += (_, _) => EnterFilePage(project);
        return item;
    }

    #endregion

    #region 版本列表

    private void EnterFilePage(ModServerPackDownload.ServerPackProject project)
    {
        PanProjects.Visibility = Visibility.Collapsed;
        PanFiles.Visibility = Visibility.Visible;
        PanBack.ScrollToHome();
        LabProjectName.Text = project.Name;
        PanFileList.Children.Clear();
        PanFileList.Children.Add(new MyLoading
        {
            Text = Lang.Text("Download.ServerPack.Loading"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10)
        });
        ModBase.RunInThread(() =>
        {
            List<ModServerPackDownload.ServerPackFile> files;
            try
            {
                files = ModServerPackDownload.GetFiles(project);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[ServerPack] 获取 {project.Name} 的服务端文件失败");
                ModBase.RunInUi(() =>
                {
                    PanFileList.Children.Clear();
                    PanFileList.Children.Add(new MyHint
                    {
                        Text = Lang.Text("Download.ServerPack.Error.FilesFailed", ex.Message),
                        Theme = MyHint.Themes.Red, HasBorder = true
                    });
                });
                return;
            }

            ModBase.RunInUi(() =>
            {
                PanFileList.Children.Clear();
                if (files.Count == 0)
                {
                    PanFileList.Children.Add(new MyHint
                    {
                        Text = Lang.Text("Download.ServerPack.NoServerPack"), Theme = MyHint.Themes.Yellow,
                        HasBorder = true
                    });
                    return;
                }

                foreach (var file in files)
                    PanFileList.Children.Add(BuildFileItem(file));
            });
        });
    }

    private void ExitFilePage()
    {
        PanFiles.Visibility = Visibility.Collapsed;
        PanProjects.Visibility = Visibility.Visible;
        PanBack.ScrollToHome();
    }

    private MyListItem BuildFileItem(ModServerPackDownload.ServerPackFile file)
    {
        var versionText = string.IsNullOrWhiteSpace(file.GameVersion)
            ? Lang.Text("Select.Server.Core.Unknown")
            : file.GameVersion;
        var loaderText = string.IsNullOrWhiteSpace(file.Loader)
            ? Lang.Text("Download.ServerPack.Type.Modpack")
            : file.Loader;
        var info = Lang.Text("Download.ServerPack.File.Info", versionText, loaderText);
        info += " · " + Lang.Text("Download.ServerPack.ClickToDownload");
        var item = new MyListItem
        {
            SvgIcon = "lucide/server",
            SnapsToDevicePixels = true,
            Title = string.IsNullOrWhiteSpace(file.DisplayName)
                ? file.FileName
                : $"{file.DisplayName} ({file.FileName})",
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Info = info,
            Tag = file
        };
        item.Click += (_, _) => StartDownload(file);
        return item;
    }

    #endregion

    #region 下载

    private void StartDownload(ModServerPackDownload.ServerPackFile file)
    {
        if (isDownloading)
            return;
        isDownloading = true;
        try
        {
            ModServerPackDownload.Download(file,
                name => ModBase.RunInUi(() =>
                {
                    isDownloading = false;
                    ModInstanceList.mcInstanceListLoader.Start(ModFolder.mcFolderSelected, true);
                    HintService.Hint(Lang.Text("Download.ServerPack.DownloadFinished", name), HintType.Success);
                }),
                () => ModBase.RunInUi(() => isDownloading = false));
        }
        catch (Exception ex)
        {
            isDownloading = false;
            ModBase.Log(ex, Lang.Text("Download.ServerPack.Error.DownloadFailed", file.FileName),
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Download.ServerPack.Error.DownloadFailed", file.FileName));
        }
    }

    private void BtnMore_Click(object sender, MouseButtonEventArgs e)
    {
        LoadProjects();
    }

    #endregion
}