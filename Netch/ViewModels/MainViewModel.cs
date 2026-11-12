using System.ComponentModel;
using Microsoft.VisualStudio.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Netch.Controllers;
using Netch.Enums;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;
using Netch.Services;
using Netch.UI.Hub;
using Netch.Utils;
using Netch.Views;

namespace Netch.ViewModels;

/// <summary>
///     主窗口编排 VM（04）：启停状态机（平移旧 MainForm.State setter 全语义）、
///     内核看门狗、网络感知自愈、电源事件、状态栏 1Hz 刷新、更新检查/下载、退出语义。
///     状态变更统一走本类 State setter 并 RaiseStateChanged（业务层经 hub.State / UIEvents 感知）。
///     重子系统（计时器/网络感知/启动钩子）仅在 OnMainWindowLoaded 启用，harness 驱动 VM 时不激活。
/// </summary>
[Fody.ConfigureAwait(true)]  // AsyncRelayCommand 完成回调须回 UI 线程刷新 CanExecute（Fody 全局 false 会把 continuation 丢线程池崩 UI）
public partial class MainViewModel : ObservableObject
{
    private readonly NetworkWatcher _networkWatcher = new();
    private int _kernelRestartCount;
    private DateTime _lastKernelDeath = DateTime.MinValue;
    private CancellationTokenSource? _discoveryNatCts;
    private CancellationTokenSource? _httpConnectCts;
    private DispatcherTimer? _statusTimer;
    private ulong _lastEtwReceived;
    private bool _etwActive;
    private string? _customStatusText;
    private bool _resumeFlag;
    private bool _isFirstCloseWindow = true;
    private int _restoring;

    /// <summary>启停全流程互斥（低危⑭，2026-11-04 审查）：StopCoreAsync 的「等内核锁→放→再取锁改 State」
    /// 窗口内并发 Start 可能被误停；Start/Stop 串行化后 Stop 持锁期间 Start 只能排队，落地时 State 已一致</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public HomeViewModel Home { get; }

    /// <summary>当前状态（唯一事实源；变更时 RaiseStateChanged）</summary>
    public State State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;

            _state = value;
            OnStateChanged(value);
        }
    }

    private State _state = State.Waiting;

    public AsyncRelayCommand StartStopCommand { get; }
    public AsyncRelayCommand RefreshNatCommand { get; }
    public AsyncRelayCommand RefreshHttpCommand { get; }
    public AsyncRelayCommand NewVersionCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }

    public MainViewModel()
    {
        Home = new HomeViewModel(() => State) { Main = this };

        StartStopCommand = new AsyncRelayCommand(StartStopAsync, () => CanStartStop());
        RefreshNatCommand = new AsyncRelayCommand(DiscoveryNatTypeAsync, () => State == State.Started);
        RefreshHttpCommand = new AsyncRelayCommand(HttpConnectAsync, () => State == State.Started);
        NewVersionCommand = new AsyncRelayCommand(DownloadUpdateAsync);
        ExitCommand = new AsyncRelayCommand(() => ExitAsync());

        UIEvents.KernelExited += OnKernelExited;
        UIEvents.LanguageChanged += OnLanguageChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // F-4a（2026-11-04 审查）：内核更新的停/启经状态机——此前 CoreUpdater 直调 MainController，
        // State 全程不知情，更新失败后状态栏仍显示「运行中」；经 VM 停启附带任务也全跑
        CoreUpdater.StopRunner = _ => StopAsync();
        CoreUpdater.RestartRunner = _ => StartStopAsync();

        // 初始态推导：State 初始值即 Waiting，setter 同值去重不会触发 OnStateChanged，
        // 状态栏/内核指示的初值需手动推导（旧 WinForms 由 InitializeComponent 画初值）
        RefreshStatusDisplay();
        RefreshStatus();
    }

    /// <summary>状态变化的全部连带（平移旧 MainForm.State setter，04§4.3 状态机表）</summary>
    private void OnStateChanged(State value)
    {
        UIEvents.RaiseStateChanged(value);

        DelayTestHelper.Enabled = IsWaiting(value);

        RefreshStartStopText();

        _customStatusText = null;
        RefreshStatusDisplay();

        switch (value)
        {
            case State.Stopped:
                _etwActive = false;
                Bandwidth.Stop();
                ClashTrafficMonitor.Stop();
                break;
        }

        // CanExecute 绑定 State 的命令统一刷新（订阅/服务器编辑等在 Home VM 内同理）
        CommandManager.InvalidateRequerySuggested();
        Home.NotifyStateChanged();
        RefreshStatus();
    }

    #region 启停（平移 ControlButton_Click / StopCoreAsync）

    private bool CanStartStop()
    {
        // Stopped 也允许点击：停止后可直接再启动（按钮文案 Stopped→main.start，IsWaiting 含 Stopped，语义齐备）
        return State is State.Waiting or State.Started or State.Stopped or State.Degraded;
    }

    private void RefreshStartStopText()
    {
        StartStopText = State switch
        {
            State.Waiting or State.Stopped => I18N.T("main.start"),
            State.Started or State.Degraded => I18N.T("main.stop"),
            State.Stopping => I18N.T("main.stopping"),
            _ => I18N.T("main.starting")
        };
    }

    /// <summary>语言切换：主按钮文案与状态栏为命令式赋值文本，随 LanguageChanged 重赋（05§B4）；
    /// 瞬态提示（如启动失败）key 未留存，回退为状态默认文案</summary>
    private void OnLanguageChanged()
    {
        RefreshStartStopText();
        _customStatusText = null;
        RefreshStatusDisplay();
    }

    private async Task StartStopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StartStopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartStopCoreAsync()
    {
        if (State == State.Degraded)
        {
            // 等待网络期间用户点击 = 明确意图：先完整停止，再按正常流程重启
            await StopCoreAsync();
        }
        else if (!IsWaiting())
        {
            await StopCoreAsync();
            return;
        }

        Configuration.SaveAsync().Forget();

        // 服务器、模式 需选择
        var server = Home.SelectedServer?.Model;
        if (server == null)
        {
            Global.UIHub.Alert(I18N.T("home.selectServerFirst"));
            return;
        }

        var mode = Home.SelectedMode;
        if (mode == null)
        {
            Global.UIHub.Alert(I18N.T("home.selectModeFirst"));
            return;
        }

        State = State.Starting;

        try
        {
            await MainController.StartAsync(server, mode);
        }
        catch (Exception exception)
        {
            State = State.Stopped;
            SetCustomStatusText(I18N.T("main.startFailed"));
            Global.UIHub.Alert(exception.Message, DialogLevel.Error);
            return;
        }

        State = State.Started;

        RunPostStartTasks();

        if (Global.Settings.MinimizeWhenStarted)
            MinimizeToTray(false);
    }

    /// <summary>Started 之后的附带任务（F-5，2026-11-04 审查）：流量统计/NAT/HTTP 探测/周期测速。
    /// 用户启动、看门狗重启、断网恢复重建共用——此前恢复路径只重建内核不拉附带任务，
    /// 表现为恢复后周期测速/NAT 显示/流量全部停摆</summary>
    private void RunPostStartTasks()
    {
        // 流量统计：sing-box 内核（进程/TUN 同源）经 Clash API 按出站分账（状态栏已使用=仅代理，流速拆代理/直连）；
        // 其余内核无此接口，回退 ETW 统计内核进程总流量（含直连）。两路均自防重入
        if (MainController.ServerController is SingboxController { ClashApiPort: not null })
            Task.Run(ClashTrafficMonitor.RunAsync).Forget();
        else
        {
            _etwActive = true;
            Bandwidth.NetTraffic();
        }

        _ = DiscoveryNatTypeAsync();
        _ = HttpConnectAsync();

        _ = StartedPingAsync();
    }

    /// <summary>启动后周期测速（平移 StartedPingAsync；多节点实例成员由 Clash API 维护，跳过防覆盖）</summary>
    private async Task StartedPingAsync()
    {
        while (State == State.Started)
        {
            if (Global.Settings.StartedPingInterval >= 0)
            {
                var server = Home.SelectedServer?.Model;
                if (server != null && !MainController.IsMultiNodeMember(server))
                    await server.PingAsync();

                await Task.Delay(Global.Settings.StartedPingInterval * 1000);
            }
            else
            {
                await Task.Delay(5000);
            }
        }
    }

    public async Task StopAsync()
    {
        if (IsWaiting())
            return;

        await _lifecycleGate.WaitAsync();
        try
        {
            if (IsWaiting())
                return;

            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>激活 Profile（04§4.2 平移 ProfileButton_Click 尾部）：先停（Waiting 短路）再按正常流程启动</summary>
    public async Task ActivateProfileAsync(Server server, Mode mode)
    {
        if (!IsWaiting())
            await StopAsync();

        await StartStopAsync();
    }

    private async Task StopCoreAsync(bool keepDegraded = false)
    {
        State = State.Stopping;
        if (_discoveryNatCts != null) await _discoveryNatCts.CancelAsync();
        if (_httpConnectCts != null) await _httpConnectCts.CancelAsync();
        await MainController.StopAsync();
        State = keepDegraded ? State.Degraded : State.Stopped;
    }

    public bool IsWaiting()
    {
        return IsWaiting(State);
    }

    /// <summary>Waiting/Stopped 才算「未运行」；Degraded（等待网络）不算，避免被普通 Start 流程误判</summary>
    public static bool IsWaiting(State state)
    {
        return state is State.Waiting or State.Stopped;
    }

    #endregion

    #region 导航壳（04§3：NavigationView 页缓存单例，VM 层切换 CurrentPage）

    private readonly Dictionary<string, object> _pages = new();
    private ConnectionsViewModel? _connectionsVm;
    private RuleSetsViewModel? _rulesetsVm;

    [ObservableProperty]
    private object _currentPage = null!;

    [ObservableProperty]
    private string _startStopText = I18N.T("main.start");

    /// <summary>导航页 key（XAML NavigationViewItem.TargetPageTag 对齐）</summary>
    public const string PageHome = "home", PageConnections = "connections",
        PageSubscriptions = "subscriptions", PageRuleSets = "rulesets", PageSettings = "settings", PageLog = "log", PageAbout = "about";

    [RelayCommand]
    private void Navigate(string? tag)
    {
        if (tag == null)
            return;

        CurrentPage = GetPage(tag);
    }

    /// <summary>页面缓存：导航切换只切实例不销毁（连接页历史不丢的语义基础，07§C3）</summary>
    private object GetPage(string tag)
    {
        if (_pages.TryGetValue(tag, out var page))
            return page;

        page = tag switch
        {
            PageHome => new Views.HomeView { DataContext = Home },
            PageConnections => new Views.ConnectionsView { DataContext = _connectionsVm ??= new ConnectionsViewModel() },
            PageSubscriptions => new Views.SubscriptionsView { DataContext = new SubscriptionsViewModel() },
            PageRuleSets => new Views.RuleSetsView { DataContext = _rulesetsVm ??= new RuleSetsViewModel() },
            PageSettings => new Views.SettingsView { DataContext = new SettingsViewModel() },
            PageLog => new Views.LogView(),
            PageAbout => new Views.AboutView { DataContext = new AboutViewModel() },
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
        _pages[tag] = page;
        return page;
    }

    /// <summary>初始页=主页（App 在 Show 前调用）</summary>
    public void InitializeNavigation()
    {
        CurrentPage = GetPage(PageHome);
    }

    #endregion

    #region 状态栏（04§4.4：内容项依序，弹簧殿后；1Hz 读静态字段，UI 线程零 Clash HTTP 调用）

    [ObservableProperty]
    private string _statusDisplay = string.Empty;

    [ObservableProperty]
    private string _statusPortTooltip = string.Empty;

    [ObservableProperty]
    private string _usedBandwidth = string.Empty;

    [ObservableProperty]
    private string _usedBandwidthTooltip = string.Empty;

    [ObservableProperty]
    private string _proxySpeed = string.Empty;

    [ObservableProperty]
    private string _directSpeed = string.Empty;

    [ObservableProperty]
    private string _httpDisplay = string.Empty;

    [ObservableProperty]
    private string _natDisplay = string.Empty;

    [ObservableProperty]
    private int _natLevel = -1;

    [ObservableProperty]
    private string _kernelIndicator = string.Empty;

    [ObservableProperty]
    private bool _kernelHealthy;

    [ObservableProperty]
    private bool _newVersionVisible;

    [ObservableProperty]
    private string _newVersionText = string.Empty;

    /// <summary>流量区可见（非等待态且统计源在跑；ETW 回退隐藏直连列——语义沿旧 SetUploadSpeedVisible）</summary>
    public bool TrafficVisible => !IsWaiting(State) && !string.IsNullOrEmpty(UsedBandwidth);

    public bool DirectSpeedVisible => ClashTrafficMonitor.Running;

    /// <summary>状态栏自定义文本（hub.StatusText；null=按 State 恢复默认，语义同旧 MainForm.StatusText）</summary>
    public void SetCustomStatusText(string? text)
    {
        _customStatusText = text;
        RefreshStatusDisplay();
    }

    private void RefreshStatusDisplay()
    {
        var text = _customStatusText ?? I18N.T(StateExtension.GetStatusKey(State));
        StatusDisplay = $"{I18N.T("common.status") + I18N.T("common.colon")}{text}";
        StatusPortTooltip = State == State.Started ? StatusPortInfoText.Value.Trim() : string.Empty;
    }

    /// <summary>1Hz 状态栏刷新（计时器/harness 均直调；只读静态字段，UI 线程零 HTTP 调用）</summary>
    public void RefreshStatus()
    {
        if (ClashTrafficMonitor.Running)
        {
            var speeds = ClashTrafficMonitor.Speeds;

            UsedBandwidth = $"{I18N.T("common.used") + I18N.T("common.colon")}{Bandwidth.ComputeShort(ClashTrafficMonitor.ProxyUpTotal + ClashTrafficMonitor.ProxyDownTotal)}";
            UsedBandwidthTooltip =
                $"{I18N.T("common.used") + I18N.T("common.colon")}{Bandwidth.Compute(ClashTrafficMonitor.ProxyUpTotal + ClashTrafficMonitor.ProxyDownTotal)}\n" +
                $"{I18N.T("common.proxy") + I18N.T("common.colon")}↑{Bandwidth.Compute(ClashTrafficMonitor.ProxyUpTotal)} ↓{Bandwidth.Compute(ClashTrafficMonitor.ProxyDownTotal)}\n" +
                $"{I18N.T("common.direct") + I18N.T("common.colon")}↑{Bandwidth.Compute(ClashTrafficMonitor.DirectUpTotal)} ↓{Bandwidth.Compute(ClashTrafficMonitor.DirectDownTotal)}";

            ProxySpeed = $"{I18N.T("common.proxy")}↑{Bandwidth.ComputeShort(speeds.ProxyUp)}/s ↓{Bandwidth.ComputeShort(speeds.ProxyDown)}/s";
            DirectSpeed = $"{I18N.T("common.direct")}↑{Bandwidth.ComputeShort(speeds.DirectUp)}/s ↓{Bandwidth.ComputeShort(speeds.DirectDown)}/s";
        }
        else if (_etwActive && !IsWaiting(State) && State != State.Stopping)
        {
            // ETW 回退（非 sing-box 内核）：总流量口径，含直连
            UsedBandwidth = $"{I18N.T("common.used") + I18N.T("common.colon")}{Bandwidth.ComputeShort(Bandwidth.received)}";
            UsedBandwidthTooltip = $"{I18N.T("common.used") + I18N.T("common.colon")}{Bandwidth.Compute(Bandwidth.received)} (ETW total, direct included)";
            ProxySpeed = $"↑↓ {Bandwidth.ComputeShort(Bandwidth.received - _lastEtwReceived)}/s";
            DirectSpeed = string.Empty;
            _lastEtwReceived = Bandwidth.received;
        }
        else
        {
            UsedBandwidth = string.Empty;
            UsedBandwidthTooltip = string.Empty;
            ProxySpeed = string.Empty;
            DirectSpeed = string.Empty;
            _lastEtwReceived = 0;
        }

        OnPropertyChanged(nameof(TrafficVisible));
        OnPropertyChanged(nameof(DirectSpeedVisible));

        // 内核指示（顶栏）：运行中显示控制器名 + 健康点
        KernelIndicator = MainController.ServerController?.Name ?? string.Empty;
        KernelHealthy = State is State.Started or State.Degraded;
    }

    /// <summary>主窗 Loaded 后启动 1Hz 计时器（harness 不走此路径，直调 RefreshStatus）</summary>
    public void StartStatusTimer()
    {
        if (_statusTimer != null)
            return;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += (_, _) =>
        {
            RefreshStatus();
            Home.RefreshDelays();
        };
        _statusTimer.Start();
    }

    #endregion

    #region NAT / HTTP 检测（平移 MainForm.DiscoveryNatTypeAsync/HttpConnectAsync）

    private async Task DiscoveryNatTypeAsync()
    {
        NatDisplay = $"NAT{I18N.T("common.colon")}{I18N.T("main.nat.testing")}";

        _discoveryNatCts = new CancellationTokenSource();

        try
        {
            var res = await MainController.DiscoveryNatTypeAsync(_discoveryNatCts.Token);
            if (_discoveryNatCts.IsCancellationRequested)
                return;

            if (!string.IsNullOrEmpty(res.PublicEnd))
            {
                var country = await Utils.Utils.GetCityCodeAsync(res.PublicEnd);

                NatDisplay = string.IsNullOrEmpty(country)
                    ? $"NAT{I18N.T("common.colon")}{res.Result}"
                    : $"NAT{I18N.T("common.colon")}{res.Result} [{country}]";

                NatLevel = int.TryParse(res.Result, out var natType) ? natType : -1;
            }
            else
            {
                NatDisplay = $"NAT{I18N.T("common.colon") + I18N.T("main.nat.failed")}";
                NatLevel = -1;
            }
        }
        catch (Exception)
        {
            NatDisplay = $"NAT{I18N.T("common.colon") + I18N.T("main.nat.failed")}";
            NatLevel = -1;
        }
        finally
        {
            _discoveryNatCts.Dispose();
            _discoveryNatCts = null;
        }
    }

    private async Task HttpConnectAsync()
    {
        _httpConnectCts = new CancellationTokenSource();

        try
        {
            var res = await MainController.HttpConnectAsync(_httpConnectCts.Token);
            if (_httpConnectCts.IsCancellationRequested)
                return;

            HttpDisplay = res != null ? $"HTTP{I18N.T("common.colon")}{res}ms" : $"HTTP{I18N.T("common.colon") + I18N.T("common.timeout")}";
        }
        finally
        {
            _httpConnectCts.Dispose();
            _httpConnectCts = null;
        }
    }

    #endregion

    #region 内核看门狗（平移 HandleKernelExitedAsync）

    private async void OnKernelExited(Guard guard)
    {
        try
        {
            await OnUiThreadAsync(() => OnKernelExitedAsync(guard));
        }
        catch (Exception e)
        {
            Log.Error(e, "Kernel watchdog failed");
        }
    }

    /// <summary>
    ///     线程池/系统消息线程入口统一调度到 UI 线程（C-3，2026-11-04 审查）：State 的 check-then-set
    ///     非原子，事件线程直写会与用户操作交错（断网事件覆盖刚点的 Starting）；OnStateChanged 里的
    ///     CommandManager.InvalidateRequerySuggested 在无 Dispatcher 的线程调用=命令可用态不刷新。
    ///     UI 线程内直调（零跳转）；无 Application 的 harness 宿主同步执行
    /// </summary>
    private static async Task OnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        await tcs.Task;
    }

    private async Task OnKernelExitedAsync(Guard guard)
    {
        // FATAL 类致命错误（C-4）：重启同配置必再挂，跳过重启循环直接停止并明示错误
        if (guard.FatalAfterStart)
        {
            if (State is not (State.Started or State.Degraded))
                return;

            Log.Error("Kernel {Name} fatal error after start, stopping without restart", guard.Name);
            await StopCoreAsync();
            Global.UIHub.NotifyTip(I18N.T("main.kernelFatalStopped"), info: false);
            Utils.Utils.Open(guard.LogPath);
            return;
        }

        if (!Global.Settings.Network.KernelAutoRestart)
        {
            Log.Warning("Kernel {Name} exited; auto restart disabled", guard.Name);
            return;
        }

        if (State is not (State.Started or State.Degraded))
            return;

        var now = DateTime.UtcNow;
        if (now - _lastKernelDeath > TimeSpan.FromSeconds(30))
            _kernelRestartCount = 0;

        _lastKernelDeath = now;
        _kernelRestartCount++;

        if (_kernelRestartCount > 5)
        {
            await StopCoreAsync();
            Global.UIHub.NotifyTip(I18N.T("main.kernelKeepsExitingStopped"), info: false);
            Utils.Utils.Open(Constants.LogFile);
            return;
        }

        var delay = _kernelRestartCount switch { 1 => 0, 2 => 5, 3 => 15, 4 => 60, _ => 60 };
        Global.UIHub.NotifyTip(I18N.TF("main.kernelExitedUnexpectedlyRestarting0", _kernelRestartCount, 5));
        UIEvents.RaiseKernelRestarting("kernel exited", _kernelRestartCount, 5);

        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay));

        if (State is not (State.Started or State.Degraded))
            return;

        try
        {
            await MainController.RestartCoreAsync();
            State = State.Started;
            RunPostStartTasks();
            Log.Information("Kernel restarted after unexpected exit ({Count})", _kernelRestartCount);
        }
        catch (Exception e)
        {
            Log.Error(e, "Kernel restart failed");
            await StopCoreAsync();
            Global.UIHub.NotifyTip(I18N.T("main.kernelKeepsExitingStopped"), info: false);
        }
    }

    #endregion

    #region 网络感知自愈（平移 HandleNetworkLost/Restored，M3-2 语义）

    private void OnNetworkLost(string reason)
    {
        RunOnUi(() =>
        {
            if (!Global.Settings.Network.AutoRestore)
        {
            Log.Information("Network lost ({Reason}); auto restore disabled", reason);
            return;
        }

        // Starting/Stopping 期间只记录，不动作（与用户操作互斥）
        if (State is not State.Started)
            return;

            Log.Information("Network lost ({Reason}), pausing", reason);
            _ = PauseForNetworkAsync();
        });
    }

    /// <summary>同步版线程入口调度（C-3）：非 UI 线程 fire-and-forget 场景</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private async Task PauseForNetworkAsync()
    {
        State = State.Degraded;

        try
        {
            if (MainController.Mode?.Type is ModeType.TunMode or ModeType.ShareMode)
                await StopCoreAsync(keepDegraded: true);
            else
                await MainController.StopServerControllerOnlyAsync();
        }
        catch (Exception e)
        {
            Log.Error(e, "Pause on network loss failed");
        }
    }

    private void OnNetworkRestored()
    {
        RunOnUi(() =>
        {
            if (!Global.Settings.Network.AutoRestore)
                return;

            if (State != State.Degraded)
                return;

            _ = RestoreNetworkAsync();
        });
    }

    private async Task RestoreNetworkAsync()
    {
        // 防重入：周期探活抖动会连发 NetworkRestored，多个恢复循环并发重建会互相踩
        if (Interlocked.CompareExchange(ref _restoring, 1, 0) != 0)
            return;

        try
        {
            var delays = new[] { 5, 10, 30, 60, 120 };

            for (var attempt = 0; attempt <= delays.Length; attempt++)
            {
                // 重试期间再次断网或用户干预 -> 放弃本轮
                if (State != State.Degraded)
                    return;

                // 上一轮失败后的等待期里网络又断了 -> 放弃本轮，等下一个恢复事件
                if (attempt > 0 && !await NetworkWatcher.IsConnectivityOkAsync())
                    return;

                try
                {
                    SetCustomStatusText(I18N.T("main.restoringNetwork"));
                    NativeMethods.RefreshDNSCache();

                    if (MainController.Mode?.Type is ModeType.TunMode or ModeType.ShareMode)
                    {
                        // 整链路重启：复用既有 Start 流程
                        State = State.Waiting;
                        await StartStopAsync();
                        return;
                    }

                    await MainController.RestartCoreAsync();
                    State = State.Started;
                    RunPostStartTasks();
                    Log.Information("Network restored, core rebuilt");
                    Global.UIHub.NotifyTip(I18N.T("main.networkRestoredConnectionRebuilt"));
                    return;
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Network restore attempt {Attempt} failed", attempt + 1);
                }

                if (attempt < delays.Length)
                    await Task.Delay(TimeSpan.FromSeconds(delays[attempt]));
            }

            await StopCoreAsync();
            Global.UIHub.NotifyTip(I18N.T("main.autoRestoreFailedStopped"), info: false);
        }
        finally
        {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }

    #endregion

    #region 电源事件（平移 SystemEvents_PowerModeChanged）

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            // C-3：系统消息线程入口，State 写入须在 UI 线程
            await OnUiThreadAsync(async () =>
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        if (!IsWaiting())
                        {
                            _resumeFlag = true;
                            Log.Information("OS Suspend, Stop");
                            await StopAsync();
                        }

                        break;
                    case PowerModes.Resume:
                        if (_resumeFlag)
                        {
                            _resumeFlag = false;
                            Log.Information("OS Resume, waiting for network stack");
                            await WaitForNetworkThenRestartAsync();
                        }

                        break;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Power mode change handling failed");
        }
    }

    /// <summary>唤醒后网络栈要几秒才就绪：先等一次可用性确认（最多 10s），超时强制走一次恢复尝试</summary>
    private async Task WaitForNetworkThenRestartAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !NetworkWatcher.IsNetworkAvailable())
            await Task.Delay(500);

        if (State == State.Degraded)
        {
            OnNetworkRestored();
            return;
        }

        await StartStopAsync();
    }

    #endregion

    #region 更新（平移 CheckUpdateAsync / NewVersionLabel_Click）

    public async Task CheckUpdateAsync()
    {
        try
        {
            UpdateChecker.NewVersionFound += OnUpdateCheckerOnNewVersionFound;
            await UpdateChecker.CheckAsync(Global.Settings.CheckBetaUpdate);
            // 低危⑤（2026-11-04 审查）：--ForceUpdate 强制提示新版本，但 GitHub 请求失败时
            // LatestRelease 为 null，径直取 LatestVersionNumber 会 NRE——失败如实提示不崩
            if (Flags.AlwaysShowNewVersionFound)
            {
                if (UpdateChecker.LatestRelease != null)
                    OnUpdateCheckerOnNewVersionFound(null!, null!);
                else
                    Global.UIHub.NotifyTip(I18N.T("main.newVersionCheckFailed"), info: false);
            }
        }
        finally
        {
            UpdateChecker.NewVersionFound -= OnUpdateCheckerOnNewVersionFound;
        }
    }

    private void OnUpdateCheckerOnNewVersionFound(object? o, EventArgs? eventArgs)
    {
        Global.UIHub.NotifyTip($"{I18N.T("main.newVersion") + I18N.T("common.colon")}{UpdateChecker.LatestVersionNumber}");
        NewVersionText = I18N.T("main.newVersion");
        NewVersionVisible = true;
    }

    private async Task DownloadUpdateAsync()
    {
        try
        {
            if (UpdateChecker.LatestRelease == null || !UpdateChecker.LatestRelease.assets.Any())
            {
                Utils.Utils.Open(UpdateChecker.LatestVersionUrl!);
                return;
            }

            if (!await Global.UIHub.ConfirmAsync(
                    I18N.TF("main.update.downloadConfirm", UpdateChecker.GetLatestReleaseContent())))
                return;

            Global.UIHub.NotifyTip(I18N.T("main.startDownloadingNewVersion"));
            NewVersionVisible = false;

            var progress = new Progress<int>();
            progress.ProgressChanged += (_, percentage) =>
            {
                NewVersionText = $"{percentage}%";
                NewVersionVisible = true;
            };

            string downloadDirectory = Path.Combine(Global.NetchDir, "data");

            var (updateFileName, sha256) = UpdateChecker.GetLatestUpdateFileNameAndHash();
            var updateFileUrl = UpdateChecker.LatestRelease.assets[0].browser_download_url!;

            var updateFileFullName = Path.Combine(downloadDirectory, updateFileName);
            var updater = new Updater(updateFileFullName, Global.NetchDir);

            var downloaded = false;
            if (File.Exists(updateFileFullName))
            {
                var fileHash = await Utils.Utils.Sha256CheckSumAsync(updateFileFullName);
                if (fileHash == sha256)
                    downloaded = true;
                else
                    File.Delete(updateFileFullName);
            }

            if (!downloaded)
            {
                try
                {
                    await WebUtil.DownloadFileAsync(updateFileUrl, updateFileFullName, progress);
                }
                catch (Exception e1)
                {
                    Log.Warning(e1, "Download Update File Failed");
                    throw new MessageException($"Download Update File Failed: {e1.Message}");
                }

                var fileHash = await Utils.Utils.Sha256CheckSumAsync(updateFileFullName);
                if (fileHash != sha256)
                    throw new MessageException(I18N.T("main.downloadedFileHasWrongHash"));
            }

            await StopAsync();
            await Configuration.SaveAsync();

            // Update
            await Task.Run(updater.ApplyUpdate);

            // release mutex, exit
            Program.SingleInstance.Dispose();
            Process.Start(Global.NetchExecutable);
            Environment.Exit(0);
        }
        catch (MessageException exception)
        {
            Global.UIHub.NotifyTip(exception.Message, info: false);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unhandled Update error");
            Global.UIHub.NotifyTip(exception.Message, info: false);
        }
        finally
        {
            NewVersionVisible = false;
            NewVersionText = I18N.T("main.newVersion");
        }
    }

    #endregion

    #region 退出 / 托盘（平移 Exit / Minimize / MainForm_FormClosing）

    /// <summary>关闭按钮 → 最小化到托盘（ExitWhenClosed 时真退出，语义平移 MainForm_FormClosing）</summary>
    public void HandleCloseRequest(CancelEventArgs e)
    {
        if (ExitWhenClosedRequested)
            return;

        e.Cancel = true;

        if (!Global.Settings.ExitWhenClosed)
            MinimizeToTray(true);
        else
            ExitCommand.Execute(null);
    }

    /// <summary>真退出标记（退出流程置位，绕过最小化拦截）</summary>
    public bool ExitWhenClosedRequested { get; private set; }

    private void MinimizeToTray(bool notifyFirstTime)
    {
        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            window.WindowState = WindowState.Minimized;
            window.Hide();
        }

        if (notifyFirstTime && _isFirstCloseWindow)
        {
            Global.UIHub.NotifyTip(I18N.T("main.netchMinimizedNotificationBarDouble"));
            _isFirstCloseWindow = false;
        }
    }

    public async Task ExitAsync(bool forceExit = false, bool saveConfiguration = true)
    {
        if (!IsWaiting() && !Global.Settings.StopWhenExited && !forceExit)
        {
            Global.UIHub.Alert(I18N.T("main.pressStopButtonFirst"));
            Global.UIHub.ShowMainForm();
            return;
        }

        SaveWindowSize();
        ExitWhenClosedRequested = true;

        if (App.CurrentTrayIcon != null)
            App.CurrentTrayIcon.Visibility = Visibility.Hidden;

        Application.Current?.MainWindow?.Hide();

        if (saveConfiguration)
            await Configuration.SaveAsync();

        // C-2（2026-11-04 审查）：退出瞬间临时文件被杀软/更新器占用时 IOException 冒泡会中断退出流程，
        // 窗口已藏、托盘已隐 = 僵尸进程。清理本就是尽力而为，失败记日志继续退出
        foreach (var file in new[] { Constants.TempConfig, Constants.TempRouteFile })
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Delete temp file on exit failed: {File}", file);
            }

        await StopAsync();

        Environment.Exit(Environment.ExitCode);
    }

    /// <summary>退出前记录窗口与连接页列宽（最大化/最小化态窗口尺寸不记录，沿用 a6bf2c1 语义；列宽与窗口状态无关照记）</summary>
    public void SaveWindowSize()
    {
        var window = Application.Current?.MainWindow;
        if (window != null && window.WindowState == WindowState.Normal)
        {
            Global.Settings.MainFormWidth = (int)window.ActualWidth;
            Global.Settings.MainFormHeight = (int)window.ActualHeight;
        }

        Views.ConnectionsView.SaveColumnWidths();
    }

    #endregion

    #region 主窗 Loaded 启动钩子（平移 MainForm_Load）

    /// <summary>主窗 Loaded 后的一次性启动钩子（harness 不调用）</summary>
    public async void OnMainWindowLoaded()
    {
        StartStatusTimer();

        DelayTestHelper.UpdateTick(true);

        ModeService.Instance.Load();

        // 网络状态感知：断网有序暂停、恢复自动重建
        _networkWatcher.DebounceSeconds = Global.Settings.Network.DebounceSeconds;
        _networkWatcher.NetworkLost += OnNetworkLost;
        _networkWatcher.NetworkRestored += OnNetworkRestored;
        _networkWatcher.Start();

        Program.SingleInstance.StartListenServer();

        // 检查更新
        if (Global.Settings.CheckUpdateWhenOpened)
            _ = CheckUpdateAsync();

        // 检查订阅更新
        if (Global.Settings.UpdateServersWhenOpened)
            await UpdateServersFromSubscriptionAsync();

        // 打开软件时启动加速
        if (Global.Settings.StartWhenOpened)
            StartStopCommand.Execute(null);
    }

    /// <summary>启动时更新订阅（平移 UpdateServersFromSubscriptionAsync；订阅页 06 落地前的宿主）</summary>
    public async Task UpdateServersFromSubscriptionAsync()
    {
        if (Global.Settings.Subscription.Count <= 0)
        {
            Global.UIHub.Alert(I18N.T("subscription.none"));
            return;
        }

        SetCustomStatusText(I18N.T("subscription.updating"));

        try
        {
            await SubscriptionUtil.UpdateServersAsync();

            UIEvents.RaiseServersChanged();
            await Configuration.SaveAsync();
            SetCustomStatusText(I18N.T("subscription.updated"));
        }
        catch (Exception e)
        {
            Global.UIHub.NotifyTip(I18N.T("main.unhandledUpdateServersError") + "\n" + e.Message, info: false);
            Log.Error(e, "Unhandled Update servers error");
        }
        finally
        {
            SetCustomStatusText(null);
        }
    }

    #endregion
}
