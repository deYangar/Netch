using System.Net;

namespace Netch.Utils;

/// <summary>
///     连接页 IP 归属地查询：IPv4/IPv6 统一走 ip2region xdb（国家/省/市/ISP，Ip2Region 类；
///     v4 bin\ip2region.xdb + v6 bin\ip2region_v6.xdb，咩咩 2026-10-30 拍板 country.mmdb 退役全量切 ip2region）。
///     单例只读 + 按 IP 缓存——1Hz 连接表批量查询不重复开库/重复解析；
///     库在运行期被外部资源调度器替换后旧数据继续生效（全内存副本），重启后生效新库，可接受。
///     库缺失/解析失败返回空串（列留白），不抛错不上日志（连接页每秒调用，异常噪音不可入日志）。
/// </summary>
public static class GeoLookup
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>缓存上限（超大离峰防膨胀：不同 IP 的连接持续刷新时缓存按计数清空重热）</summary>
    private const int CacheCapacity = 8192;

    /// <summary>
    ///     目标文本（ip:port / [v6]:port / 裸 IP / 域名）→ 归属地显示名。
    ///     无 IP 成分（纯域名，进程页 DNS 未解析目标）返回空串，不做 DNS 解析（1Hz 轮询不可联网查）
    /// </summary>
    public static string Lookup(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !TryExtractIp(target, out var address))
            return "";

        lock (Lock)
        {
            if (Cache.TryGetValue(address, out var cached))
                return cached;
        }

        // address 来自 TryExtractIp（已通过 IPAddress 解析），此处 TryParse 恒成功
        var name = IPAddress.TryParse(address, out var parsed)
            ? Ip2Region.Lookup(parsed)
            : "";

        lock (Lock)
        {
            if (Cache.Count >= CacheCapacity)
                Cache.Clear();
            Cache[address] = name;
        }

        return name;
    }

    /// <summary>清缓存（单测隔离与库更换后强制重查）</summary>
    internal static void ResetCache()
    {
        lock (Lock)
            Cache.Clear();

        Ip2Region.Reset();
    }

    /// <summary>ip:port / [v6]:port / 裸 v4/v6 → IPAddress；域名与解析失败返回 false</summary>
    internal static bool TryExtractIp(string target, out string address)
    {
        address = "";
        var text = target.Trim().TrimStart('[');

        if (IPAddress.TryParse(text, out var ip))
        {
            address = text;
            return true;
        }

        // ip:port（v4）或 [v6]:port（上文 TrimStart 后仍带尾 :port）
        var colon = text.LastIndexOf(':');
        if (colon == -1)
            return false;

        var hostPart = text[..colon].TrimEnd(']');
        if (!IPAddress.TryParse(hostPart, out ip))
            return false;

        address = hostPart;
        return true;
    }
}
