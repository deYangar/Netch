using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Netch.UI.Theme;
using Netch.ViewModels;
using Wpf.Ui.Controls;

namespace Netch.Views;

/// <summary>
///     主窗 code-behind（04§3 纪律：只做尺寸记忆 / 系统主题钩子 / 导航转发，业务一律在 VM）。
/// </summary>
public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>默认窗口尺寸（XAML 初值/恢复默认共用单一来源；新 UI 信息密度高于 WinForms 老版，740×340 连接列放不下）</summary>
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 800;

    /// <summary>应用默认窗口尺寸（按主屏工作区收敛，小屏不超界）</summary>
    public static void ApplyDefaultWindowSize(Window window)
    {
        var work = SystemParameters.WorkArea;
        window.Width = Math.Min(DefaultWidth, work.Width);
        window.Height = Math.Min(DefaultHeight, work.Height);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 系统主题变化钩子（02§3）：WM_SETTINGCHANGE ImmersiveColorSet
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WndProc);

        var work = SystemParameters.WorkArea;

        // 尺寸记忆（a6bf2c1 语义）：Loaded 应用；无记忆走默认；越界（换小屏）收敛到工作区
        if (Global.Settings.MainFormWidth > 0 && Global.Settings.MainFormHeight > 0)
        {
            Width = Math.Clamp(Global.Settings.MainFormWidth, MinWidth, Math.Max(MinWidth, work.Width));
            Height = Math.Clamp(Global.Settings.MainFormHeight, MinHeight, Math.Max(MinHeight, work.Height));
        }
        else
        {
            ApplyDefaultWindowSize(this);
        }

        ViewModel.OnMainWindowLoaded();

        // 初始选中态：主页项高亮（与 InitializeNavigation 的初始页对齐）
        SelectNavItem(navView.MenuItems.OfType<NavigationViewItem>()
            .First(i => i.TargetPageTag == MainViewModel.PageHome));
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x001A /*WM_SETTINGCHANGE*/ && wParam == 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            ThemeManager.OnSystemThemeChanged();

        return 0;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        ViewModel.HandleCloseRequest(e);
    }

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        // WPF-UI 4.3 手工 XAML 场景（无 PageService/DI，TargetPageType 不设）下其内部导航系统整链静默：
        // NavigationViewItem.OnClick 里 TargetPageType==null 不调 OnNavigationViewItemClick，
        // SelectionChanged/ItemInvoked 永不触发（P1 根因）。导航走 ButtonBase 标准 Click，选中态自管。
        if (sender is not NavigationViewItem item)
            return;

        SelectNavItem(item);
        ViewModel.NavigateCommand.Execute(item.TargetPageTag);
    }

    /// <summary>侧栏选中态自管：目标项 Activate、其余 Deactivate（IsActive 驱动 WPF-UI 模板高亮指示条）</summary>
    private void SelectNavItem(NavigationViewItem target)
    {
        foreach (var navItem in navView.MenuItems.OfType<NavigationViewItem>()
                     .Concat(navView.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            if (ReferenceEquals(navItem, target))
                navItem.Activate(navView);
            else
                navItem.Deactivate(navView);
        }
    }
}
