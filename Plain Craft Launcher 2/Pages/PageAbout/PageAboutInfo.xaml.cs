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
}
