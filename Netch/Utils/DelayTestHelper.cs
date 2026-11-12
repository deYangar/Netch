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
    ///     运行态「测速全部」（C-5，2026-11-04 审查）：与等待态共用 16 并发池与防重入锁——
    ///     此前运行态分支 331 节点无上限 Task.WhenAll，连接风暴互相争带宽导致结果失真、还可能触发服务端限速
    /// </summary>
    public static Task PerformRunningTestAsync()
    {
        return RunTestLoopAsync(false, async () =>
        {
            var tasks = Global.Settings.Server.Select(async s =>
            {
                using (await PoolLock.EnterAsync())
                {
                    await MainController.TestNodeDelayAsync(s);
                }
            });

            await Task.WhenAll(tasks);
        });
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