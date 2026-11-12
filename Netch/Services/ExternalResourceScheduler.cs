using Netch.Controllers;
using Netch.Models;

namespace Netch.Services;

/// <summary>
///     外部资源自动更新调度器（R 线 §4.5）：全局开关开时启动延迟 30s 静默检查（仅 AutoUpdate=true 的资源），
///     此后每 AutoUpdateIntervalHours 一轮。
///     内核只检查只提醒（事件驱动徽章「可更新」，不自动换内核——1.x 偶有 breaking change）；
///     文件资源自动下载替换（指纹短路：未变不重下）。
///     全部动作经 BusyLock 与手动更新串行；静默失败不弹窗，徽章 + 日志如实记录。
/// </summary>
public static class ExternalResourceScheduler
{
    /// <summary>启动延迟（秒）：避开启动高峰（内核拉起/订阅更新）</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource? _cts;

    /// <summary>检查到远端有新版（内核资源；UI 侧据此亮「可更新」徽章）</summary>
    public static event Action<string>? ResourceUpdatable;

    /// <summary>文件资源自动更新成功（UI 侧据此刷新行徽章/详情）</summary>
    public static event Action<string>? ResourceUpdated;

    public static void Start()
    {
        if (_cts != null)
            return;

        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        Log.Information("External resource scheduler started (interval={Hours}h)", Global.Settings.ExternalResources.AutoUpdateIntervalHours);
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private static async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await RunOnceAsync(ct);

                var hours = Math.Max(1, Global.Settings.ExternalResources.AutoUpdateIntervalHours);
                await Task.Delay(TimeSpan.FromHours(hours), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>
    ///     一轮检查：逐个处理 AutoUpdate 开着的资源。单资源失败静默（日志）继续下一个；
    ///     手动操作持锁时拿不到锁即跳过该资源（不排队堆积，下轮再来）。
    ///     public 供测试与「立即检查」复用。
    /// </summary>
    public static async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (!Global.Settings.ExternalResources.AutoUpdateEnabled)
            return;

        foreach (var resource in ExternalResourceService.All.Where(ExternalResourceService.IsAutoUpdateOn).ToList())
        {
            ct.ThrowIfCancellationRequested();

            // 全局资源互斥：手动更新进行中则跳过（BusyLock 信号量 0 = 占用）
            if (ExternalResourceService.BusyLock.CurrentCount == 0)
            {
                Log.Debug("Auto-update skipped (busy): {Key}", resource.Key);
                continue;
            }

            try
            {
                await CheckAndUpdateAsync(resource);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Auto-update failed: {Key}", resource.Key);
            }
        }
    }

    private static async Task CheckAndUpdateAsync(ExternalResource resource)
    {
        switch (resource.Provider)
        {
            case ExternalResourceProviderKind.GitHubRelease:
            {
                // 内核自动更新只提醒不自动换（协议兼容风险自己担不起；徽章驱动 + 日志）
                var channel = resource.Key == "kernel.singbox" ? CoreUpdater.SingBox : CoreUpdater.Xray;
                var latest = await CoreUpdater.CheckUpdateAsync(channel);
                await ExternalResourceService.MutateStateAsync(resource, s => s.LastCheckAt = DateTime.Now);
                if (latest != null)
                {
                    Log.Information("Kernel update available (auto-check, not applied): {Key} {Tag}", resource.Key, latest.tag_name);
                    ResourceUpdatable?.Invoke(resource.Key);
                }

                break;
            }

            case ExternalResourceProviderKind.HttpFile:
            {
                var (outcome, _, _) = await ExternalResourceService.UpdateHttpFileAsync(resource);
                if (outcome == ExternalResourceService.ResourceUpdateOutcome.Updated)
                {
                    Log.Information("External resource auto-updated: {Key}", resource.Key);
                    ResourceUpdated?.Invoke(resource.Key);
                }

                break;
            }
        }
    }
}
