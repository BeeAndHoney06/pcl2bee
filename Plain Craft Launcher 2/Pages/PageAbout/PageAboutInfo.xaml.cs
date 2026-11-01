using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageAboutInfo
{
    public PageAboutInfo()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LabVersion.Text = Lang.Text("About.Info.Version", BeeVersion.Name, BeeVersion.Code);
            HintCopied.Visibility = Visibility.Collapsed;
        };
    }

    private void BtnCopy_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.ClipboardSet(ModBase.versionBaseName);
        HintCopied.Visibility = Visibility.Visible;
    }

    private void BtnWebsite_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenWebsite("https://pcl2bee.pages.dev/");

    private void BtnSource_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenWebsite("https://github.com/BeeAndHoney06/pcl2bee/");

    /// <summary>
    ///     打开服务器管理模块的来源仓库（Apache-2.0 鸣谢）。
    /// </summary>
    private void ItemServerControlPad_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenWebsite("https://github.com/ErieneNeko/PCL-CE-ServerControlPad");
}
