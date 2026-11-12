using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.Threading;

namespace Netch.Controllers;

/// <summary>
///     网络状态感知：把三类事件源收敛进同一状态机（M3-2 §1.2，2026-10-29 补连通性周期判定）。
///     - NetworkChange.NetworkAvailabilityChanged / NetworkAddressChanged 订阅后进防抖窗口，
///       窗口结束统一判定一次（Windows 在 Wi-Fi 搜索/驱动重置时会连发事件）
///     - 「有网络」= 存在 Up 的非 loopback/tunnel 接口且该接口有非链路本地单播地址，**且 DNS 探活可达**。
///       只看接口抓不到「接口在但上游断网」（光猫死/运营商故障/WiFi 连着但没网）——内核空跑的根源；
///       周期判定（默认 15s）+ 连续失败阈值（默认 2 轮）覆盖该盲区，接口事件路径共用同一判定。
///     - 探活用 DNS 查询而非 ICMP：TUN 模式下公网 ICMP 被 sing-box 本地代答，探了也是假的；
///       UDP 53 查询被 hijack 后走真实上游，上游断则无响应，三种运行形态（无内核/进程模式/TUN）判定一致。
///     - 接口集合做 diff：只有承载流量的物理接口集合变化才算「网络切换」，同接口续租忽略
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    private readonly System.Threading.Timer _debounce;
    private readonly System.Threading.Timer _probe;
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly object _lock = new();
    private HashSet<string> _lastInterfaces = new();
    private bool _lastAvailable;
    private bool _started;
    private bool _disposed;

    /// <summary>连续探活失败轮数（成功清零；达到阈值才判死，防单次抖动误杀）</summary>
    private int _probeFailures;

    public NetworkWatcher()
    {
        _debounce = new System.Threading.Timer(_ => EvaluateAsync().Forget(), null, Timeout.Infinite, Timeout.Infinite);
        _probe = new System.Threading.Timer(_ => EvaluateAsync().Forget(), null,
            TimeSpan.FromSeconds(ProbeIntervalSeconds), TimeSpan.FromSeconds(ProbeIntervalSeconds));
    }

    /// <summary>防抖窗口（秒），窗口内的连续事件合并为一次判定</summary>
    public int DebounceSeconds { get; set; } = 3;

    /// <summary>连通性周期判定间隔（秒）</summary>
    public int ProbeIntervalSeconds { get; init; } = 15;

    /// <summary>连续探活失败判定断网的阈值（轮）</summary>
    public int ProbeFailureThreshold { get; init; } = 2;

    /// <summary>断网（防抖确认后触发），参数为原因描述</summary>
    public event Action<string>? NetworkLost;

    /// <summary>网络恢复或承载接口切换（防抖确认后触发）</summary>
    public event Action? NetworkRestored;

    /// <summary>当前是否判定为「有网络」（不做探活）</summary>
    public static bool IsNetworkAvailable()
    {
        try
        {
            return Snapshot().Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>测试注入点：替换 DNS 探活（生产路径为真实查询）</summary>
    internal static Func<Task<bool>>? ProbeOverride;

    /// <summary>
    ///     一次性连通性判定：接口存在且 DNS 探活可达。
    ///     恢复重建的每轮重试前调用，避免断网期间盲目重建内核。
    /// </summary>
    public static async Task<bool> IsConnectivityOkAsync()
    {
        try
        {
            if (Snapshot().Count == 0)
                return false;

            return await ProbeDnsAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        try
        {
            _lastInterfaces = Snapshot();
        }
        catch (Exception)
        {
            _lastInterfaces = new HashSet<string>();
        }

        _lastAvailable = _lastInterfaces.Count > 0;

        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Schedule();
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        Schedule();
    }

    private void Schedule()
    {
        if (_disposed)
            return;

        var window = Math.Clamp(DebounceSeconds, 1, 60) * 1000;

        try
        {
            _debounce.Change(window, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }
    }

    /// <summary>统一判定（事件防抖与周期探活共用；接口判定 + DNS 探活 + 阈值 + 状态机）</summary>
    private async Task EvaluateAsync()
    {
        if (_disposed)
            return;

        // 已有判定在跑（事件与周期撞车）：跳过本轮，下轮周期/事件补偿
        if (!await _evalLock.WaitAsync(0))
            return;

        try
        {
            HashSet<string> current;
            try
            {
                current = Snapshot();
            }
            catch (Exception)
            {
                return;
            }

            var available = current.Count > 0;
            if (available)
            {
                if (await ProbeDnsAsync())
                    _probeFailures = 0;
                else if (++_probeFailures >= ProbeFailureThreshold)
                    available = false;
            }
            else
            {
                _probeFailures = 0;
            }

            bool wasAvailable;
            bool lost;
            bool restored;

            lock (_lock)
            {
                var switched = !_lastInterfaces.SetEquals(current);

                wasAvailable = _lastAvailable;
                lost = _lastAvailable && !available;
                // 从不可用变可用，或可用但承载接口集合变了（换网）都算一次恢复/重建
                restored = available && (!_lastAvailable || switched);

                _lastAvailable = available;
                _lastInterfaces = current;
            }

            if (lost)
            {
                Log.Information("NetworkWatcher: network lost");
                NetworkLost?.Invoke("connectivity lost");
            }
            else if (restored)
            {
                Log.Information("NetworkWatcher: network available (came back={CameBack})", !wasAvailable);
                NetworkRestored?.Invoke();
            }
        }
        finally
        {
            _evalLock.Release();
        }
    }

    /// <summary>单测驱动：手动跑一轮判定（绕过防抖/周期 timer）</summary>
    internal Task EvaluateForTestAsync() => EvaluateAsync();

    /// <summary>DNS 探活：向公共 DNS（223.5.5.5 / 119.29.29.29）发 A 查询，任一在超时内应答即视为可达</summary>
    internal static async Task<bool> ProbeDnsAsync()
    {
        if (ProbeOverride != null)
            return await ProbeOverride();

        foreach (var target in new[] { "223.5.5.5", "119.29.29.29" })
        {
            try
            {
                using var udp = new UdpClient();
                var query = BuildDnsQuery("qq.com");
                await udp.SendAsync(query, query.Length, target, 53);

                var recvTask = udp.ReceiveAsync();
                if (await Task.WhenAny(recvTask, Task.Delay(2000)) != recvTask)
                {
                    // 超时：关掉 socket 让挂起的接收尽快结束，接住异常防 Unobserved 刷屏
                    udp.Close();
                    try
                    {
                        await recvTask;
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    continue;
                }

                var data = (await recvTask).Buffer;
                // 应答 >= 头部 12 字节、ID 匹配、QR=1（应答方向）。rcode 不看：NXDOMAIN 也证明链路可达
                if (data.Length >= 12 && data[0] == query[0] && data[1] == query[1] && (data[2] & 0x80) != 0)
                    return true;
            }
            catch (Exception)
            {
                // 单目标失败换下一个
            }
        }

        return false;
    }

    /// <summary>构造最小 DNS A 查询报文（随机 ID + RD=1 + 单问题）</summary>
    private static byte[] BuildDnsQuery(string domain)
    {
        var id = (ushort)Random.Shared.Next(0, 0x10000);

        using var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)id);
        ms.WriteByte(0x01); // flags: RD
        ms.WriteByte(0x00);
        ms.WriteByte(0);
        ms.WriteByte(1); // QDCOUNT
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);

        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.WriteByte(0); // root label
        ms.WriteByte(0); ms.WriteByte(1); // QTYPE=A
        ms.WriteByte(0); ms.WriteByte(1); // QCLASS=IN
        return ms.ToArray();
    }

    /// <summary>
    ///     承载流量的接口集合：Up、非 loopback/tunnel、非本程序 tun 接口、且有非链路本地单播地址
    /// </summary>
    private static HashSet<string> Snapshot()
    {
        var set = new HashSet<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            if (ni.Name.StartsWith("netch", StringComparison.OrdinalIgnoreCase))
                continue;

            bool hasAddress;
            try
            {
                hasAddress = ni.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                    !a.Address.IsIPv6LinkLocal);
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (hasAddress)
                set.Add(ni.Id);
        }

        return set;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_started)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        }

        _debounce.Dispose();
        _probe.Dispose();
        _evalLock.Dispose();
    }
}
