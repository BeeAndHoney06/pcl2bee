using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageAboutUpdate
{
    private string _releaseUrl = "";

    public PageAboutUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LabCurrent.Text = Lang.Text("About.Update.Current", BeeVersion.Name, BeeVersion.Code);
        };
    }

    private void BtnCheck_Click(object sender, MouseButtonEventArgs e)
    {
        BtnCheck.IsEnabled = false;
        BtnOpen.Visibility = Visibility.Collapsed;
        LabNotes.Visibility = Visibility.Collapsed;
        HintResult.Theme = MyHint.Themes.Blue;
        HintResult.Text = Lang.Text("About.Update.Checking");
        HintResult.Visibility = Visibility.Visible;
        ModBase.RunInNewThread(() =>
        {
            var info = UpdateInfoService.Fetch(true);
            ModBase.RunInUi(() =>
            {
                BtnCheck.IsEnabled = true;
                LabNotes.Visibility = Visibility.Collapsed;
                HintResult.Visibility = Visibility.Visible;
                if (info is null)
                {
                    HintResult.Theme = MyHint.Themes.Red;
                    HintResult.Text = Lang.Text("About.Update.Failed");
                }
                else if (!info.IsNewerThan(BeeVersion.Code))
                {
                    HintResult.Theme = MyHint.Themes.Blue;
                    HintResult.Text = Lang.Text("About.Update.Latest");
                }
                else
                {
                    HintResult.Theme = MyHint.Themes.Yellow;
                    HintResult.Text = Lang.Text("About.Update.Found", info.Version);
                    LabNotes.Text = info.Notes;
                    LabNotes.Visibility = Visibility.Visible;
                    _releaseUrl = string.IsNullOrWhiteSpace(info.ReleaseUrl)
                        ? "https://github.com/BeeAndHoney06/pcl2bee/releases/latest"
                        : info.ReleaseUrl;
                    BtnOpen.Visibility = Visibility.Visible;
                }
            });
        }, "About Update Check");
    }

    private void BtnOpen_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_releaseUrl))
            ModBase.OpenWebsite(_releaseUrl);
    }
}
