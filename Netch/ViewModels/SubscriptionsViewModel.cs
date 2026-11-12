using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Localization;
using Netch.Models;
using Netch.UI.Hub;
using Netch.Utils;
using Application = System.Windows.Application;

namespace Netch.ViewModels;

/// <summary>
///     订阅页 VM（06§B2 行为平移）：列表增删改/启停、更新全部/单个、右键 Copy link/Delete/Delete Servers。
///     更新走 SubscriptionUtil.UpdateServersAsync（含订阅走代理 Setting）；状态文本双写工具行 + 状态栏。
/// </summary>
[Fody.ConfigureAwait(true)]  // AsyncRelayCommand 完成回调须回 UI 线程刷新 CanExecute（Fody 全局 false 会把 continuation 丢线程池崩 UI）
public partial class SubscriptionsViewModel : ObservableObject
{
    public ObservableCollection<Subscription> Subscriptions { get; } = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _busy;

    public SubscriptionsViewModel()
    {
        Reload();
        UIEvents.ServersChanged += () => Reload();
    }

    public void Reload()
    {
        Subscriptions.Clear();
        foreach (var sub in Global.Settings.Subscription)
            Subscriptions.Add(sub);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (Global.Settings.Subscription.Count <= 0)
        {
            Global.UIHub.Alert(I18N.T("subscription.none"));
            return;
        }

        await UpdateCoreAsync();
    }

    /// <summary>单个订阅更新（只更新该订阅；2026-11-04 审查低危①：此前忽略参数实际更新全部）</summary>
    [RelayCommand]
    private async Task UpdateOneAsync(Subscription? sub)
    {
        if (sub == null)
            return;

        await UpdateCoreAsync(sub);
    }

    private async Task UpdateCoreAsync(Subscription? only = null)
    {
        Busy = true;
        StatusText = I18N.T("subscription.updating");
        Global.UIHub.StatusText(I18N.T("subscription.updating"));
        try
        {
            await SubscriptionUtil.UpdateServersAsync(only: only);
            // 咩咩 2026-10-31 反馈①：更新出的节点要立刻反映到主页——与 MainViewModel 的更新路径同发
            // ServersChanged（HomeViewModel 只听此事件重建列表，此前订阅页这条路径漏发=更新后主页空白）
            UIEvents.RaiseServersChanged();
            await Configuration.SaveAsync();
            StatusText = I18N.T("subscription.updated");
            Global.UIHub.StatusText(I18N.T("subscription.updated"));
            Reload();
        }
        catch (Exception e)
        {
            Global.UIHub.NotifyTip(I18N.T("main.unhandledUpdateServersError") + "\n" + e.Message, info: false);
            Log.Error(e, "Unhandled Update servers error");
            StatusText = string.Empty;
            Global.UIHub.StatusText(null);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>添加/编辑（SubscriptionDialog：Remark/Link/User-Agent + Link 非空校验）。
    ///     重名拦截（2026-11-04 审查 F-6）：节点归属按 Group=Remark 删改，重名订阅会互相吞噬节点</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var dialog = Views.SubscriptionDialog.Create();
        dialog.Owner = Application.Current?.MainWindow;
        if (dialog.ShowDialog() != true)
            return;

        if (HasDuplicateRemark(dialog.Result.Remark, null))
            return;

        Global.Settings.Subscription.Add(dialog.Result);
        Reload();
        await Configuration.SaveAsync();
    }

    [RelayCommand]
    private async Task EditAsync(Subscription? sub)
    {
        if (sub == null)
            return;

        var oldRemark = sub.Remark;
        var dialog = Views.SubscriptionDialog.Edit(sub);
        dialog.Owner = Application.Current?.MainWindow;
        if (dialog.ShowDialog() != true)
            return;

        // 改名后节点归属要跟着迁移（Group=旧 Remark 的节点归新名），重名同样拦截
        if (HasDuplicateRemark(sub.Remark, sub))
            return;

        if (sub.Remark != oldRemark)
            foreach (var server in Global.Settings.Server.Where(s => s.Group.Equals(oldRemark)).ToList())
                server.Group = sub.Remark;

        Reload();
        UIEvents.RaiseServersChanged();
        await Configuration.SaveAsync();
    }

    /// <summary>重名检查：存在同 Remark 的其他订阅即弹提示返回 true（F-6：重名订阅更新/删节点会互相吞噬）</summary>
    private static bool HasDuplicateRemark(string remark, Subscription? self)
    {
        if (Global.Settings.Subscription.Any(s => s != self && s.Remark.Equals(remark)))
        {
            Global.UIHub.Alert(I18N.T("subscription.duplicateRemark"), DialogLevel.Warning);
            return true;
        }

        return false;
    }

    /// <summary>启停（旧 Unselect 语义=取消勾选）</summary>
    [RelayCommand]
    private async Task ToggleAsync(Subscription? sub)
    {
        if (sub == null)
            return;

        sub.Enable = !sub.Enable;
        Reload();
        await Configuration.SaveAsync();
    }

    /// <summary>删除订阅 + 该组节点（旧 Delete 菜单语义：确认后连组清理）</summary>
    [RelayCommand]
    private async Task DeleteAsync(Subscription? sub)
    {
        if (sub == null)
            return;

        if (!await Global.UIHub.ConfirmAsync(I18N.T("subscription.deleteNotWillCleanUp")))
            return;

        DeleteServersByGroup(sub.Remark);
        Global.Settings.Subscription.Remove(sub);
        Reload();
        UIEvents.RaiseServersChanged();
        await Configuration.SaveAsync();
    }

    /// <summary>仅删除该组节点（订阅保留；平移旧 Delete Servers 菜单）</summary>
    [RelayCommand]
    private async Task DeleteServersAsync(Subscription? sub)
    {
        if (sub == null)
            return;

        if (!await Global.UIHub.ConfirmAsync(I18N.T("subscription.deleteServersConfirm")))
            return;

        // 仅删节点，订阅本体保留（2026-11-04 审查 F-2：此前误删订阅配置，用户数据丢失）
        DeleteServersByGroup(sub.Remark);
        UIEvents.RaiseServersChanged();
        await Configuration.SaveAsync();
    }

    /// <summary>右键 Copy link</summary>
    [RelayCommand]
    private void CopyLink(Subscription? sub)
    {
        if (sub == null)
            return;

        try
        {
            System.Windows.Clipboard.SetText(sub.Link);
        }
        catch (Exception)
        {
            // 剪贴板被占用（忽略）
        }
    }

    private static void DeleteServersByGroup(string group)
    {
        Global.Settings.Server.RemoveAll(s => s.Group.Equals(group));
    }
}
