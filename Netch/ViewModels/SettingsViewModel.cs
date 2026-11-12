using System.Windows;
using Application = System.Windows.Application;
using ThemeMode = Netch.UI.Theme.ThemeMode;
using Microsoft.VisualStudio.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Controllers;
using Netch.Localization;
using Netch.Models;
using Netch.Models.GitHubRelease;
using Netch.Services;
using Netch.UI;
using Netch.UI.Hub;
using Netch.UI.Theme;
using Netch.Utils;
using File = System.IO.File;
using Path = System.IO.Path;
using System.Linq;

namespace Netch.ViewModels;

/// <summary>
///     设置页 VM（05§A1/A2）：旧九标签页逐项 INPC 化绑定 Global.Settings，变更防抖 500ms 即时保存；
///     语言/主题变更即时生效；内核更新双通道（sing-box/Xray，共享 busy 互斥）；工具区命令平移旧 Options 菜单。
/// </summary>
[Fody.ConfigureAwait(true)]  // AsyncRelayCommand 完成回调须回 UI 线程刷新 CanExecute（Fody 全局 false 会把 continuation 丢线程池崩 UI）
public partial class SettingsViewModel : ObservableObject
{
    private static Setting S => Global.Settings;

    private readonly DispatcherTimer _saveDebounce;
    private bool _suppressSave;

    public SettingsViewModel()
    {
        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            Configuration.SaveAsync().Forget();
        };

        // 内核行引擎（R2 资源框架：互斥/状态写回/停-换-启在其内；R3 由 ResourceRowViewModel 统一转发）
        SingBoxVm = new KernelChannelViewModel(CoreUpdater.SingBox);
        XrayVm = new KernelChannelViewModel(CoreUpdater.Xray);

        // R4 徽章驱动：调度器静默检查结果反馈到行（页面实例经 _pages 缓存常驻，静态事件订阅与 AboutViewModel 同模式）
        ExternalResourceScheduler.ResourceUpdatable += OnSchedulerUpdatable;
        ExternalResourceScheduler.ResourceUpdated += OnSchedulerUpdated;

        ReloadResourceRows();

        SyncAccentSwatches();
    }

    /// <summary>调度器事件从线程池触发：刷徽章/行必须回 UI 线程（harness 无 Application 时跳过派发）</summary>
    private void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    private void OnSchedulerUpdatable(string key)
    {
        OnUi(() => RowsByKey.FirstOrDefault(r => r.Key == key)?.MarkUpdatable());
    }

    private void OnSchedulerUpdated(string key)
    {
        OnUi(() => _ = RowsByKey.FirstOrDefault(r => r.Key == key)?.ReloadAsync());
    }

    private IEnumerable<ResourceRowViewModel> RowsByKey => KernelRows.Concat(RuleSetRows).Concat(DataRows);

    private readonly KernelChannelViewModel SingBoxVm;
    private readonly KernelChannelViewModel XrayVm;

    /// <summary>当前类目索引（左导航绑定；九面板按此显隐）</summary>
    [ObservableProperty]
    private int _currentCategoryIndex;

    /// <summary>防抖保存（语言/主题等需要立即生效的走 ApplyXxx 内部先保存）</summary>
    private void ScheduleSave()
    {
        if (_suppressSave)
            return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    #region 常规 General

    public ushort Socks5LocalPort
    {
        get => S.Socks5LocalPort;
        set => SetSetting(S.Socks5LocalPort, value, v => S.Socks5LocalPort = v);
    }

    public ushort HTTPLocalPort
    {
        get => S.HTTPLocalPort;
        set => SetSetting(S.HTTPLocalPort, value, v => S.HTTPLocalPort = v);
    }

    /// <summary>本地监听地址联动（旧 AllowDevicesCheckBox：勾选=0.0.0.0，取消=127.0.0.1）</summary>
    public bool ShareLan
    {
        get => S.LocalAddress == "0.0.0.0";
        set => SetSetting(ShareLan, value, v => S.LocalAddress = v ? "0.0.0.0" : "127.0.0.1");
    }

    public bool ServerTCPing
    {
        get => S.ServerTCPing;
        set => SetSetting(S.ServerTCPing, value, v => S.ServerTCPing = v);
    }

    public int ProfileCount
    {
        get => S.ProfileCount;
        set
        {
            if (SetSetting(S.ProfileCount, value, v => S.ProfileCount = v))
                UIEvents.RaiseProfileCountChanged();
        }
    }

    public int DetectionTick
    {
        get => S.DetectionTick;
        set
        {
            if (SetSetting(S.DetectionTick, value, v => S.DetectionTick = v))
                DelayTestHelper.UpdateTick();
        }
    }

    public int StartedPingInterval
    {
        get => S.StartedPingInterval;
        set => SetSetting(S.StartedPingInterval, value, v => S.StartedPingInterval = v);
    }

    /// <summary>STUN 服务器完整地址 host:port，无端口按 3478（见 DnsUtils.SplitHostPort）</summary>
    public string STUNServer
    {
        get => S.STUN_Server;
        set
        {
            // IsEditable ComboBox 初始化/填充 ItemsSource 时会把 Text 清空写回：空值直接忽略，
            // 既防设置被清空，也让初值在源里保持有效（回显由 XAML 绑定顺序解决）
            if (string.IsNullOrWhiteSpace(value))
                return;

            SetSetting(S.STUN_Server, value, v => S.STUN_Server = v);
        }
    }

    /// <summary>公共 STUN 服务器候选（bin/stun.txt，host 或 host:port；原版 NetchX 设置页同源）。
    /// 读不到文件时给单项目前值，下拉退化仍可手输</summary>
    public IReadOnlyList<string> StunServerOptions { get; } = LoadStunServers();

    private static IReadOnlyList<string> LoadStunServers()
    {
        try
        {
            var path = Path.Combine(Global.NetchDir, "bin", "stun.txt");
            var list = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
            if (list.Count > 0)
                return list;
        }
        catch (Exception)
        {
            // 文件缺失时退化为仅当前值（仍可手输）
        }

        return [Global.Settings.STUN_Server];
    }

    public static IReadOnlyList<string> LanguageOptions { get; } = new[] { "System", "zh-CN", "en-US" };

    /// <summary>语言切换即时生效（05§B4）：双轨加载（新键制 + 旧词表供过渡期 Forms）+ 全 UI 刷新</summary>
    public string Language
    {
        get => S.Language;
        set
        {
            if (!SetSetting(S.Language, value, v => S.Language = v))
                return;

            I18N.SetLanguage(value);
            UIEvents.RaiseLanguageChanged();
            ScheduleSave();
        }
    }

    public static IReadOnlyList<string> ThemeOptions { get; } = new[] { ThemeMode.System.ToString(), ThemeMode.Light.ToString(), ThemeMode.Dark.ToString() };

    public string Theme
    {
        get => S.AppearanceSettings.Theme.ToString();
        set
        {
            if (!SetSetting(Theme, value, v => S.AppearanceSettings.Theme = Enum.Parse<ThemeMode>(v)))
                return;

            ThemeManager.Apply(S.AppearanceSettings.Theme);
            ScheduleSave();
        }
    }

    /// <summary>强调色（#RRGGBB；空=默认）</summary>
    public string Accent
    {
        get => S.AppearanceSettings.Accent;
        set
        {
            if (!SetSetting(S.AppearanceSettings.Accent, value, v => S.AppearanceSettings.Accent = v))
                return;

            ThemeManager.SetAccent(string.IsNullOrEmpty(value) ? null : value);
            SyncAccentSwatches();
            ScheduleSave();
        }
    }

    /// <summary>强调色预设色板（B6 反馈⑤：点选即换，首项空=恢复默认；hex 框保留作自定义兜底）</summary>
    public System.Collections.ObjectModel.ObservableCollection<AccentSwatchItem> AccentSwatches { get; } = new(
        new[]
        {
            "", "#c12b2f", "#da8b23", "#ffb900", "#0f7b0f", "#00b294", "#0099bc",
            "#0078d4", "#4f6bed", "#6b69d6", "#c239b3", "#e3008c", "#744da9", "#8a8886"
        }.Select(h => new AccentSwatchItem(h)));

    [RelayCommand]
    private void ApplyAccentSwatch(string? hex) => Accent = hex ?? "";

    private void SyncAccentSwatches()
    {
        foreach (var item in AccentSwatches)
            item.IsChecked = string.Equals(item.Hex, Accent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>恢复默认大小（05§A2：窗口尺寸重置并立即应用；连接观测页列宽一并恢复出厂并清记忆）</summary>
    [RelayCommand]
    private void RestoreWindowSize()
    {
        _suppressSave = true;
        S.MainFormWidth = 0;
        S.MainFormHeight = 0;
        _suppressSave = false;

        var window = Application.Current?.MainWindow;
        if (window != null)
            Views.MainWindow.ApplyDefaultWindowSize(window);

        Views.ConnectionsView.ResetColumnWidths();

        Configuration.SaveAsync().Forget();
    }

    #endregion

    #region 外部资源 External Resources（R3 方案 A：内核+规则集+数据文件+清单统一管理）

    public bool GroupAutoSelect
    {
        get => S.Kernel.GroupAutoSelect;
        set => SetSetting(S.Kernel.GroupAutoSelect, value, v => S.Kernel.GroupAutoSelect = v);
    }

    /// <summary>全局自动更新总开关（R4 调度器读取）</summary>
    public bool ExternalAutoUpdate
    {
        get => S.ExternalResources.AutoUpdateEnabled;
        set => SetSetting(S.ExternalResources.AutoUpdateEnabled, value, v => S.ExternalResources.AutoUpdateEnabled = v);
    }

    public int ExternalAutoUpdateInterval
    {
        get => S.ExternalResources.AutoUpdateIntervalHours;
        set => SetSetting(S.ExternalResources.AutoUpdateIntervalHours, value, v => S.ExternalResources.AutoUpdateIntervalHours = v);
    }

    [ObservableProperty]
    private string _mainUpdateStatus = "";

    /// <summary>检查主程序更新（Updater 7z 就地更新通道保留，此处只挂检查入口）</summary>
    [RelayCommand]
    private async Task CheckMainUpdateAsync()
    {
        MainUpdateStatus = I18N.T("common.testing");
        try
        {
            await UpdateChecker.CheckAsync(S.CheckBetaUpdate);
            var latest = UpdateChecker.LatestRelease;
            MainUpdateStatus = latest == null
                ? I18N.T("main.checkUpdateFailed")
                : VersionUtil.CompareVersion(latest.tag_name, UpdateChecker.Version) > 0
                    ? I18N.TF("settings.newKernelVersionFound0", latest.tag_name)
                    : I18N.T("main.alreadyLatestVersion");
        }
        catch (Exception e)
        {
            Log.Warning(e, "Check main update from external resources page failed");
            MainUpdateStatus = I18N.T("main.checkUpdateFailed");
        }
    }

    /// <summary>内核区行（sing-box/Xray 可更新可删 + Redirector 只读随主程序发行）</summary>
    public System.Collections.ObjectModel.ObservableCollection<ResourceRowViewModel> KernelRows { get; } = new();

    /// <summary>规则集区行（内置 ×2 + 用户自定义动态项）</summary>
    public System.Collections.ObjectModel.ObservableCollection<ResourceRowViewModel> RuleSetRows { get; } = new();

    /// <summary>数据文件区行（IP 归属地库 + STUN 清单）</summary>
    public System.Collections.ObjectModel.ObservableCollection<ResourceRowViewModel> DataRows { get; } = new();

    private void ReloadResourceRows()
    {
        KernelRows.Clear();
        RuleSetRows.Clear();
        DataRows.Clear();
        foreach (var resource in ExternalResourceService.All)
        {
            var row = new ResourceRowViewModel(resource, resource.Key switch
            {
                "kernel.singbox" => SingBoxVm,
                "kernel.xray" => XrayVm,
                _ => null
            });
            row.DeleteHostHandler = DeleteCustomRuleSetAsync;
            row.EditHostHandler = EditCustomRuleSetAsync;

            switch (resource.Kind)
            {
                case ExternalResourceKind.Kernel:
                    KernelRows.Add(row);
                    break;
                case ExternalResourceKind.RuleSet:
                    RuleSetRows.Add(row);
                    break;
                default:
                    DataRows.Add(row);
                    break;
            }
        }

        _ = ReloadRowsAsync();
    }

    private async Task ReloadRowsAsync()
    {
        foreach (var row in KernelRows.Concat(RuleSetRows).Concat(DataRows))
            await row.ReloadAsync();
    }

    /// <summary>语言/资源状态外部变化后刷新行文案</summary>
    public Task RefreshRowsAsync() => ReloadRowsAsync();

    /// <summary>立即全部更新：串行跑全部可更新且未禁用的行（BusyLock 保证与调度器互斥）</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        foreach (var row in KernelRows.Concat(RuleSetRows).Concat(DataRows).Where(r =>
                     r.Resource.Caps.HasFlag(ExternalResourceCaps.CanUpdate) &&
                     !ExternalResourceService.IsDisabled(r.Resource)))
        {
            await row.UpdateCommand.ExecuteAsync(null);
        }
    }

    public bool ExternalResourcesViaProxy
    {
        get => S.ExternalResources.ViaProxy;
        set => SetSetting(S.ExternalResources.ViaProxy, value, v => S.ExternalResources.ViaProxy = v);
    }

    public bool UseBuiltInRuleSets
    {
        get => S.UseBuiltInRuleSets;
        set => SetSetting(S.UseBuiltInRuleSets, value, v => S.UseBuiltInRuleSets = v);
    }

    /// <summary>自定义规则集编辑（行转发宿主弹 RuleSetDialog；内置/内核行无此能力）</summary>
    private async Task EditCustomRuleSetAsync(ResourceRowViewModel row)
    {
        var tag = row.Key["ruleset.custom.".Length..];
        var config = Global.Settings.RuleSets.FirstOrDefault(r => r.Tag == tag);
        if (config == null)
            return;

        var dialog = Views.RuleSetDialog.Edit(config);
        dialog.Owner = Application.Current?.MainWindow;
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;

        try
        {
            await RuleSetReferenceService.RenameRuleSetAsync(tag, dialog.Result);
            ReloadResourceRows();
        }
        catch (Exception e)
        {
            ReloadResourceRows();
            Global.UIHub.Alert(e.Message, DialogLevel.Error);
        }
    }

    /// <summary>自定义规则集删除：删配置 + 连带缓存快照进 .trash（R1 拍板：语义干净，配置没了缓存也不留）</summary>
    private async Task DeleteCustomRuleSetAsync(ResourceRowViewModel row)
    {
        var tag = row.Key["ruleset.custom.".Length..];
        var config = Global.Settings.RuleSets.FirstOrDefault(r => r.Tag == tag);
        if (config == null)
            return;

        var referencing = RuleSetReferenceService.ReferencingModes(tag);
        if (referencing.Count > 0)
        {
            if (!await Global.UIHub.ConfirmAsync(I18N.TF("rulesets.deleteReferenced", tag,
                    string.Join(", ", referencing.Select(m => m.i18NRemark)))))
                return;
        }
        else if (!await Global.UIHub.ConfirmAsync(I18N.TF("external.confirmDelete", config.Tag)))
            return;

        // 先捕获资源引用，删配置后动态注册行会消失（RE-2§3.6）
        var cacheResource = ExternalResourceService.Find(row.Key);
        Global.Settings.RuleSets.Remove(config);

        // 缓存快照连带进 .trash（可能从未下载过，不存在则跳过）
        if (cacheResource != null && ExternalResourceService.Exists(cacheResource))
            ExternalResourceService.MoveToTrash(cacheResource);

        ReloadResourceRows();
        await Configuration.SaveAsync();
    }

    [RelayCommand]
    private void OpenBinFolder()
    {
        Utils.Utils.Open(Path.Combine(Global.NetchDir, "bin"));
    }

    #endregion

    #region 可靠性 Reliability

    public bool AutoRestore
    {
        get => S.Network.AutoRestore;
        set => SetSetting(S.Network.AutoRestore, value, v => S.Network.AutoRestore = v);
    }

    public bool KernelRestart
    {
        get => S.Network.KernelAutoRestart;
        set => SetSetting(S.Network.KernelAutoRestart, value, v => S.Network.KernelAutoRestart = v);
    }

    public int DebounceSeconds
    {
        get => S.Network.DebounceSeconds;
        set => SetSetting(S.Network.DebounceSeconds, value, v => S.Network.DebounceSeconds = v);
    }

    public bool SubscriptionUseProxy
    {
        get => S.SubscriptionUseProxy;
        set => SetSetting(S.SubscriptionUseProxy, value, v => S.SubscriptionUseProxy = v);
    }

    #endregion

    #region 进程模式 Process Mode（旧 NF 页）

    public bool FilterTCP
    {
        get => S.Redirector.FilterTCP;
        set => SetSetting(S.Redirector.FilterTCP, value, v => S.Redirector.FilterTCP = v);
    }

    public bool FilterUDP
    {
        get => S.Redirector.FilterUDP;
        set => SetSetting(S.Redirector.FilterUDP, value, v => S.Redirector.FilterUDP = v);
    }

    public bool FilterICMP
    {
        get => S.Redirector.FilterICMP;
        set => SetSetting(S.Redirector.FilterICMP, value, v => S.Redirector.FilterICMP = v);
    }

    public bool FilterDNS
    {
        get => S.Redirector.FilterDNS;
        set => SetSetting(S.Redirector.FilterDNS, value, v => S.Redirector.FilterDNS = v);
    }

    public bool HandleProcDNS
    {
        get => S.Redirector.HandleOnlyDNS;
        set => SetSetting(S.Redirector.HandleOnlyDNS, value, v => S.Redirector.HandleOnlyDNS = v);
    }

    public bool DNSProxy
    {
        get => S.Redirector.DNSProxy;
        set => SetSetting(S.Redirector.DNSProxy, value, v => S.Redirector.DNSProxy = v);
    }

    public int ICMPDelay
    {
        get => S.Redirector.ICMPDelay;
        set => SetSetting(S.Redirector.ICMPDelay, value, v => S.Redirector.ICMPDelay = v);
    }

    public bool VerboseLog
    {
        get => S.Redirector.VerboseLog;
        set => SetSetting(S.Redirector.VerboseLog, value, v => S.Redirector.VerboseLog = v);
    }

    #endregion

    #region TUN

    public static IReadOnlyList<string> StackOptions { get; } = new[] { "system", "gvisor", "mixed" };

    public string TunStack
    {
        get => S.TUNTAP.Stack;
        set => SetSetting(S.TUNTAP.Stack, value, v => S.TUNTAP.Stack = v);
    }

    public bool StrictRoute
    {
        get => S.TUNTAP.StrictRoute;
        set => SetSetting(S.TUNTAP.StrictRoute, value, v => S.TUNTAP.StrictRoute = v);
    }

    /// <summary>全局绕过 IP 编辑（07§D GlobalBypassIPDialog）</summary>
    [RelayCommand]
    private void EditBypassIps()
    {
        var dialog = new Views.BypassIPDialog { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
    }

    #endregion

    #region V2Ray

    public bool XrayCone
    {
        get => S.V2RayConfig.XrayCone;
        set => SetSetting(S.V2RayConfig.XrayCone, value, v => S.V2RayConfig.XrayCone = v);
    }

    public bool TLSAllowInsecure
    {
        get => S.V2RayConfig.AllowInsecure;
        set => SetSetting(S.V2RayConfig.AllowInsecure, value, v => S.V2RayConfig.AllowInsecure = v);
    }

    public bool UseMux
    {
        get => S.V2RayConfig.UseMux;
        set => SetSetting(S.V2RayConfig.UseMux, value, v => S.V2RayConfig.UseMux = v);
    }

    public bool TCPFastOpen
    {
        get => S.V2RayConfig.TCPFastOpen;
        set => SetSetting(S.V2RayConfig.TCPFastOpen, value, v => S.V2RayConfig.TCPFastOpen = v);
    }

    public int KcpMtu
    {
        get => S.V2RayConfig.KcpConfig.mtu;
        set => SetSetting(S.V2RayConfig.KcpConfig.mtu, value, v => S.V2RayConfig.KcpConfig.mtu = v);
    }

    public int KcpTti
    {
        get => S.V2RayConfig.KcpConfig.tti;
        set => SetSetting(S.V2RayConfig.KcpConfig.tti, value, v => S.V2RayConfig.KcpConfig.tti = v);
    }

    public int KcpUplink
    {
        get => S.V2RayConfig.KcpConfig.uplinkCapacity;
        set => SetSetting(S.V2RayConfig.KcpConfig.uplinkCapacity, value, v => S.V2RayConfig.KcpConfig.uplinkCapacity = v);
    }

    public int KcpDownlink
    {
        get => S.V2RayConfig.KcpConfig.downlinkCapacity;
        set => SetSetting(S.V2RayConfig.KcpConfig.downlinkCapacity, value, v => S.V2RayConfig.KcpConfig.downlinkCapacity = v);
    }

    public int KcpRead
    {
        get => S.V2RayConfig.KcpConfig.readBufferSize;
        set => SetSetting(S.V2RayConfig.KcpConfig.readBufferSize, value, v => S.V2RayConfig.KcpConfig.readBufferSize = v);
    }

    public int KcpWrite
    {
        get => S.V2RayConfig.KcpConfig.writeBufferSize;
        set => SetSetting(S.V2RayConfig.KcpConfig.writeBufferSize, value, v => S.V2RayConfig.KcpConfig.writeBufferSize = v);
    }

    public bool KcpCongestion
    {
        get => S.V2RayConfig.KcpConfig.congestion;
        set => SetSetting(S.V2RayConfig.KcpConfig.congestion, value, v => S.V2RayConfig.KcpConfig.congestion = v);
    }

    #endregion

    #region DNS（旧 AioDNS 页）

    public static IReadOnlyList<string> DnsPolicyOptions { get; } =
        new[] { DnsPolicies.Auto, DnsPolicies.ProxyAll, DnsPolicies.DirectAll, DnsPolicies.Custom };

    public string DnsPolicy
    {
        get => S.DNS.Policy;
        set => SetSetting(S.DNS.Policy, value, v => S.DNS.Policy = v);
    }

    public string DnsChina
    {
        get => S.DNS.ChinaDns;
        set => SetSetting(S.DNS.ChinaDns, value, v => S.DNS.ChinaDns = v);
    }

    public string DnsRemote
    {
        get => S.DNS.RemoteDns;
        set => SetSetting(S.DNS.RemoteDns, value, v => S.DNS.RemoteDns = v);
    }

    #endregion

    #region 其他 Others

    public bool ExitWhenClosed
    {
        get => S.ExitWhenClosed;
        set => SetSetting(S.ExitWhenClosed, value, v => S.ExitWhenClosed = v);
    }

    public bool StopWhenExited
    {
        get => S.StopWhenExited;
        set => SetSetting(S.StopWhenExited, value, v => S.StopWhenExited = v);
    }

    public bool StartWhenOpened
    {
        get => S.StartWhenOpened;
        set => SetSetting(S.StartWhenOpened, value, v => S.StartWhenOpened = v);
    }

    public bool MinimizeWhenStarted
    {
        get => S.MinimizeWhenStarted;
        set => SetSetting(S.MinimizeWhenStarted, value, v => S.MinimizeWhenStarted = v);
    }

    public bool RunAtStartup
    {
        get => S.RunAtStartup;
        set
        {
            if (!SetSetting(S.RunAtStartup, value, v => S.RunAtStartup = v))
                return;

            // 开机自启立即写入注册表（旧 SettingForm 同步语义）
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (value)
                    key?.SetValue(UpdateChecker.Name, $"\"{Global.NetchExecutable}\"");
                else
                    key?.DeleteValue(UpdateChecker.Name, false);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Set RunAtStartup failed");
            }
        }
    }

    public bool CheckUpdateWhenOpened
    {
        get => S.CheckUpdateWhenOpened;
        set => SetSetting(S.CheckUpdateWhenOpened, value, v => S.CheckUpdateWhenOpened = v);
    }

    public bool CheckBetaUpdate
    {
        get => S.CheckBetaUpdate;
        set => SetSetting(S.CheckBetaUpdate, value, v => S.CheckBetaUpdate = v);
    }

    public bool NoSupportDialog
    {
        get => S.NoSupportDialog;
        set => SetSetting(S.NoSupportDialog, value, v => S.NoSupportDialog = v);
    }

    public int RequestTimeout
    {
        get => S.RequestTimeout;
        set => SetSetting(S.RequestTimeout, value, v => S.RequestTimeout = v);
    }

    #region 工具区（旧 Options 菜单迁入）

    [RelayCommand]
    private void OpenDirectory()
    {
        Utils.Utils.Open(Global.NetchDir);
    }

    [RelayCommand]
    private void ShowHideConsole()
    {
        // 手写 P/Invoke：CsWin32 生成物在 WPF markup 编译的 wpftmp 工程不可见
        const int GWL_STYLE = -16;
        const long WS_VISIBLE = 0x10000000;
        const int SW_HIDE = 0;
        const int SW_SHOWNOACTIVATE = 4;

        var style = GetWindowLongPtr(Program.ConsoleHwnd, GWL_STYLE);
        var visible = (style & WS_VISIBLE) != 0;
        ShowWindow(Program.ConsoleHwnd, visible ? SW_HIDE : SW_SHOWNOACTIVATE);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern long GetWindowLongPtr(nint hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [RelayCommand]
    private Task CleanDnsCacheAsync()
    {
        return Task.Run(() =>
        {
            NativeMethods.RefreshDNSCache();
            DnsUtils.ClearCache();
        });
    }

    [RelayCommand]
    private async Task UninstallNfServiceAsync()
    {
        Global.UIHub.StatusText(I18N.TF("main.uninstalling0", "NF Service"));
        try
        {
            if (await Task.Run(NFController.UninstallDriver))
                Global.UIHub.NotifyTip(I18N.TF("main.0HasBeenUninstalled", "NF Service"));
            else
                Global.UIHub.Alert(I18N.T("controller.uninstallFailed"), DialogLevel.Error);
        }
        finally
        {
            Global.UIHub.StatusText(null);
        }
    }

    [RelayCommand]
    private void RemoveFirewallRules()
    {
        Firewall.RemoveNetchFwRules();
        Global.UIHub.NotifyTip(I18N.T("settings.tools.firewallRemoved"));
    }

    [RelayCommand]
    private Task BackupSettingsAsync()
    {
        return BackupService.BackupAsync(BackupService.BackupType.Settings);
    }

    [RelayCommand]
    private Task BackupFullAsync()
    {
        return BackupService.BackupAsync(BackupService.BackupType.Full);
    }

    [RelayCommand]
    private Task RestoreBackupAsync()
    {
        return BackupService.RestoreAsync();
    }

    #endregion

    #endregion

    /// <summary>写模型属性 + 通知 + 防抖保存的统一通道（属性名由 CallerMemberName 提供）</summary>
    private bool SetSetting<T>(T oldValue, T newValue, Action<T> write,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
            return false;

        write(newValue);
        OnPropertyChanged(propertyName);
        ScheduleSave();
        return true;
    }
}

/// <summary>强调色色板项（Hex 空 = 恢复默认强调色）</summary>
public partial class AccentSwatchItem : ObservableObject
{
    public string Hex { get; }

    [ObservableProperty]
    private bool _isChecked;

    public string ToolTipText => string.IsNullOrEmpty(Hex)
        ? I18N.T("settings.general.accentDefault")
        : Hex;

    public AccentSwatchItem(string hex) => Hex = hex;
}
