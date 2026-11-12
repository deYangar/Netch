using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.VisualStudio.Threading;
using Netch.Controllers;
using Netch.Enums;
using Netch.UI.Hub;

namespace Netch.Utils;

public static class Bandwidth
{
    public static ulong received;
    public static TraceEventSession? tSession;

    private static readonly string[] Suffix = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };

    /// <summary>
    ///     计算流量
    /// </summary>
    /// <param name="d"></param>
    /// <returns>带单位的流量字符串</returns>
    public static string Compute(ulong d)
    {
        const double step = 1024.00;

        byte level = 0;
        double? size = null;
        while ((size ?? d) > step)
        {
            if (level >= 6) // Suffix.Length - 1
                break;

            level++;
            size = (size ?? d) / step;
        }

        return $@"{size ?? 0:0.##} {Suffix[level]}";
    }

    /// <summary>
    ///     紧凑流量文本（876B / 12.3K / 4.56M / 1.23G）：状态栏空间有限，速率用短单位无空格格式
    /// </summary>
    public static string ComputeShort(ulong d)
    {
        const double step = 1024.00;

        double size = d;
        byte level = 0;
        while (size > step && level < 5)
        {
            level++;
            size /= step;
        }

        var unit = level == 0 ? "B" : "KMGTPE"[level - 1].ToString();
        var number = size >= 100 ? "F0" : size >= 10 ? "F1" : "F2";
        return $@"{size.ToString(number)}{unit}";
    }

    private static readonly object CounterLock = new();

    /// <summary>ETW 会话/上报循环各自只建一次（重入=确保在跑；F-5 恢复路径会再次调用 NetTraffic）</summary>
    private static bool _sessionRunning;

    private static bool _reportRunning;

    /// <summary>被观测 pid 集合（P-3：动态刷新——内核重启后新 pid 不在启动快照里，旧实现统计冻结归零）</summary>
    private static volatile HashSet<int> WatchPids = new();

    private static DateTime _lastPidRefresh = DateTime.MinValue;

    /// <summary>
    ///     根据程序名统计流量。可重入：会话与上报循环单例（跑着就直接返回），
    ///     每次调用刷新一次观测 pid 集合；未知 pid 的事件触发节流刷新自动跟上重启后的新内核
    /// </summary>
    public static void NetTraffic()
    {
        if (!Flags.IsWindows10Upper)
            return;

        var watch = RefreshWatchPids();
        Log.Information("Net traffic processes: {Pids}", string.Join(',', watch.Select(p => $"#{p}")));

        received = 0;

        if (watch.Count == 0)
            return;

        UIEvents.RaiseTrafficSourceStarted(true);

        if (!_sessionRunning)
        {
            _sessionRunning = true;
            Task.Run(() =>
                {
                    tSession = new TraceEventSession("KernelAndClrEventsSession");
                    tSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                    //这玩意儿上传和下载得到的data是一样的:)
                    //所以暂时没办法区分上传下载流量
                    tSession.Source.Kernel.TcpIpRecv += data => OnRecv(data.ProcessID, data.size);
                    tSession.Source.Kernel.UdpIpRecv += data => OnRecv(data.ProcessID, data.size);

                    tSession.Source.Process();
                })
                .Forget();
        }

        if (_reportRunning)
            return;

        _reportRunning = true;
        Task.Run(async () =>
        {
            while (_reportRunning && Global.UIHub.State != State.Stopped)
            {
                await Task.Delay(1000);
                lock (CounterLock)
                    UIEvents.RaiseEtwTrafficUpdated(received);
            }

            _reportRunning = false;
        }).Forget();
    }

    private static void OnRecv(int pid, int size)
    {
        if (WatchPids.Contains(pid))
        {
            lock (CounterLock)
                received += (ulong)size;
            return;
        }

        // 未知 pid：可能是重启后的新内核进程——节流刷新观测集（P-3，1s 一次防高频事件刷爆）
        if ((DateTime.UtcNow - _lastPidRefresh).TotalSeconds >= 1)
        {
            _lastPidRefresh = DateTime.UtcNow;
            var fresh = RefreshWatchPids();
            if (fresh.Count > 0)
                WatchPids = fresh;

            if (WatchPids.Contains(pid))
                lock (CounterLock)
                    received += (ulong)size;
        }
    }

    /// <summary>当前应被观测的 pid 集：内核进程（Guard）或 NF/TUN 模式下的宿主进程</summary>
    private static HashSet<int> RefreshWatchPids()
    {
        var set = new HashSet<int>();

        switch (MainController.ServerController)
        {
            case Guard guard:
                set.Add(guard.Instance.Id);
                break;
            case null:
                switch (MainController.ModeController)
                {
                    case NFController or TUNController:
                        set.Add(Environment.ProcessId);
                        break;
                    case Guard modeGuard:
                        set.Add(modeGuard.Instance.Id);
                        break;
                }

                break;
        }

        return set;
    }

    public static void Stop()
    {
        tSession?.Dispose();
        tSession = null;
        _sessionRunning = false;
        received = 0;
    }
}