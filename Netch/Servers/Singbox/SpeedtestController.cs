using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using Netch.Controllers;
using Netch.Models;
using Netch.Utils;

namespace Netch.Servers;

/// <summary>
///     临时测速实例（真实测速，2026-11-19 咩咩拍板）：等待态/运行态非成员节点经节点出站真实访问
///     测试地址取延迟，替代只能测到入口的握手测速。与主实例并存三隔离：
///     日志 logging\sing-box-speedtest.log（Guard 按名开独占句柄）、配置 data\speedtest.json、
///     Clash API 随机回环端口。实例退出事件无人订阅，意外退出仅记日志不触发重启编排。
/// </summary>
public class SpeedtestSingboxController : SingboxController
{
    /// <summary>独立日志名（Guard 日志路径 = logging\{Name}.log，与主实例 sing-box.log 隔离防抢句柄）</summary>
    public override string Name => "sing-box-speedtest";

    /// <summary>测速专用配置（与主实例 last.json 互不覆盖；命令行相对 bin 工作目录）</summary>
    private const string RelativeConfigPath = "data\\speedtest.json";

    /// <summary>
    ///     生成多节点配置（全节点出站 + Clash API）并起进程，就绪以 Clash API 应答为准
    /// </summary>
    /// <returns>起失败返回 false（调用方如实标失败码，不回落握手测速遮掩）</returns>
    public async Task<bool> StartAsync(IReadOnlyList<Server> servers)
    {
        var port = PortHelper.GetAvailablePort(PortType.TCP);
        var (config, tagMap) = await SingboxConfigUtils.GenerateClientConfigAsync(servers, servers[0], port);

        StripForSpeedtest(config);

        var fullPath = Path.Combine(Global.NetchDir, RelativeConfigPath);
        await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, true))
        {
            await JsonSerializer.SerializeAsync(fileStream, config, Global.NewCustomJsonSerializerOptions());
        }

        ClashApiPort = port;
        OutboundTagMap = tagMap;

        await StartGuardAsync($"run -c ..\\{RelativeConfigPath}");

        // sing-box warn 级日志无 started 行（见基类注释），真实就绪以 Clash API 应答为准
        if (!await ClashApiClient.GetVersionAsync(port, TimeSpan.FromSeconds(10)))
        {
            await StopAsync();
            return false;
        }

        return true;
    }

    /// <summary>
    ///     测速专用配置后处理（不动日用配置生成路径），三件事：
    ///     ① 清空入站——无本地流量经它，还占全局 Socks5 端口（与主实例冲突）；
    ///     ② 摘除 urltest(auto)——它启动即自发全节点测速，与 Clash API delay 并发 = 连接风暴双份；
    ///     ③ selector.Default 指向 auto 时改指首个节点出站（引用已摘除的 tag 会被 sing-box 校验拒绝）
    /// </summary>
    internal static void StripForSpeedtest(SingboxConfig config)
    {
        config.Inbounds.Clear();

        config.Outbounds?.RemoveAll(o => o is SingboxUrltestOutbound);
        foreach (var selector in config.Outbounds?.OfType<SingboxSelectorOutbound>() ?? Enumerable.Empty<SingboxSelectorOutbound>())
        {
            selector.Outbounds.RemoveAll(tag => tag == "auto");
            if (selector.Default == "auto")
                selector.Default = selector.Outbounds.FirstOrDefault() ?? "direct";
        }
    }
}

/// <summary>
///     临时测速实例编排（静态单例）：一次一个实例，起 → 16 并发逐 tag 实测 → 停；
///     MainController.StartAsync 起正式实例前先停当前实例（<see cref="StopAsync"/>），互不干扰。
/// </summary>
public static class SpeedtestController
{
    private static readonly AsyncSemaphore Lock = new(1);

    private static SpeedtestSingboxController? _current;

    /// <summary>停当前临时实例（若有）。幂等，未在跑直接返回</summary>
    public static async Task StopAsync()
    {
        using var _ = await Lock.EnterAsync();
        await StopCoreAsync();
    }

    private static async Task StopCoreAsync()
    {
        if (_current == null)
            return;

        try
        {
            await _current.StopAsync();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Stop speedtest instance failed");
        }

        _current = null;
    }

    /// <summary>
    ///     实测一批节点：起临时实例（全部节点做成出站）→ 16 并发经出站访问测试地址 → 停。
    ///     结果经 SetDelay 回填（DelaySource.ClashApi 实测语义）；单节点失败记 -4（与运行态成员失败同码）。
    ///     起失败返回 false（调用方显式标 -5），不静默回落握手测速。
    /// </summary>
    /// <param name="onResult">每节点测完即回调（UI 逐个刷新进度），可空</param>
    public static async Task<bool> TestAsync(IReadOnlyList<Server> servers, Action<Server>? onResult = null)
    {
        if (servers.Count == 0)
            return true;

        using var _ = await Lock.EnterAsync();
        await StopCoreAsync();

        _current = new SpeedtestSingboxController();
        bool started;
        try
        {
            started = await _current.StartAsync(servers);
        }
        catch (Exception e)
        {
            // 配置生成失败（节点协议不可表达等）/进程启动失败统一按起失败处理，日志留因
            Log.Error(e, "Speedtest instance failed to start, see logging\\sing-box-speedtest.log");
            started = false;
        }

        if (!started)
        {
            _current = null;
            return false;
        }

        try
        {
            var port = _current.ClashApiPort!.Value;
            using var pool = new AsyncSemaphore(16);
            await Task.WhenAll(_current.OutboundTagMap.Select(async kv =>
            {
                using var __ = await pool.EnterAsync();
                var delay = await ClashApiClient.GetProxyDelayAsync(port, kv.Key);
                kv.Value.SetDelay(delay ?? -4);
                onResult?.Invoke(kv.Value);
            }));

            return true;
        }
        finally
        {
            await StopCoreAsync();
        }
    }

    /// <summary>
    ///     单节点实测（等待态双击行/运行态非成员）：
    ///     实例起失败返回 null（调用方标 -5），节点经出站测失败回 -4
    /// </summary>
    public static async Task<int?> TestSingleAsync(Server server)
    {
        int? delay = -4;
        var ok = await TestAsync(new[] { server }, s => delay = s.Delay);
        return ok ? delay : null;
    }
}
