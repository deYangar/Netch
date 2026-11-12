using System.Collections;
using System.Net;
using System.Net.Sockets;

namespace Netch.Utils;

public static class DnsUtils
{
    /// <summary>
    ///     缓存条目：值 + 写入时间（TTL 过期即 miss，网络切换/断网恢复后旧解析自然失效）
    /// </summary>
    private sealed record CacheEntry(IPAddress Address, DateTime SavedAt);

    /// <summary>
    ///     缓存过期秒数：CDN/多 A 记录场景解析结果会随网络环境变化，
    ///     永久缓存会让断网恢复的「重新解析」拿到旧 IP，自愈链路连旧 IP 必败（2026-11-04 审查 F-1）
    /// </summary>
    private const int CacheTtlSeconds = 60;

    /// <summary>
    ///     缓存
    /// </summary>
    private static readonly Hashtable Cache = new();
    private static readonly Hashtable Cache6 = new();

    /// <summary>缓存写锁：Hashtable 单写多读（读线程安全），仅写路径串行；查询本身不再全局排队（P-4）</summary>
    private static readonly object WriteLock = new();

    public static async Task<IPAddress?> LookupAsync(string hostname, AddressFamily inet = AddressFamily.Unspecified, int timeout = 3000)
    {
        try
        {
            if (TryGetCache(hostname, inet, out var cached))
                return cached;

            return await LookupNoCacheAsync(hostname, inet, timeout);
        }
        catch (Exception e)
        {
            Log.Verbose(e, "Lookup hostname {Hostname} failed", hostname);
            return null;
        }
    }

    private static bool TryGetCache(string hostname, AddressFamily inet, out IPAddress? address)
    {
        var entry = inet switch
        {
            AddressFamily.Unspecified => (Cache[hostname] ?? Cache6[hostname]) as CacheEntry,
            AddressFamily.InterNetwork => Cache[hostname] as CacheEntry,
            AddressFamily.InterNetworkV6 => Cache6[hostname] as CacheEntry,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (entry != null && (DateTime.UtcNow - entry.SavedAt).TotalSeconds <= CacheTtlSeconds)
        {
            address = entry.Address;
            return true;
        }

        address = null;
        return false;
    }

    private static async Task<IPAddress?> LookupNoCacheAsync(string hostname, AddressFamily inet = AddressFamily.Unspecified, int timeout = 3000)
    {
        using var task = Dns.GetHostAddressesAsync(hostname);
        using var resTask = await Task.WhenAny(task, Task.Delay(timeout));

        if (resTask == task)
        {
            var addresses = await task;

            var result = addresses.FirstOrDefault(i => inet == AddressFamily.Unspecified || inet == i.AddressFamily);
            if (result == null)
                return null;

            switch (result.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    WriteCache(Cache, hostname, result);
                    break;
                case AddressFamily.InterNetworkV6:
                    WriteCache(Cache6, hostname, result);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return result;
        }

        return null;
    }

    /// <summary>缓存写入（覆盖式：过期重查后更新时间戳；Hashtable 写路径须串行）</summary>
    private static void WriteCache(Hashtable table, string hostname, IPAddress address)
    {
        lock (WriteLock)
            table[hostname] = new CacheEntry(address, DateTime.UtcNow);
    }

    public static void ClearCache()
    {
        lock (WriteLock)
        {
            Cache.Clear();
            Cache6.Clear();
        }
    }

    public static string AppendPort(string host, ushort port = 53)
    {
        if (!host.Contains(':'))
            return host + $":{port}";

        return host;
    }

    /// <summary>
    ///     拆分 host:port 地址；无端口部分用默认端口。含多个冒号（IPv6 字面量）时整体视为主机名
    /// </summary>
    public static (string Host, ushort Port) SplitHostPort(string address, ushort defaultPort)
    {
        var idx = address.LastIndexOf(':');
        if (idx > 0 && !address[..idx].Contains(':') && ushort.TryParse(address[(idx + 1)..], out var port))
            return (address[..idx], port);

        return (address, defaultPort);
    }
}