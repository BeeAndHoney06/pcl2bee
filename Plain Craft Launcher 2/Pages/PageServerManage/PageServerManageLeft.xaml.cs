// ─────────────────────────────────────────────────────────────────────────────
// 服务器管理模块：移植自 ErieneNeko/PCL-CE-ServerControlPad
//   https://github.com/ErieneNeko/PCL-CE-ServerControlPad
//   原作者：ErieneNeko    许可：Apache License 2.0
//   本文件在原作基础上做了适配修改（命名空间/本地化/对话框 API 对齐本项目）。
// ─────────────────────────────────────────────────────────────────────────────
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageServerManageLeft
{
    public FormMain.PageSubType PageId = FormMain.PageSubType.ServerLocal;

    public PageServerManageLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        ItemLocal.SetChecked(true, false, false);
    }

    private void PageCheck(object senderRaw, Common.RouteEventArgs e)
    {
        if (senderRaw is MyListItem { Tag: { } tag })
            PageChange((FormMain.PageSubType)Common.Val(tag));
    }

    public object PageGet(FormMain.PageSubType id)
    {
        if (id == FormMain.PageSubType.Default)
            id = PageId;
        switch (id)
        {
            case FormMain.PageSubType.ServerLocal:
            {
                Launcher.frmServerLocal ??= new PageServerManageLocal();
                return Launcher.frmServerLocal;
            }
            case FormMain.PageSubType.ServerRemote:
            {
                Launcher.frmServerRemote ??= new PageServerManageRemote();
                return Launcher.frmServerRemote;
            }
            default:
                throw new Exception("未知的服务器管理子页面：" + (int)id);
        }
    }

    public void PageChange(FormMain.PageSubType id)
    {
        if (PageId == id)
            return;
        Animations.AniBeginControl();
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            PageId = id;
        }
        catch (Exception ex)
        {
            Common.Log(ex, "切换服务器管理子页面失败", Common.LogLevel.Feedback);
        }
        finally
        {
            Animations.AniEndControl();
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        Animations.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(ContentPresenter.ContentProperty, null);
        Launcher.frmMain!.pageRight = target;
        ((MyPageRight)Launcher.frmMain.PanMainRight.Child).PageOnExit();
        Animations.AniStart(new List<Animations.AniData>
        {
            Animations.AaCode(() =>
            {
                ((MyPageRight)Launcher.frmMain.PanMainRight.Child).PageOnForceExit();
                Launcher.frmMain.PanMainRight.Child = Launcher.frmMain.pageRight;
                Launcher.frmMain.pageRight.Opacity = 0d;
            }, 130),
            Animations.AaCode(() =>
            {
                Launcher.frmMain.pageRight.Opacity = 1d;
                Launcher.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }
}
