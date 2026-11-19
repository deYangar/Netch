using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Netch.Utils;

/// <summary>
///     DNS-over-HTTPS 客户端（RFC 8484）：POST application/dns-message，
///     支持任意 DoH URL（含自定义路径，如 https://host:port/api-query）。
///     直连查询（解析节点域名时代理尚未启动，与 mihomo proxy-server-nameserver 直连语义一致）。
///     缓存独立于 DnsUtils 的系统 DNS 缓存，key 掺 DoH URL——同一域名在系统/不同 DoH 解析器间不串结果。
///     失败负缓存（2026-11-19 42s 启动事故）：DoH 被服务器限速/不可达时失败结果同样缓存（短 TTL），
///     同一 DoH 对同一域名短期内不重试——多节点启动曾把失败放大成节点数×DoH 数连击，且连击本身持续触发限速。
/// </summary>
public static class DoHDnsClient
{
    private const int CacheTtlSeconds = 60;
    private const int NegativeCacheTtlSeconds = 30;
    private const ushort QTypeA = 1;
    private const ushort QTypeAAAA = 28;

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // 直连：不设代理（节点域名解析失败往往就是没有可用代理的时刻）；
        // 连接超时独立于整体超时，避免把超时预算耗在 TCP 建连上
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    /// <summary>Address 为 null 表示负缓存（该 URL 对该域名解析失败，TTL 内不再重试）</summary>
    private sealed record CacheEntry(IPAddress? Address, DateTime SavedAt)
    {
        public bool Alive(DateTime now)
        {
            var ttl = Address == null ? NegativeCacheTtlSeconds : CacheTtlSeconds;
            return (now - SavedAt).TotalSeconds <= ttl;
        }
    }

    /// <summary>
    ///     用指定 DoH 上游解析域名。失败（网络/格式/RCODE）返回 null，不抛异常（对齐 DnsUtils.LookupAsync 语义）
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(string dohUrl, string hostname, AddressFamily inet = AddressFamily.Unspecified)
    {
        var host = hostname.TrimEnd('.');
        var key = CacheKey(dohUrl, host, inet);
        try
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Alive(DateTime.UtcNow))
                return cached.Address;

            var result = await ResolveNoCacheAsync(dohUrl, host, inet);

            // 成功与失败都写缓存：失败负缓存防止同域名重复烧失败路径（见类注释）
            Cache[key] = new CacheEntry(result, DateTime.UtcNow);

            return result;
        }
        catch (Exception e)
        {
            // 异常（网络不可达/超时）与快拒绝同语义：负缓存
            Cache[key] = new CacheEntry(null, DateTime.UtcNow);
            Log.Verbose(e, "DoH resolve {Hostname} via {Url} failed", hostname, dohUrl);
            return null;
        }
    }

    private static async Task<IPAddress?> ResolveNoCacheAsync(string dohUrl, string hostname, AddressFamily inet)
    {
        // Unspecified 先 A 后 AAAA（prefer_ipv4 偏好，与 DnsUtils 系统解析的 FirstOrDefault 语义对齐）
        if (inet == AddressFamily.Unspecified)
        {
            return await QueryAsync(dohUrl, hostname, QTypeA)
                   ?? await QueryAsync(dohUrl, hostname, QTypeAAAA);
        }

        return await QueryAsync(dohUrl, hostname, inet == AddressFamily.InterNetworkV6 ? QTypeAAAA : QTypeA);
    }

    private static async Task<IPAddress?> QueryAsync(string dohUrl, string hostname, ushort qType)
    {
        using var response = await Http.PostAsync(dohUrl, new ByteArrayContent(BuildQuery(hostname, qType)));
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsByteArrayAsync();
        return ParseResponse(body, qType);
    }

    private static string CacheKey(string dohUrl, string hostname, AddressFamily inet)
    {
        return $"{dohUrl}|{inet}|{hostname}";
    }

    /// <summary>清空 DoH 解析缓存（自愈链路/DNS 设置变更时与 DnsUtils.ClearCache 同步清）</summary>
    public static void ClearCache() => Cache.Clear();

    #region DNS wireformat（internal 纯函数，供单测 round-trip）

    /// <summary>
    ///     构造 DNS 查询报文：header（RD=1，QDCOUNT=1）+ 单条 question
    /// </summary>
    internal static byte[] BuildQuery(string hostname, ushort qType)
    {
        var question = new MemoryStream();
        foreach (var label in hostname.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            question.WriteByte((byte)bytes.Length);
            question.Write(bytes);
        }

        question.WriteByte(0);
        question.WriteByte((byte)(qType >> 8));
        question.WriteByte((byte)qType);
        // QCLASS = IN
        question.WriteByte(0);
        question.WriteByte(1);

        var query = new MemoryStream();
        foreach (var b in new byte[]
                 {
                     // Transaction ID（响应原样带回，不校验——单并发场景下串扰无来源）
                     (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256),
                     // Flags: RD=1
                     0x01, 0x00,
                     // QDCOUNT = 1, AN/NS/ARCOUNT = 0
                     0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                 })
            query.WriteByte(b);
        question.Position = 0;
        question.CopyTo(query);

        return query.ToArray();
    }

    /// <summary>
    ///     解析 DNS 响应：跳过 question 区，按序读 answer，返回首个匹配 qType 的 A/AAAA 地址；
    ///     CNAME 等其他记录跳过。RCODE 非 0 或报文残缺抛 InvalidDataException
    /// </summary>
    internal static IPAddress? ParseResponse(byte[] message, ushort qType)
    {
        if (message.Length < 12)
            throw new EndOfStreamException("DNS response truncated (header)");

        var rCode = message[3] & 0x0F;
        if (rCode != 0)
            throw new InvalidDataException($"DNS response RCODE={rCode}");

        var qdCount = ReadU16(message, 4);
        var anCount = ReadU16(message, 6);

        var pos = 12;
        for (var i = 0; i < qdCount; i++)
        {
            ReadDomain(message, ref pos); // QNAME
            pos += 4; // QTYPE + QCLASS
        }

        for (var i = 0; i < anCount; i++)
        {
            ReadDomain(message, ref pos); // NAME（含压缩指针）
            var type = ReadU16(message, pos);
            pos += 8; // TYPE + CLASS + TTL
            var rdLength = ReadU16(message, pos);
            pos += 2;
            if (pos + rdLength > message.Length)
                throw new EndOfStreamException("DNS response truncated (answer)");

            if (type == qType && rdLength == 4 && qType == QTypeA)
                return new IPAddress(message[pos..(pos + 4)]);

            if (type == qType && rdLength == 16 && qType == QTypeAAAA)
                return new IPAddress(message[pos..(pos + 16)]);

            pos += rdLength;
        }

        return null;
    }

    private static ushort ReadU16(byte[] message, int pos)
    {
        return (ushort)((message[pos] << 8) | message[pos + 1]);
    }

    /// <summary>
    ///     读取域名（处理 0xC0 压缩指针；跳转上限防御环路报文）
    /// </summary>
    private static string ReadDomain(byte[] message, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var resumePos = 0;
        for (var guard = 0; guard < 126; guard++)
        {
            if (pos >= message.Length)
                throw new EndOfStreamException("DNS response truncated (name)");

            var len = message[pos];
            if (len == 0)
            {
                pos++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= message.Length)
                    throw new EndOfStreamException("DNS response truncated (pointer)");

                if (!jumped)
                {
                    resumePos = pos + 2;
                    jumped = true;
                }

                pos = ((len & 0x3F) << 8) | message[pos + 1];
                continue;
            }

            if (pos + 1 + len > message.Length)
                throw new EndOfStreamException("DNS response truncated (label)");

            labels.Add(Encoding.ASCII.GetString(message, pos + 1, len));
            pos += 1 + len;
        }

        if (jumped)
            pos = resumePos;

        return string.Join('.', labels);
    }

    #endregion
}
