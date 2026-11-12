using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Controllers;
using Netch.Localization;
using Netch.UI.Hub;

namespace Netch.ViewModels;

/// <summary>关于页 VM（07§D）：检查更新（含 Beta 开关）/新版本横幅（复用 MainViewModel 的下载链）</summary>
[Fody.ConfigureAwait(true)]
public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _checkBetaUpdate;

    [ObservableProperty]
    private bool _newVersionVisible;

    [ObservableProperty]
    private string _newVersionText = "";

    [ObservableProperty]
    private string _statusText = "";

    public AboutViewModel()
    {
        CheckBetaUpdate = Global.Settings.CheckBetaUpdate;
        UpdateChecker.NewVersionFound += OnFound;
        UpdateChecker.NewVersionNotFound += (_, _) => StatusText = I18N.T("main.alreadyLatestVersion");
        UpdateChecker.NewVersionFoundFailed += (_, _) => StatusText = I18N.T("main.checkUpdateFailed");
    }

    private void OnFound(object? o, EventArgs? args)
    {
        NewVersionText = I18N.T("main.newVersion");
        NewVersionVisible = true;
        Global.Settings.CheckBetaUpdate = CheckBetaUpdate;
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        StatusText = I18N.T("common.testing");
        try
        {
            Global.Settings.CheckBetaUpdate = CheckBetaUpdate;
            await UpdateChecker.CheckAsync(CheckBetaUpdate);
        }
        finally
        {
            if (StatusText == I18N.T("common.testing"))
                StatusText = "";
        }
    }

    /// <summary>下载新版本（平移 MainViewModel.DownloadUpdateAsync 主体）</summary>
    [RelayCommand]
    private Task DownloadAsync()
    {
        return App.CurrentMain != null ? App.CurrentMain.NewVersionCommand.ExecuteAsync(null) : Task.CompletedTask;
    }
}
