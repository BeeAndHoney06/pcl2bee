using System.Windows.Input;

namespace PCL;

public partial class PageAboutSponsor
{
    private const string SponsorUrl = "https://afdian.com/a/pcl2bee";

    public PageAboutSponsor()
    {
        InitializeComponent();
    }

    private void BtnSponsor_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenWebsite(SponsorUrl);
}
