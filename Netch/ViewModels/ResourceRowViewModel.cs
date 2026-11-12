using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Controllers;
using Netch.Models;
using Netch.Localization;
using Netch.Services;
using Netch.Utils;
using Netch.UI.Hub;

namespace Netch.ViewModels;

/// <summary>
///     外部资源统一行 VM（R3）：名称 | 详情（版本/大小/时间）| 徽章 | 行操作（更新/删除/禁用/自动更新/恢复默认）。
///     一套 DataTemplate 渲染全部资源，可见性由能力位（ExternalResourceCaps）决定；
///     内核可更新通道（sing-box/Xray）的操作转发 KernelChannelViewModel（停-换-启联动在其内）。
/// </summary>
public partial class ResourceRowViewModel : ObservableObject
{
    private readonly ExternalResource _resource;

    /// <summary>内核可更新通道（kernel.singbox / kernel.xray）；其余资源为 null</summary>
    public KernelChannelViewModel? Kernel { get; }

    /// <summary>宿主注入：自定义规则集删除（删配置 + 连带缓存快照进 .trash）</summary>
    internal Func<ResourceRowViewModel, Task>? DeleteHostHandler { get; set; }

    /// <summary>宿主注入：自定义规则集编辑（弹 RuleSetDialog）</summary>
    internal Func<ResourceRowViewModel, Task>? EditHostHandler { get; set; }

    /// <summary>上次更新经备胎镜像（本次会话内，徽章如实显示「经镜像更新」）</summary>
    private bool _viaMirror;

    /// <summary>检查到远端有新版（检查动作/调度器置位，更新后清除）</summary>
    private bool _updatable;

    public ResourceRowViewModel(ExternalResource resource, KernelChannelViewModel? kernel = null)
    {
        _resource = resource;
        Kernel = kernel;

        DisplayName = I18N.T(resource.DisplayNameKey);

        Disabled = ExternalResourceService.IsDisabled(resource);
        AutoUpdate = ExternalResourceService.IsAutoUpdateOn(resource);

        if (Kernel != null)
            Kernel.PropertyChanged += (_, _) => SyncKernelText();
    }

    public string Key => _resource.Key;

    public ExternalResource Resource => _resource;

    #region 展示

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _detailText = "";

    [ObservableProperty]
    private string _badgeText = "";

    [ObservableProperty]
    private string _badgeToolTip = "";

    /// <summary>徽章主题画刷键（DynamicResource 消费；空=无徽章底色）</summary>
    [ObservableProperty]
    private string _badgeBrushKey = "";

    public Visibility BadgeVis => string.IsNullOrEmpty(BadgeText) ? Visibility.Collapsed : Visibility.Visible;

    #endregion

    #region 能力可见性（构造后不变）

    public Visibility UpdateVis => _resource.Caps.HasFlag(ExternalResourceCaps.CanUpdate) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteVis => _resource.Caps.HasFlag(ExternalResourceCaps.CanDelete) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DisableVis => _resource.Caps.HasFlag(ExternalResourceCaps.CanDisable) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AutoUpdateVis => _resource.Caps.HasFlag(ExternalResourceCaps.CanAutoUpdate) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCustomRuleSet => _resource.Key.StartsWith("ruleset.custom.", StringComparison.Ordinal);

    public Visibility EditVis => IsCustomRuleSet ? Visibility.Visible : Visibility.Collapsed;

    #endregion

    #region 状态

    [ObservableProperty]
    private bool _disabled;

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private int _progress;

    public Visibility ProgressVis => Busy && Progress > 0 && Progress < 100 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnProgressChanged(int value) => OnPropertyChanged(nameof(ProgressVis));

    #endregion

    private void SyncKernelText()
    {
        if (Kernel == null)
            return;

        var text = Kernel.VersionText;
        if (!string.IsNullOrEmpty(Kernel.StatusText))
            text = string.IsNullOrEmpty(text) ? Kernel.StatusText : $"{text}\n{Kernel.StatusText}";

        DetailText = text;
    }

    /// <summary>刷新行展示（版本/徽章/开关；UI 线程调用）</summary>
    public async Task ReloadAsync()
    {
        DisplayName = I18N.T(_resource.DisplayNameKey);
        Disabled = ExternalResourceService.IsDisabled(_resource);
        AutoUpdate = ExternalResourceService.IsAutoUpdateOn(_resource);

        if (Kernel != null)
        {
            await Kernel.LoadVersionAsync();
            SyncKernelText();
        }

        var state = ExternalResourceService.GetState(_resource);

        DetailText = DescribeFileState();
        RefreshBadge(state);
    }

    /// <summary>finally 路径的刷新（起进程查版本可能抛）：失败记日志，不吞主体异常、不打断收尾</summary>
    private async Task SafeReloadAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Reload resource row failed: {Key}", _resource.Key);
        }
    }

    private string DescribeFileState()
    {        if (!ExternalResourceService.Exists(_resource))
            return I18N.T("external.badge.notInstalled");

        var size = new FileInfo(ExternalResourceService.GetFullPath(_resource)).Length;
        var sizeText = size >= 1024 * 1024 ? $"{size / 1024.0 / 1024.0:F1} MB" : $"{size / 1024.0:F0} KB";
        var last = ExternalResourceService.GetState(_resource).LastUpdateAt;
        var lastText = last == null ? I18N.T("external.neverUpdated") : I18N.TF("external.lastUpdateFormat", last.Value.ToString("yyyy-MM-dd HH:mm"));
        return $"{sizeText} · {lastText}";
    }

    private void RefreshBadge(ExternalResourceState state)
    {
        if (Disabled)
            SetBadge(I18N.T("external.badge.disabled"), "Brush.TextDisabled", I18N.T("external.badge.disabled"));
        else if (!ExternalResourceService.Exists(_resource))
        {
            SetBadge(I18N.T("external.badge.notInstalled"), "Brush.Warning", I18N.T("external.badge.notInstalled"));
        }
        else if (_viaMirror)
            SetBadge(I18N.T("external.badge.mirror"), "Brush.Success", I18N.T("external.badge.mirror"));
        else if (_updatable)
            SetBadge(I18N.T("external.badge.updatable"), "Brush.Warning", I18N.T("external.badge.updatable"));
        else if (state.LastUpdateAt != null || state.LastCheckAt != null)
            SetBadge(I18N.T("external.badge.upToDate"), "Brush.Success", "");
        else
            SetBadge("", "", "");

        OnPropertyChanged(nameof(BadgeVis));
    }

    private void SetBadge(string text, string brushKey, string toolTip)
    {
        BadgeText = text;
        BadgeBrushKey = brushKey;
        BadgeToolTip = toolTip;
    }

    /// <summary>失败徽章（更新/检查失败：原因进 ToolTip，旧文件原样保留）</summary>
    public void MarkFailed(string reason)
    {
        _updatable = false;
        SetBadge(I18N.TF("external.badge.failedFormat", reason), "Brush.Danger", reason);
        OnPropertyChanged(nameof(BadgeVis));
    }

    /// <summary>调度器/检查置位「可更新」徽章（R4 徽章驱动入口）</summary>
    public void MarkUpdatable()
    {
        _updatable = true;
        RefreshBadge(ExternalResourceService.GetState(_resource));
    }

    #region 行操作

    private bool NotBusy() => !Busy;

    /// <summary>更新（检查 + 按需下载替换）。失败：徽章=失败+原因，旧文件原样保留；手动路径失败弹 Alert</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UpdateAsync()
    {
        if (Busy)
            return;

        Busy = true;
        Progress = 1;
        OnPropertyChanged(nameof(ProgressVis));
        try
        {
            switch (_resource.Provider)
            {
                case ExternalResourceProviderKind.GitHubRelease when Kernel != null:
                {
                    var latest = await Kernel.CheckAsync();
                    if (latest != null)
                        await Kernel.UpdateAsync(latest, new Progress<int>(v => Progress = v));

                    _updatable = false;
                    _viaMirror = false;
                    break;
                }

                case ExternalResourceProviderKind.HttpFile:
                {
                    var (outcome, usedUrl, viaMirror) = await ExternalResourceService.UpdateHttpFileAsync(
                        _resource, new Progress<int>(v => Progress = v));
                    if (outcome == ExternalResourceService.ResourceUpdateOutcome.Updated)
                    {
                        _updatable = false;
                        _viaMirror = viaMirror && usedUrl != _resource.Url;
                    }
                    else
                    {
                        _updatable = false;
                        _viaMirror = false;
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "External resource update failed: {Key}", _resource.Key);
            MarkFailed(ex.Message);
            Global.UIHub.Alert($"{I18N.T("external.updateFailed")}\n{ex.Message}", DialogLevel.Error);
        }
        finally
        {
            Busy = false;
            Progress = 0;
            OnPropertyChanged(nameof(ProgressVis));
            // F-4b（2026-11-04 审查）：ReloadAsync 起进程查版本可能抛，裸放 finally 会吞掉主体异常
            await SafeReloadAsync();
        }
    }

    /// <summary>删除（确认框 → .trash 隔离；自定义规则集转发宿主删配置+缓存）</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DeleteAsync()
    {
        if (Busy)
            return;

        if (IsCustomRuleSet && DeleteHostHandler != null)
        {
            await DeleteHostHandler(this);
            return;
        }

        if (!await Global.UIHub.ConfirmAsync(I18N.TF("external.confirmDelete", DisplayName)))
            return;

        Busy = true;
        try
        {
            if (Kernel != null)
                await Kernel.DeleteAsync();
            else
                ExternalResourceService.MoveToTrash(_resource);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "External resource delete failed: {Key}", _resource.Key);
            Global.UIHub.Alert($"{I18N.T("external.updateFailed")}\n{ex.Message}", DialogLevel.Error);
        }
        finally
        {
            Busy = false;
            await SafeReloadAsync();
        }
    }

    /// <summary>编辑（仅自定义规则集：转发宿主弹 RuleSetDialog）</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task EditAsync()
    {
        if (EditHostHandler != null)
            await EditHostHandler(this);
    }

    /// <summary>禁用开关（配置生成/模式选择器联动语义见能力矩阵；写回设置并保存）</summary>
    [RelayCommand]
    private async Task ToggleDisabledAsync()
    {
        var newValue = !Disabled;
        await ExternalResourceService.MutateStateAsync(_resource, s => s.Disabled = newValue);
        Disabled = newValue;
        RefreshBadge(ExternalResourceService.GetState(_resource));
    }

    /// <summary>自动更新小开关（用户显式设置优先于注册表默认）</summary>
    [RelayCommand]
    private async Task ToggleAutoUpdateAsync()
    {
        var newValue = !AutoUpdate;
        await ExternalResourceService.MutateStateAsync(_resource, s => s.AutoUpdate = newValue);
        AutoUpdate = newValue;
    }

    #endregion
}
