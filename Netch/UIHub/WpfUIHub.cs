using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Netch.Controls;
using Netch.Controllers;
using Netch.Enums;
using Netch.ViewModels;
using BalloonIcon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon;

// VSTHRD001: IMainUIHub 契约即「任意线程调用、内部调度到 UI 线程」，同步 Invoke 是刻意语义（与旧 WinFormsUIHub 等价）
#pragma warning disable VSTHRD001

namespace Netch.UI.Hub;

/// <summary>
///     IMainUIHub 的 WPF 实现（04 接管）：任意线程可调，内部经 Dispatcher 调度；
///     StatusText/State 转发 MainViewModel 单例（App.CurrentMain）；
///     Alert/Confirm 用 Token 化的 ContentDialogWindow（无主窗时降级系统 MessageBox）。
/// </summary>
public class WpfUIHub : IMainUIHub
{
    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public void StatusText(string? text)
    {
        BeginInvoke(() => App.CurrentMain?.SetCustomStatusText(text));
    }

    public void NotifyTip(string text, int timeout = 0, bool info = true)
    {
        BeginInvoke(() =>
        {
            var tray = App.CurrentTrayIcon;
            if (tray != null)
            {
                tray.ShowBalloonTip(UpdateChecker.Name, text, info ? BalloonIcon.Info : BalloonIcon.Error);
                return;
            }

            // 托盘不可用（极早/极晚）降级弹窗
            Alert(text, info ? DialogLevel.Info : DialogLevel.Error);
        });
    }

    public void Invoke(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public void BeginInvoke(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted)
            return;

        _ = dispatcher.BeginInvoke(action);
    }

    public void ShowMainForm()
    {
        BeginInvoke(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window == null)
                return;

            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }

    public Task<bool> ConfirmAsync(string text, string? title = null)
    {
        return Task.FromResult(InvokeWithResult(() => ContentDialogWindow.Confirm(OwnerWindow(), text, title)));
    }

    public void Alert(string text, DialogLevel level = DialogLevel.Info, string? title = null)
    {
        BeginInvoke(() => ContentDialogWindow.Alert(OwnerWindow(), text, level, title));
    }

    public State State => App.CurrentMain?.State ?? State.Waiting;

    private static Window? OwnerWindow()
    {
        var window = Application.Current?.MainWindow;
        return window is { IsLoaded: true } ? window : null;
    }

    private static T InvokeWithResult<T>(Func<T> func)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted)
            return default!;

        return dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
    }
}
