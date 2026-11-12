using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Netch.Models;
using Netch.Utils;

namespace Netch.ViewModels;

/// <summary>
///     连接观测页 VM（07§C）：进程/内核/历史三视图。
///     进程页 = Redirector 快照 1Hz 拉（Interops.Redirector.ExportConnections，旧 ConnectionForm.PollProcess 同源）；
///     内核页 = ConnectionsPolled 推送（Dispatcher 合帧 ≥200ms 批量应用）；
///     历史 = ConnectionClosed 头插 + 环形 500（页面常驻单例不销毁，导航切换不丢）。
///     三页均为长寿命 ObservableCollection + 按 Id 就地更新：DataGrid 表头排序（SortDescriptions 挂在
///     视图上）不再被每秒整表替换冲掉；筛选走 ICollectionView.Filter；流速 = 相邻快照按 Id 字节差分。
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    private readonly ObservableCollection<ConnectionInfo> _processConnections = new();
    private readonly Dictionary<string, ConnectionInfo> _processIndex = new();
    private readonly Dictionary<string, (ulong Up, ulong Down)> _processLastBytes = new();
    private readonly Stopwatch _processClock = Stopwatch.StartNew();

    private readonly ObservableCollection<ConnectionInfo> _kernelConnections = new();
    private readonly Dictionary<string, ConnectionInfo> _kernelIndex = new();
    private readonly List<ConnectionInfo> _kernelPending = new();
    private readonly Dictionary<string, (ulong Up, ulong Down)> _kernelLastBytes = new();
    private readonly Stopwatch _kernelClock = Stopwatch.StartNew();
    private double _kernelLastPollSeconds;

    private readonly ObservableCollection<ClashTrafficMonitor.ClosedConnection> _history = new();

    private bool _viewsInitialized;

    /// <summary>各 Tab 汇总缓存（B6 反馈②：进程/内核两数据源每秒都会刷新，
    /// 若共写一个 SummaryText 会交替闪现对方数值——按 Tab 分源，仅当前 Tab 的刷新落 UI）</summary>
    private string _summaryProcess = "";
    private string _summaryKernel = "";
    private string _summaryHistory = "";

    /// <summary>内核页 pending 的分账计数（OnPolled 数好，ApplyKernelPending 消费）</summary>
    private (int Proxy, int Direct) _kernelPendingCounts;

    public ConnectionsViewModel()
    {
        ClashTrafficMonitor.ConnectionsPolled += OnPolled;
        ClashTrafficMonitor.ConnectionClosed += OnClosed;

        // HistorySnapshot 为旧→新序（Queue），反转后加入使列表头=最新（与 OnClosed 头插一致）
        var snapshot = ClashTrafficMonitor.HistorySnapshot();
        for (var i = snapshot.Count - 1; i >= 0; i--)
            _history.Add(snapshot[i]);
        _summaryHistory = Netch.Localization.I18N.TF("connections.summary.history", _history.Count);

        EnsureViews();

        // 进程页 1Hz 快照（页面常驻，VM 单例随首建启动）
        _processTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _processTimer.Tick += (_, _) => PollProcess();
        _processTimer.Start();

        SummaryText = _summaryProcess;
    }

    private readonly DispatcherTimer _processTimer;

    /// <summary>进程页行集合（长寿命，按 Id 就地更新；视图 Filter/Sort 挂在 ICollectionView 上）</summary>
    public ObservableCollection<ConnectionInfo> ProcessConnections => _processConnections;

    public ObservableCollection<ConnectionInfo> KernelConnections => _kernelConnections;

    public ObservableCollection<ClashTrafficMonitor.ClosedConnection> History => _history;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _pauseRefresh;

    [ObservableProperty]
    private string _summaryText = "";

    public System.Windows.Visibility TabProcessVis => SelectedTabIndex == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility TabKernelVis => SelectedTabIndex == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility TabHistoryVis => SelectedTabIndex == 2 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(TabProcessVis));
        OnPropertyChanged(nameof(TabKernelVis));
        OnPropertyChanged(nameof(TabHistoryVis));
        SummaryText = value switch
        {
            0 => _summaryProcess,
            1 => _summaryKernel,
            _ => _summaryHistory
        };
        RefreshVisible();
    }

    partial void OnFilterTextChanged(string value) => RefreshViews();

    partial void OnPauseRefreshChanged(bool value)
    {
        if (!value)
            RefreshVisible();
    }

    /// <summary>三个视图挂 Filter 闭包（闭包动态读 FilterText，Refresh 即重筛；排序由 DataGrid 表头自理）</summary>
    private void EnsureViews()
    {
        if (_viewsInitialized)
            return;

        _viewsInitialized = true;
        CollectionViewSource.GetDefaultView(_processConnections).Filter = o => MatchProcess((ConnectionInfo)o);
        CollectionViewSource.GetDefaultView(_kernelConnections).Filter = o => MatchProcess((ConnectionInfo)o);
        CollectionViewSource.GetDefaultView(_history).Filter = o => MatchHistory((ClashTrafficMonitor.ClosedConnection)o);

        // B6 反馈⑦：SortDescriptions 只在集合增删时评估，行属性就地更新不触发重排——
        // 排序后流速变化行序不动（0 排在 1.5M 前面）。LiveSorting 让可变键属性变更时视图实时重排；
        // 历史页行只读（累计值不再变化），无需 live。
        EnableLiveSorting(CollectionViewSource.GetDefaultView(_processConnections),
            nameof(ConnectionInfo.UpSpeed), nameof(ConnectionInfo.DownSpeed), nameof(ConnectionInfo.Up),
            nameof(ConnectionInfo.Down), nameof(ConnectionInfo.Process));
        EnableLiveSorting(CollectionViewSource.GetDefaultView(_kernelConnections),
            nameof(ConnectionInfo.UpSpeed), nameof(ConnectionInfo.DownSpeed), nameof(ConnectionInfo.Up),
            nameof(ConnectionInfo.Down), nameof(ConnectionInfo.Target), nameof(ConnectionInfo.Rule), nameof(ConnectionInfo.Chains));
    }

    /// <summary>开 LiveSorting：排序激活（SortDescriptions 非空）时，列出的属性变更即重排行</summary>
    private static void EnableLiveSorting(ICollectionView view, params string[] properties)
    {
        if (view is not System.Windows.Data.ListCollectionView list)
            return;

        foreach (var property in properties)
            list.LiveSortingProperties.Add(property);
        list.IsLiveSorting = true;
    }

    private void RefreshViews()
    {
        if (!_viewsInitialized)
            return;

        CollectionViewSource.GetDefaultView(_processConnections).Refresh();
        CollectionViewSource.GetDefaultView(_kernelConnections).Refresh();
        CollectionViewSource.GetDefaultView(_history).Refresh();
    }

    private bool MatchProcess(ConnectionInfo c)
    {
        var keyword = FilterText?.Trim();
        return string.IsNullOrEmpty(keyword)
            || c.Target?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || c.RemoteText?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || c.Process?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || c.Rule?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || c.Chains?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool MatchHistory(ClashTrafficMonitor.ClosedConnection h)
    {
        var keyword = FilterText?.Trim();
        return string.IsNullOrEmpty(keyword)
            || h.Connection.Metadata.Host?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || h.Connection.Metadata.DestinationIP.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void PollProcess()
    {
        if (PauseRefresh)
            return;

        var snapshot = Interops.Redirector.ExportConnections();

        // 流速差分：相邻 1Hz 快照按 Id 对齐（Id 消失/新增时速率记 0）
        var seconds = _processClock.Elapsed.TotalSeconds;
        _processClock.Restart();

        var seen = new HashSet<string>();
        foreach (var row in snapshot)
        {
            seen.Add(row.Id);
            var speedUp = 0UL;
            var speedDown = 0UL;
            if (_processLastBytes.TryGetValue(row.Id, out var last))
            {
                speedUp = SecondsBased(last.Up, row.Up, seconds);
                speedDown = SecondsBased(last.Down, row.Down, seconds);
            }

            _processLastBytes[row.Id] = (row.Up, row.Down);

            if (!_processIndex.TryGetValue(row.Id, out var existing))
            {
                row.UpSpeed = speedUp;
                row.DownSpeed = speedDown;
                row.Geo = GeoLookup.Lookup(row.Target);
                _processIndex[row.Id] = row;
                _processConnections.Add(row);
            }
            else
            {
                existing.Target = row.Target;
                existing.Up = row.Up;
                existing.Down = row.Down;
                existing.UpSpeed = speedUp;
                existing.DownSpeed = speedDown;
            }
        }

        // 快照里消失的连接移除（连接表导出即 lifetime 全集，closed 行已被 DLL lazy 清理）
        foreach (var id in _processIndex.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;

            _processIndex.Remove(id);
            _processLastBytes.Remove(id);
            for (var i = _processConnections.Count - 1; i >= 0; i--)
            {
                if (_processConnections[i].Id == id)
                    _processConnections.RemoveAt(i);
            }
        }

        UpdateSummaryProcess(snapshot.Count);
    }

    private static ulong SecondsBased(ulong last, ulong now, double seconds)
    {
        if (seconds <= 0 || now <= last)
            return 0;

        return (ulong)((now - last) / seconds);
    }

    private void OnPolled(ClashConnectionsResponse snapshot)
    {
        var payload = snapshot.Connections ?? new List<ClashConnection>();

        // 流速差分按相邻推送间隔（事件线程；合帧应用被暂停挡住时 last 不推进，恢复即按新差分刷新）
        var nowSeconds = _kernelClock.Elapsed.TotalSeconds;
        var dt = nowSeconds - _kernelLastPollSeconds;
        _kernelLastPollSeconds = nowSeconds;

        List<ConnectionInfo> rows;
        (int Proxy, int Direct) pendingCounts;
        lock (_kernelLastBytes)
        {
            rows = new List<ConnectionInfo>(payload.Count);
            foreach (var c in payload)
            {
                if (string.IsNullOrEmpty(c.Id))
                    continue;

                var up = (ulong)Math.Max(0, c.Upload);
                var down = (ulong)Math.Max(0, c.Download);
                var speedUp = 0UL;
                var speedDown = 0UL;
                if (_kernelLastBytes.TryGetValue(c.Id, out var last))
                {
                    speedUp = SecondsBased(last.Up, up, dt);
                    speedDown = SecondsBased(last.Down, down, dt);
                }

                _kernelLastBytes[c.Id] = (up, down);

                rows.Add(new ConnectionInfo
                {
                    Id = c.Id,
                    Target = !string.IsNullOrEmpty(c.Metadata.Host)
                        ? c.Metadata.Host
                        : $"{c.Metadata.DestinationIP}:{c.Metadata.DestinationPort}",
                    RemoteText = $"{c.Metadata.DestinationIP}:{c.Metadata.DestinationPort}",
                    Rule = string.IsNullOrEmpty(c.RulePayload) ? c.Rule : $"{c.Rule}({c.RulePayload})",
                    Chains = string.Join("→", c.Chains),
                    Up = up,
                    Down = down,
                    UpSpeed = speedUp,
                    DownSpeed = speedDown,
                    Start = c.Start
                });
            }

            // 汇总分账计数（与流量分账同口径 Classify；total 含 block/dns 等无法归属的连接）
            pendingCounts = ClashTrafficMonitor.CountKinds(payload);
        }

        lock (_kernelPending)
        {
            _kernelPending.Clear();
            _kernelPending.AddRange(rows);
            // 低危⑨（2026-11-04 审查）：tuple 跨线程读写非原子，写入/消费统一收进 _kernelPending 锁
            _kernelPendingCounts = pendingCounts;
        }

        // 合帧：≥200ms 批量 Apply（防高频刷 UI；事件在线程池线程触发，切 UI）
        if (nowSeconds - _lastKernelApply < 0.2)
            return;

        _lastKernelApply = nowSeconds;
        BeginInvoke(ApplyKernelPending);
    }

    private double _lastKernelApply = double.MinValue;

    private void ApplyKernelPending()
    {
        if (PauseRefresh)
            return;

        List<ConnectionInfo> rows;
        (int Proxy, int Direct) counts;
        lock (_kernelPending)
        {
            if (_kernelPending.Count == 0)
                return;

            rows = new List<ConnectionInfo>(_kernelPending);
            _kernelPending.Clear();
            counts = _kernelPendingCounts;
            _kernelPendingCounts = (0, 0);
        }

        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            seen.Add(row.Id);
            if (!_kernelIndex.TryGetValue(row.Id, out var existing))
            {
                row.Geo = GeoLookup.Lookup(row.RemoteText);
                _kernelIndex[row.Id] = row;
                _kernelConnections.Add(row);
            }
            else
            {
                existing.Target = row.Target;
                existing.Rule = row.Rule;
                existing.Chains = row.Chains;
                existing.Up = row.Up;
                existing.Down = row.Down;
                existing.UpSpeed = row.UpSpeed;
                existing.DownSpeed = row.DownSpeed;
            }
        }

        foreach (var id in _kernelIndex.Keys.ToList())
        {
            if (seen.Contains(id))
                continue;

            _kernelIndex.Remove(id);
            lock (_kernelLastBytes)
                _kernelLastBytes.Remove(id);
            for (var i = _kernelConnections.Count - 1; i >= 0; i--)
            {
                if (_kernelConnections[i].Id == id)
                    _kernelConnections.RemoveAt(i);
            }
        }

        UpdateSummaryKernel(rows.Count, counts.Proxy, counts.Direct);
    }

    private void OnClosed(ClashTrafficMonitor.ClosedConnection entry)
    {
        BeginInvoke(() =>
        {
            _history.Insert(0, entry);
            while (_history.Count > 500)
                _history.RemoveAt(_history.Count - 1);
            UpdateSummaryHistory(_history.Count);
        });
    }

    /// <summary>刷新当前 Tab 的可见数据（暂停时跳过——恢复时由 OnPauseRefreshChanged 触发一次性合帧）</summary>
    private void RefreshVisible()
    {
        if (PauseRefresh)
            return;

        switch (SelectedTabIndex)
        {
            case 0:
                PollProcess();
                break;
            case 1:
                BeginInvoke(ApplyKernelPending);
                break;
        }
    }

    /// <summary>进程页汇总（Redirector 快照无代理/直连概念，只报连接数）</summary>
    private void UpdateSummaryProcess(int total)
    {
        _summaryProcess = Netch.Localization.I18N.TF("connections.summary.process", total);
        if (SelectedTabIndex == 0)
            SummaryText = _summaryProcess;
    }

    /// <summary>内核页汇总（代理/直连按出站链分账；total 含 block/dns 等不可归属连接）</summary>
    private void UpdateSummaryKernel(int total, int proxy, int direct)
    {
        _summaryKernel = Netch.Localization.I18N.TF("connections.summary", total, proxy, direct);
        if (SelectedTabIndex == 1)
            SummaryText = _summaryKernel;
    }

    private void UpdateSummaryHistory(int total)
    {
        _summaryHistory = Netch.Localization.I18N.TF("connections.summary.history", total);
        if (SelectedTabIndex == 2)
            SummaryText = _summaryHistory;
    }

    private static void BeginInvoke(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted)
            return;

        if (app.Dispatcher.CheckAccess())
            action();
        else
// 事件线程 fire-and-forget 投递（无可等待的异步上下文，JTF 切换不适用）
#pragma warning disable VSTHRD001
            _ = app.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal);
#pragma warning restore VSTHRD001
    }
}
