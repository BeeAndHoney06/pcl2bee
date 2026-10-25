using System.Windows;
using System.Windows.Controls;

namespace PCL;

public partial class PageAboutLeft
{
    public FormMain.PageSubType pageID = FormMain.PageSubType.AboutInfo;

    public PageAboutLeft()
    {
        InitializeComponent();
        ItemInfo.Check += PageCheck;
        ItemUpdate.Check += PageCheck;
        ItemLicense.Check += PageCheck;
    }

    private void PageCheck(object sender, ModBase.RouteEventArgs e)
    {
        if (sender is not MyListItem { Tag: not null } item) return;
        PageChange((FormMain.PageSubType)ModBase.Val(item.Tag));
    }

    public object PageGet(FormMain.PageSubType id = FormMain.PageSubType.AboutInfo)
    {
        switch (id)
        {
            case FormMain.PageSubType.AboutUpdate:
                return ModMain.frmAboutUpdate ??= new PageAboutUpdate();
            case FormMain.PageSubType.AboutLicense:
                return ModMain.frmAboutLicense ??= new PageAboutLicense();
            default:
                return ModMain.frmAboutInfo ??= new PageAboutInfo();
        }
    }

    /// <summary>
    ///     切换关于页的子页面。
    /// </summary>
    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id) return;
        ModAnimation.AniControlEnabled += 1;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "切换关于页失败：" + (int)id, ModBase.LogLevel.Feedback);
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        ModAnimation.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(ContentPresenter.ContentProperty, null);
        ModMain.frmMain.pageRight = target;
        ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnExit();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnForceExit();
                ModMain.frmMain.PanMainRight.Child = ModMain.frmMain.pageRight;
                ModMain.frmMain.pageRight.Opacity = 0d;
            }, 130),
            ModAnimation.AaCode(() =>
            {
                ModMain.frmMain.pageRight.Opacity = 1d;
                ModMain.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }
}
