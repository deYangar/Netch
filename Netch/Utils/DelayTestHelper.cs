using Microsoft.VisualStudio.Threading;
using Netch.Controllers;
using Netch.Models;
using Netch.Servers;
using Timer = System.Timers.Timer;

namespace Netch.Utils;

public static class DelayTestHelper
{
    private static readonly Timer Timer;

    private static readonly AsyncSemaphore Lock = new(1);

    private static readonly AsyncSemaphore PoolLock = new(16);

    /// <summary>手动真实测速进行中/排队中标志（重复点击忽略）</summary>
    private static int _realTestRunning;

    public static readonly NumberRange Range = new(0, int.MaxValue / 1000);

    private static bool _enabled = true;

    static DelayTestHelper()
    {
        Timer = new Timer
        {
            Interval = 10000,
            AutoReset = true
        };

        Timer.Elapsed += (_, _) => PerformTestAsync().Forget();
    }

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            UpdateTick();
        }
    }

    /// <param name="waitFinish">if does not get lock, block until last release</param>
    public static Task PerformTestAsync(bool waitFinish = false)
    {
        return RunTestLoopAsync(waitFinish, async () =>
        {
            // 多节点 sing-box 实例在跑：经各出站真实访问测试地址（Clash API），
            // 比裸 TCPing/ICMPing 更能反映节点可用性；不在实例内的节点回落旧测法
            if (MainController.ServerController is SingboxController { MultiNode: true, ClashApiPort: not null } controller)
            {
                var taggedServers = controller.OutboundTagMap.Values.ToHashSet();

                var tasks = controller.OutboundTagMap.Select(async kv =>
                    {
                        using (await PoolLock.EnterAsync())
                        {
                            var delay = await ClashApiClient.GetProxyDelayAsync(controller.ClashApiPort!.Value, kv.Key);
                            kv.Value.SetDelay(delay ?? -4);
                        }
                    })
                    .Concat(Global.Settings.Server
                        .Where(s => !taggedServers.Contains(s))
                        .Select(async s =>
                        {
                            using (await PoolLock.EnterAsync())
                            {
                                await s.PingAsync();
                            }
                        }));

                await Task.WhenAll(tasks);
            }
            else
            {
                var tasks = Global.Settings.Server.Select(async s =>
                {
                    using (await PoolLock.EnterAsync())
                    {
                        await s.PingAsync();
                    }
                });

                await Task.WhenAll(tasks);
            }
        });
    }

    /// <summary>
    ///     「测速全部」真实测速（2026-11-19 咩咩拍板，取代旧 PerformRunningTestAsync 的逐节点分发——
    ///     非成员逐个起停临时实例不可用；C-5 2026-11-04 审查的 16 并发防连接风暴约束保留）：
    ///     运行实例成员经主实例 Clash API、其余全部节点经临时实例出站实测；
    ///     握手测速（TCPing/ICMP 只到入口）不再作为手动测速结果。
    ///     两路串行（各自 16 并发不同时跑），防连接风暴互争带宽失真（C-5 同源约束）；
    ///     定时测活仍走 <see cref="PerformTestAsync"/>（轻量，Soft 色徽章区分两种语义）。
    ///     与定时测活共用防重入锁但语义不同：手动测速必须真正执行（排队等锁后跑），
    ///     不复用 RunTestLoopAsync 的「等到上一轮即返回」；重复点击在跑/排队中直接忽略
    /// </summary>
    public static async Task PerformRealTestAsync()
    {
        if (Interlocked.Exchange(ref _realTestRunning, 1) == 1)
            return;

        try
        {
            using var _ = await Lock.EnterAsync();

            try
            {
                if (MainController.ServerController is SingboxController { MultiNode: true, ClashApiPort: not null } controller)
                {
                    var taggedServers = controller.OutboundTagMap.Values.ToHashSet();

                    // 成员同样按入口 IP 分组限流（拥塞与临时实例同源——同入口机持续并发把 RTT 抬高一倍+）
                    var resolved = await SpeedtestController.ResolveEndpointsAsync(controller.OutboundTagMap.Values);
                    foreach (var s in controller.OutboundTagMap.Values.Where(s => !resolved.ContainsKey(s)))
                        s.SetDelay(-2);

                    var port = controller.ClashApiPort!.Value;
                    var groups = controller.OutboundTagMap
                        .Where(kv => resolved.ContainsKey(kv.Value))
                        .GroupBy(kv => resolved[kv.Value].ToString())
                        .Select(g => (g.Key, List: g.ToList()))
                        .ToList();

                    await Task.WhenAll(groups.Select(g =>
                    {
                        // 组内一把信号量：同一入口机的节点压到 3 并发以内，跨组（跨入口机）并行
                        var groupLock = new AsyncSemaphore(SpeedtestController.PerEndpointConcurrency);
                        return Task.WhenAll(g.List.Select(async kv =>
                        {
                            using var _group = await groupLock.EnterAsync();
                            var delay = await ClashApiClient.GetProxyDelayAsync(port, kv.Key);
                            kv.Value.SetDelay(delay ?? -4);
                        }));
                    }));

                    var rest = Global.Settings.Server.Where(s => !taggedServers.Contains(s)).ToList();
                    await SpeedtestController.TestAsync(rest);
                }
                else
                {
                    await SpeedtestController.TestAsync(Global.Settings.Server);
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Real delay test loop failed");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _realTestRunning, 0);
        }
    }

    /// <summary>测试循环公共骨架：整体防重入（Lock）+ 执行体异常吞掉（与原语义一致）</summary>
    private static async Task RunTestLoopAsync(bool waitFinish, Func<Task> body)
    {
        if (Lock.CurrentCount == 0)
        {
            if (waitFinish)
                (await Lock.EnterAsync()).Dispose();

            return;
        }

        using var _ = await Lock.EnterAsync();

        try
        {
            await body();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public static void UpdateTick(bool performTestAtOnce = false)
    {
        UpdateTick(Global.Settings.DetectionTick, performTestAtOnce);
    }

    /// <param name="interval">interval(seconds), 0 disable, MaxValue <c>int.MaxValue/1000</c></param>
    /// <param name="performTestAtOnce"></param>
    private static void UpdateTick(int interval, bool performTestAtOnce = false)
    {
        Timer.Stop();

        var enable = Enabled && interval > 0 && Range.InRange(interval);
        if (enable)
        {
            Timer.Interval = interval * 1000;
            Timer.Start();
            if (performTestAtOnce)
                PerformTestAsync().Forget();
        }
    }
}