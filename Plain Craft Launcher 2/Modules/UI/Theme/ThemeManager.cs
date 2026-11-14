using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL;

public static class ThemeManager
{
    private static bool _contextMenuHandlerRegistered;

    public static bool IsDarkMode => ThemeService.IsDarkMode;

    public static ResourceDictionary AppResources => System.Windows.Application.Current.Resources;

    public static ModBase.MyColor colorGray1 = new(AppResources["ColorObjectGray1"]);
    public static ModBase.MyColor colorGray4 = new(AppResources["ColorObjectGray4"]);
    public static ModBase.MyColor colorGray5 = new(AppResources["ColorObjectGray5"]);
    public static ModBase.MyColor colorSemiTransparent = new(AppResources["ColorBrushSemiTransparent"]);

    public static void ThemeRefresh(int newTheme = -1)
    {
        RefreshGlassResources();
        colorGray1 = new ModBase.MyColor(AppResources["ColorObjectGray1"]);
        colorGray4 = new ModBase.MyColor(AppResources["ColorObjectGray4"]);
        colorGray5 = new ModBase.MyColor(AppResources["ColorObjectGray5"]);
        colorSemiTransparent = new ModBase.MyColor(AppResources["ColorBrushSemiTransparent"]);
        ThemeRefreshMain();
    }

    public static void ThemeRefreshMain()
    {
        ModBase.RunInUi(() =>
        {
            if (!ModMain.frmMain.IsLoaded) return;
            RefreshBackground();
            RefreshAllContextMenuThemes();
        });
    }
    
    private static void RefreshGlassResources()
    {
        var dark = IsDarkMode;
        SolidColorBrush Surface(byte alpha) => new(dark
            ? Color.FromArgb(alpha, 20, 20, 20)
            : Color.FromArgb(alpha, 128, 128, 128));
        AppResources["GlassSurface"] = Surface(18);
        AppResources["GlassWindow"] = Surface(8);
        AppResources["GlassHeader"] = new SolidColorBrush(Color.FromArgb(80, 24, 24, 24));
        AppResources["GlassControl"] = Surface(16);
        AppResources["GlassHover"] = Surface(40);
        AppResources["GlassDisabled"] = Surface(8);
        AppResources["GlassPopup"] = Surface(150);
        // 沿用这些资源的旧页面也使用玻璃底色，文字和状态色保持原值。
        foreach (var key in new[] { "ColorBrushHalfWhite", "ColorBrushSemiWhite" })
            AppResources[key] = Surface(16);
        AppResources["ColorBrushTransparentBackground"] = Surface(18);
        AppResources["ColorBrushToolTip"] = Surface(190);
        AppResources["ColorBrushBackground"] = Surface(18);
        AppResources["ColorBrushWhite"] = Surface(16);
        foreach (var key in new[] { "ColorBrushGray6", "ColorBrushGray7", "ColorBrushGray8" })
            AppResources[key] = Surface(16);
    }

    // 主页面背景
    private static void RefreshBackground()
    {
        ModMain.frmMain.PanForm.SetResourceReference(Panel.BackgroundProperty, "GlassWindow");
    }

    // 通用ContextMenu主题刷新
    private static void RefreshAllContextMenuThemes()
    {
        try
        {
            if (!_contextMenuHandlerRegistered)
            {
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                    new RoutedEventHandler(OnContextMenuOpened));
                _contextMenuHandlerRegistered = true;
            }

            foreach (Window window in System.Windows.Application.Current.Windows)
                RefreshContextMenusInElement(window);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "刷新ContextMenu主题时出错");
        }
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is ContextMenu contextMenu)
            {
                contextMenu.ClearValue(FrameworkElement.StyleProperty);
                contextMenu.UpdateDefaultStyle();
            }
        }
        catch
        {
            // 忽略个别错误
        }
    }

    private static void RefreshContextMenusInElement(DependencyObject element)
    {
        if (element is null)
            return;

        try
        {
            if (element is FrameworkElement { ContextMenu: not null } fe)
            {
                fe.ContextMenu.ClearValue(FrameworkElement.StyleProperty);
                fe.ContextMenu.UpdateDefaultStyle();
            }

            var childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
                RefreshContextMenusInElement(VisualTreeHelper.GetChild(element, i));
        }
        catch
        {
            // 忽略个别元素的错误，继续处理其他元素
        }
    }
}