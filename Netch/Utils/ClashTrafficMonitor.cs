using Netch.Controllers;
using Netch.Enums;
using Netch.Models;
using Netch.Servers;
using Netch.UI.Hub;

namespace Netch.Utils;

/// <summary>
///     连接级流量统计（sing-box 内核，进程/TUN 同源）：每秒轮询 Clash API /connections，
///     按出站链分类增量累计。
///     口径：chains 含 direct = 直连；含 block/dns 或无 chains = 忽略（拒绝连接无流量、DNS 劫持本地应答）；
///     其余（节点出站/selector）= 代理。状态栏「已使用」只取代理累计。
///     关闭连接按最后一次观测值入账（秒级轮询存在亚秒级尾部误差）。
/// </summary>
public static class ClashTrafficMonitor
{
    private const int HistoryCapacity = 500;

    private static readonly object Lock = new();
    private static readonly Queue<ClosedConnection> HistoryStore = new();

    /// <summary>连接 id -> 最后观测到的连接对象（判增量与关闭）</summary>
    private static readonly Dictionary<string, ClashConnection> LastSeen = new();

    private static CancellationTokenSource? _cancellationTokenSource;

    /// <summary>代理出站累计（字节）</summary>
    public static ulong ProxyUpTotal;
    public static ulong ProxyDownTotal;

    /// <summary>直连出站累计（字节）</summary>
    public static ulong DirectUpTotal;
    public static ulong DirectDownTotal;

    /// <summary>最近一次轮询的增量（≈每秒速率，字节）</summary>
    public static ulong ProxyUpSpeed;
    public static ulong ProxyDownSpeed;
    public static ulong DirectUpSpeed;
    public static ulong DirectDownSpeed;

    public static bool Running => _cancellationTokenSource != null;

    /// <summary>出站分类</summary>
    public enum OutboundKind
    {
        Proxy,
        Direct,
        /// <summary>拒绝/本地 DNS 应答等无法归属代理或直连的连接</summary>
        Ignored
    }

    public class ClosedConnection
    {
        public ClashConnection Connection { get; init; } = new();
        public DateTime ClosedAt { get; init; }

        /// <summary>远端地址（ip:port，历史页列绑定）</summary>
        public string RemoteText => $"{Connection.Metadata.DestinationIP}:{Connection.Metadata.DestinationPort}";

        /// <summary>归属地显示名（行入历史时由订阅方查 GeoLookup 填一次）</summary>
        public string GeoText { get; set; } = "";
    }

    /// <summary>每秒快照（内核退出间隙轮询失败不触发）。线程池线程触发，订阅方自行切线程</summary>
    public static event Action<ClashConnectionsResponse>? ConnectionsPolled;

    /// <summary>连接关闭（携带最后一次观测值）。线程池线程触发，订阅方自行切线程</summary>
    public static event Action<ClosedConnection>? ConnectionClosed;

    /// <summary>
    ///     统计主循环：随会话启停（Started/Degraded 轮询）。
    ///     端口每次现取——守护重建内核会重新选 Clash API 端口，不能用启动时缓存的值
    /// </summary>
    public static async Task RunAsync()
    {
        Stop();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        await Task.Run(async () =>
        {
            // 状态栏流量标签可见性：与 ETW 路径对称，启动时统一打开
            //（只靠每秒刷新里设置直连标签可见，会让已使用/代理流速永远隐藏）
            UIEvents.RaiseTrafficSourceStarted(false);

            lock (Lock)
            {
                ProxyUpTotal = ProxyDownTotal = DirectUpTotal = DirectDownTotal = 0;
                ProxyUpSpeed = ProxyDownSpeed = DirectUpSpeed = DirectDownSpeed = 0;
                LastSeen.Clear();
                HistoryStore.Clear();
            }

            while (!token.IsCancellationRequested &&
                   Global.UIHub.State is State.Started or State.Degraded)
            {
                try
                {
                    var port = (MainController.ServerController as SingboxController)?.ClashApiPort;
                    if (port != null)
                    {
                        // 轮询失败（内核退出间隙）返回 null：跳过本轮，保留 LastSeen，
                        // 防止把存活连接误判关闭导致恢复后基数重复入账
                        var snapshot = await ClashApiClient.GetConnectionsAsync(port.Value);
                        if (snapshot != null)
                        {
                            Accumulate(snapshot);

                            var totals = Totals;
                            var speeds = Speeds;
                            UIEvents.RaiseTrafficUpdated(
                                speeds.ProxyUp, speeds.ProxyDown, speeds.DirectUp, speeds.DirectDown,
                                totals.ProxyUp, totals.ProxyDown, totals.DirectUp, totals.DirectDown);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "ClashTrafficMonitor tick failed");
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token).ConfigureAwait(false);
    }

    public static void Stop()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
    }

    /// <summary>累计一份快照并触发事件（public 供单测驱动，勿在 UI 线程外假设触发线程）</summary>
    public static List<ClosedConnection> Accumulate(ClashConnectionsResponse snapshot)
    {
        var connections = snapshot.Connections ?? new List<ClashConnection>();
        var closed = new List<ClosedConnection>();

        lock (Lock)
        {
            var before = (ProxyUpTotal, ProxyDownTotal, DirectUpTotal, DirectDownTotal);
            var alive = new HashSet<string>();

            foreach (var conn in connections)
            {
                if (string.IsNullOrEmpty(conn.Id))
                    continue;

                alive.Add(conn.Id);
                LastSeen.TryGetValue(conn.Id, out var last);
                var upDelta = Delta(conn.Upload, last?.Upload);
                var downDelta = Delta(conn.Download, last?.Download);

                switch (Classify(conn.Chains))
                {
                    case OutboundKind.Proxy:
                        ProxyUpTotal += upDelta;
                        ProxyDownTotal += downDelta;
                        break;
                    case OutboundKind.Direct:
                        DirectUpTotal += upDelta;
                        DirectDownTotal += downDelta;
                        break;
                }

                LastSeen[conn.Id] = conn;
            }

            foreach (var id in LastSeen.Keys.Where(id => !alive.Contains(id)).ToList())
            {
                LastSeen.Remove(id, out var conn);
                var entry = new ClosedConnection { Connection = conn!, ClosedAt = DateTime.Now };
                closed.Add(entry);
                HistoryStore.Enqueue(entry);
                while (HistoryStore.Count > HistoryCapacity)
                    HistoryStore.Dequeue();
            }

            var after = (ProxyUpTotal, ProxyDownTotal, DirectUpTotal, DirectDownTotal);
            ProxyUpSpeed = after.Item1 - before.Item1;
            ProxyDownSpeed = after.Item2 - before.Item2;
            DirectUpSpeed = after.Item3 - before.Item3;
            DirectDownSpeed = after.Item4 - before.Item4;
        }

        foreach (var entry in closed)
        {
            entry.GeoText = GeoLookup.Lookup(entry.Connection.Metadata.DestinationIP);
            ConnectionClosed?.Invoke(entry);
        }

        ConnectionsPolled?.Invoke(snapshot);
        return closed;
    }

    /// <summary>连接流量归因（public 供单测与连接表着色复用）</summary>
    public static OutboundKind Classify(IEnumerable<string>? chains)
    {
        if (chains == null)
            return OutboundKind.Ignored;

        var any = false;
        var direct = false;
        foreach (var tag in chains)
        {
            any = true;
            if (string.Equals(tag, "block", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "dns", StringComparison.OrdinalIgnoreCase))
                return OutboundKind.Ignored;

            if (string.Equals(tag, "direct", StringComparison.OrdinalIgnoreCase))
                direct = true;
        }

        return any ? (direct ? OutboundKind.Direct : OutboundKind.Proxy) : OutboundKind.Ignored;
    }

    /// <summary>连接计数按出站分类（连接页汇总行分账，口径与流量分账一致；block/dns 计入总数不计入两类）</summary>
    public static (int Proxy, int Direct) CountKinds(IEnumerable<ClashConnection>? connections)
    {
        if (connections == null)
            return (0, 0);

        var proxy = 0;
        var direct = 0;
        foreach (var conn in connections)
        {
            if (string.IsNullOrEmpty(conn.Id))
                continue;

            switch (Classify(conn.Chains))
            {
                case OutboundKind.Proxy:
                    proxy++;
                    break;
                case OutboundKind.Direct:
                    direct++;
                    break;
            }
        }

        return (proxy, direct);
    }

    /// <summary>读数快照（累计 + 速率）</summary>
    public static (ulong ProxyUp, ulong ProxyDown, ulong DirectUp, ulong DirectDown) Totals
    {
        get
        {
            lock (Lock)
                return (ProxyUpTotal, ProxyDownTotal, DirectUpTotal, DirectDownTotal);
        }
    }

    public static (ulong ProxyUp, ulong ProxyDown, ulong DirectUp, ulong DirectDown) Speeds
    {
        get
        {
            lock (Lock)
                return (ProxyUpSpeed, ProxyDownSpeed, DirectUpSpeed, DirectDownSpeed);
        }
    }

    public static IReadOnlyList<ClosedConnection> HistorySnapshot()
    {
        lock (Lock)
            return HistoryStore.ToList();
    }

    public static void ClearHistory()
    {
        lock (Lock)
            HistoryStore.Clear();
    }

    /// <summary>清零全部统计状态（单测隔离用）</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            ProxyUpTotal = ProxyDownTotal = DirectUpTotal = DirectDownTotal = 0;
            ProxyUpSpeed = ProxyDownSpeed = DirectUpSpeed = DirectDownSpeed = 0;
            LastSeen.Clear();
            HistoryStore.Clear();
        }
    }

    private static ulong Delta(long current, long? lastSeen)
    {
        // 计数回退（内核重启/连接复用）按增量 0 处理，首见连接 lastSeen 为空则全额入账
        if (current <= 0 || (lastSeen.HasValue && current <= lastSeen.Value))
            return 0;

        return (ulong)(current - (lastSeen ?? 0));
    }
}
