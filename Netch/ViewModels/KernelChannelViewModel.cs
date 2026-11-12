using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Controllers;
using Netch.Models;
using Netch.Models.GitHubRelease;
using Netch.Localization;
using Netch.Services;
using Netch.UI.Hub;

namespace Netch.ViewModels;

/// <summary>
///     单内核资源行 VM（R2 迁入外部资源框架）：版本标签 + 检查更新 + 更新 + 删除。
///     互斥走 ExternalResourceService.BusyLock 全局资源锁（与文件资源/自动更新串行），
///     检查/更新结果写回资源状态（LastCheckAt/LastUpdateAt），更新主体含停-换-启联动（CoreUpdater.UpdateAsync）。
///     核心流程公开为 CheckAsync/UpdateAsync/DeleteAsync（R3 资源行统一转发），RelayCommand 薄包装。
/// </summary>
public partial class KernelChannelViewModel(CoreUpdater.KernelChannel channel) : ObservableObject
{
    private readonly CoreUpdater.KernelChannel _channel = channel;

    private Release? _latest;

    private ExternalResource Resource => ExternalResourceService.Find(_channel.ResourceKey)!;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _progress;

    public async Task LoadVersionAsync()
    {
        var version = await CoreUpdater.GetInstalledVersionAsync(_channel);
        VersionText = version == null
            ? I18N.TF("settings.0NotFoundBinDirectory", _channel.DisplayName)
            : $"{_channel.DisplayName}: {version}";
    }

    private void NotifyBusyChanged()
    {
        CheckUpdateCommand.NotifyCanExecuteChanged();
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        DeleteResourceCommand.NotifyCanExecuteChanged();
    }

    private bool NotBusy() => ExternalResourceService.BusyLock.CurrentCount > 0;

    /// <summary>检查更新并写回 LastCheckAt；持锁。返回是否有新版（Alert 失败由调用方决定）</summary>
    public async Task<Release?> CheckAsync()
    {
        Release? latest;
        await ExternalResourceService.BusyLock.WaitAsync();
        try
        {
            latest = await CoreUpdater.CheckUpdateAsync(_channel);
            await ExternalResourceService.MutateStateAsync(Resource, s => s.LastCheckAt = DateTime.Now);
        }
        finally
        {
            ExternalResourceService.BusyLock.Release();
        }

        _latest = latest;
        StatusText = latest == null
            ? I18N.T("main.alreadyLatestVersion")
            : I18N.TF("settings.newKernelVersionFound0", latest.tag_name);
        return latest;
    }

    /// <summary>下载并更新（含停-换-启联动）并写回 LastUpdateAt；持锁</summary>
    public async Task UpdateAsync(Release latest, IProgress<int>? progress = null)
    {
        var asset = _channel.MatchAsset(latest);
        if (asset == null)
        {
            Global.UIHub.Alert(I18N.T("settings.noSuitableKernelPackageFound"));
            return;
        }

        StatusText = I18N.T("settings.downloadingKernelUpdate");
        await ExternalResourceService.BusyLock.WaitAsync();
        try
        {
            await CoreUpdater.UpdateAsync(_channel, latest, asset, progress);
            await ExternalResourceService.MutateStateAsync(Resource, s =>
            {
                s.LastUpdateAt = DateTime.Now;
                s.LastCheckAt = DateTime.Now;
            });
            StatusText = I18N.TF("settings.kernelUpdated0", latest.tag_name);
            _latest = null;
        }
        finally
        {
            ExternalResourceService.BusyLock.Release();
        }

        await LoadVersionAsync();
    }

    /// <summary>删除（移入 .trash 隔离区；删后可经「更新」重新获取）；确认框由资源行统一弹</summary>
    public async Task DeleteAsync()
    {
        await ExternalResourceService.BusyLock.WaitAsync();
        try
        {
            ExternalResourceService.MoveToTrash(Resource);
        }
        finally
        {
            ExternalResourceService.BusyLock.Release();
        }

        StatusText = string.Empty;
        await LoadVersionAsync();
    }

    /// <summary>检查更新（命令包装：失败弹 Alert）</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckUpdateAsync()
    {
        StatusText = I18N.T("settings.checkingKernelUpdates");
        NotifyBusyChanged();
        try
        {
            await CheckAsync();
        }
        catch (Exception ex)
        {
            _latest = null;
            StatusText = string.Empty;
            Global.UIHub.Alert(I18N.T("settings.checkKernelUpdateFailed") + "\n" + ex.Message, DialogLevel.Error);
        }
        finally
        {
            NotifyBusyChanged();
        }
    }

    /// <summary>下载并更新（命令包装：先查最新再更新，失败弹 Alert）</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DownloadUpdateAsync()
    {
        StatusText = I18N.T("settings.checkingKernelUpdates");
        NotifyBusyChanged();
        var progress = new Progress<int>(value => Progress = value);
        try
        {
            var latest = await CheckAsync();
            if (latest == null)
                return;

            await UpdateAsync(latest, progress);
        }
        catch (Exception ex)
        {
            StatusText = string.Empty;
            Global.UIHub.Alert(I18N.T("settings.kernelUpdateFailed") + "\n" + ex.Message, DialogLevel.Error);
        }
        finally
        {
            Progress = 0;
            NotifyBusyChanged();
        }
    }

    /// <summary>删除（命令包装：确认框 + 失败弹 Alert）</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DeleteResourceAsync()
    {
        if (!await Global.UIHub.ConfirmAsync(I18N.TF("external.confirmDelete", _channel.DisplayName)))
            return;

        NotifyBusyChanged();
        try
        {
            await DeleteAsync();
        }
        catch (Exception ex)
        {
            Global.UIHub.Alert(I18N.T("external.updateFailed") + "\n" + ex.Message, DialogLevel.Error);
        }
        finally
        {
            NotifyBusyChanged();
        }
    }
}
