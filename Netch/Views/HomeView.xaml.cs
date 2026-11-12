using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Netch.Views;

/// <summary>主页（04§4）：服务器列表选中变化时滚动到选中项（搜索清空后恢复的高亮项可见）；
/// 运行中锁节点选择（咩咩 2026-10-31 反馈①）</summary>
public partial class HomeView : UserControl
{
    /// <summary>用户正在按着鼠标操作节点列表（down→up 之间）：此窗口内选中变化不滚动。
    /// 咩咩 2026-10-31 反馈②：双击底行时第一击选中即触发 ScrollIntoView，列表把该行滚到视口
    /// 中间、底部滚入新行，第二击落点变到下一项——点中的目标本来就在鼠标下方，滚动纯属有害</summary>
    private bool _mouseSelectingServer;

    /// <summary>锁定提示节流（连点空行不刷屏）</summary>
    private DateTime _lastLockTipUtc = DateTime.MinValue;

    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ScrollToSelectedServer();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ViewModels.HomeViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is ViewModels.HomeViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;

        ScrollToSelectedServer();
    }

    /// <summary>
    ///     咩咩 2026-10-31 反馈②：上次选中的节点由 VM 在视图创建前恢复（SelectedServer 之后不再变化，
    ///     PropertyChanged 滚动不触发），首帧/回到主页时主动定位到选中项。延到 Loaded 优先级执行：
    ///     模板与首帧布局就绪后再滚，未加载/选中项被过滤时 ScrollIntoView 为 no-op。
    /// </summary>
    private void ScrollToSelectedServer()
    {
        if (DataContext is not ViewModels.HomeViewModel { SelectedServer: { } row })
            return;

        if (FindName("serverList") is not ListBox list)
            return;

        list.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () => list.ScrollIntoView(row));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.HomeViewModel.SelectedServer)
            || sender is not ViewModels.HomeViewModel { SelectedServer: { } row })
            return;

        // 鼠标点击引起的变化不滚动：目标就在光标下，滚动会让双击第二击错位（见 _mouseSelectingServer）
        if (_mouseSelectingServer)
            return;

        if (FindName("serverList") is ListBox list)
            list.ScrollIntoView(row);
    }

    /// <summary>按下节点列表：记录点击窗口（供滚动守卫）；运行中吃掉非按钮区域的按下（锁选择，反馈①）</summary>
    private void ServerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseSelectingServer = true;

        if (DataContext is not ViewModels.HomeViewModel { CanSelectServer: true })
        {
            // 行内按钮（测速/复制链接等 ButtonBase）与滚动条放行，其余吃掉：不产生选中、不触发滚动
            if (e.OriginalSource is System.Windows.DependencyObject source
                && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is null
                && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null)
            {
                e.Handled = true;
                NotifyLockTipThrottled();
            }
        }
    }

    private void ServerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _mouseSelectingServer = false;

    /// <summary>拖出列表松开收不到 Up（不经 Preview 路由），离开时复位防标志卡死</summary>
    private void ServerList_MouseLeave(object sender, MouseEventArgs e)
        => _mouseSelectingServer = false;

    private void NotifyLockTipThrottled()
    {
        var now = DateTime.UtcNow;
        if (now - _lastLockTipUtc < TimeSpan.FromSeconds(2))
            return;

        _lastLockTipUtc = now;
        Global.UIHub.NotifyTip(Localization.I18N.T("home.server.lockedWhileRunning"), info: false);
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject from) where T : class
    {
        while (from is not null)
        {
            if (from is T hit)
                return hit;

            from = System.Windows.Media.VisualTreeHelper.GetParent(from);
        }

        return null;
    }

    /// <summary>新建模式菜单点击后收起 Popup（命令在 VM，IsChecked 需在此复位）</summary>
    private void NewModeMenu_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (FindName("newModeToggle") is System.Windows.Controls.Primitives.ToggleButton toggle)
            toggle.IsChecked = false;
    }
}
