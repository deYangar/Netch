using System.Net;
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

    /// <summary>同一入口机的最大并发（拥塞实测：503 节点 16 并发混测时同入口机 RTT 抬高一倍+，TAG 香港 01 独测 250/混测 538）</summary>
    public const int PerEndpointConcurrency = 3;

    /// <summary>跨入口机的全局并发上限（防解析失败等边角场景组数爆炸失控）</summary>
    private const int TotalConcurrency = 24;

    /// <summary>解析节点的并发（纯 DNS 查询，轻量）</summary>
    private const int ResolveConcurrency = 16;

    /// <summary>
    ///     并行解析一批节点的实际连接 IP（分组限流公共积木，主实例成员/临时实例两路共用）；
    ///     解析失败的节点不在返回值里（调用方自行标 -2）
    /// </summary>
    public static async Task<Dictionary<Server, IPAddress>> ResolveEndpointsAsync(IEnumerable<Server> servers)
    {
        var resolved = new Dictionary<Server, IPAddress>();
        using var resolvePool = new AsyncSemaphore(ResolveConcurrency);
        await Task.WhenAll(servers.Select(async s =>
        {
            using var __ = await resolvePool.EnterAsync();
            IPAddress? ip = null;
            try
            {
                ip = await ServerDnsResolver.ResolveAsync(s);
            }
            catch (Exception)
            {
                // 解析异常同失败语义
            }

            if (ip != null)
            {
                lock (resolved)
                {
                    resolved[s] = ip;
                }
            }
        }));

        return resolved;
    }

    /// <summary>
    ///     实测一批节点：起临时实例（全部节点做成出站）→ 分组限流经出站访问测试地址 → 停。
    ///     分组限流（治拥塞，2026-11-19 咩咩拍板「要最准」）：节点先解析到实际连接 IP（与内核连接同路径，
    ///     含订阅 Hosts 映射），同一入口机最多 <see cref="PerEndpointConcurrency"/> 并发、跨入口机并行——
    ///     拥塞来自同入口机持续并发，跨机互不影响。解析失败节点记 -2（与 PingAsync 同码）。
    ///     结果经 SetDelay 回填（DelaySource.ClashApi 实测语义）；节点经出站测失败记 -4（与运行态成员失败同码）。
    ///     起失败返回 false（调用方显式标 -5），不静默回落握手测速。
    /// </summary>
    /// <param name="onResult">每节点测完即回调（UI 逐个刷新进度），可空</param>
    public static async Task<bool> TestAsync(IReadOnlyList<Server> servers, Action<Server>? onResult = null)
    {
        if (servers.Count == 0)
            return true;

        using var _ = await Lock.EnterAsync();
        await StopCoreAsync();

        // ① 并行解析实际连接 IP（缓存供分组）；失败节点标 -2 不进测速
        var resolved = await ResolveEndpointsAsync(servers);
        foreach (var s in servers.Where(s => !resolved.ContainsKey(s)))
        {
            s.SetDelay(-2);
            onResult?.Invoke(s);
        }

        _current = new SpeedtestSingboxController();
        bool started;
        try
        {
            started = await _current.StartAsync(resolved.Keys.ToList());
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

            // ② 按入口 IP 分组（实例内跳过的不可表达节点自然不在 tagMap）
            var endpointGroups = new Dictionary<string, List<KeyValuePair<string, Server>>>();
            foreach (var kv in _current.OutboundTagMap)
            {
                var endpoint = resolved[kv.Value].ToString();
                if (!endpointGroups.TryGetValue(endpoint, out var group))
                    endpointGroups[endpoint] = group = new List<KeyValuePair<string, Server>>();
                group.Add(kv);
            }

            // ③ 双层限流实测：组内 3 并发（同入口机防拥塞）+ 全局 24（防失控）
            using var total = new AsyncSemaphore(TotalConcurrency);
            var groupLocks = endpointGroups.Values.ToDictionary(g => g, _ => new AsyncSemaphore(PerEndpointConcurrency));
            await Task.WhenAll(groupLocks.Select(pair => Task.WhenAll(pair.Key.Select(async entry =>
            {
                using var _group = await pair.Value.EnterAsync();
                using var _total = await total.EnterAsync();
                var delay = await ClashApiClient.GetProxyDelayAsync(port, entry.Key);
                entry.Value.SetDelay(delay ?? -4);
                onResult?.Invoke(entry.Value);
            }))));

            return true;
        }
        finally
        {
            await StopCoreAsync();
        }
    }

    /// <summary>单节点采样次数（奇数取中位数，抗单次瞬时抖动；起实例 ~2s，多两次采样无感）</summary>
    private const int SingleSampleCount = 3;

    /// <summary>
    ///     单节点实测（等待态双击行/运行态非成员）：同节点连测 3 次取中位数（要最准，2026-11-19 咩咩拍板）——
    ///     单次请求有 ±15% 瞬时抖动（两轮独测 250/273/310 的波动），中位数最抗毛刺。
    ///     实例起失败返回 null（调用方标 -5）；3 次全失败回 -4；部分失败取成功样本的中位数
    /// </summary>
    public static async Task<int?> TestSingleAsync(Server server)
    {
        IPAddress? ip;
        try
        {
            ip = await ServerDnsResolver.ResolveAsync(server);
        }
        catch (Exception)
        {
            ip = null;
        }

        if (ip == null)
            return -2;

        using var _ = await Lock.EnterAsync();
        await StopCoreAsync();

        _current = new SpeedtestSingboxController();
        bool started;
        try
        {
            started = await _current.StartAsync(new[] { server });
        }
        catch (Exception e)
        {
            Log.Error(e, "Speedtest instance failed to start, see logging\\sing-box-speedtest.log");
            started = false;
        }

        if (!started)
        {
            _current = null;
            return null;
        }

        try
        {
            var port = _current.ClashApiPort!.Value;
            var tag = _current.OutboundTagMap.First(kv => ReferenceEquals(kv.Value, server)).Key;

            var samples = new List<int>();
            for (var i = 0; i < SingleSampleCount; i++)
            {
                var delay = await ClashApiClient.GetProxyDelayAsync(port, tag);
                if (delay != null)
                    samples.Add(delay.Value);
            }

            if (samples.Count == 0)
                return -4;

            samples.Sort();
            return samples[samples.Count / 2];
        }
        finally
        {
            await StopCoreAsync();
        }
    }
}
