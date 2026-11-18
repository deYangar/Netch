using System.Net;
using System.Net.Sockets;
using Netch.Models;

namespace Netch.Utils;

/// <summary>
///     订阅级节点域名服务：Hosts 映射改写（订阅更新时应用，Sparkle js 覆写改 proxies[].server 的等价语义）
///     与节点解析 DoH（NodeDns 逐个尝试，全部失败回退系统 DNS）。
///     sing-box/Xray 出站地址与 GUI 测速/启动预检共用解析链路（内核收到的恒为 IP，两内核行为一致）。
///     未分组（DefaultGroup）或订阅未配置时行为与历史一致（系统 DNS、无映射）。
/// </summary>
public static class ServerDnsResolver
{
    /// <summary>
    ///     订阅更新落库前应用 Hosts 映射：命中即改写节点 Hostname——连接目标与 TLS SNI 一并跟随目标
    ///     （Netch 的 SNI = Host 覆写 ?? Hostname，与 mihomo 的 servername ?? server 同构，
    ///     故本改写与 js 覆写只改 proxies[].server 的行为完全对齐）。幂等：每次订阅更新重新映射
    /// </summary>
    public static void ApplyHostsToServers(Subscription subscription, IEnumerable<Server> servers)
    {
        if (subscription.Hosts is not { Count: > 0 })
            return;

        foreach (var server in servers)
        {
            var mapped = MatchHosts(subscription.Hosts, server.Hostname);
            if (mapped != null)
                server.Hostname = mapped;
        }
    }

    /// <summary>
    ///     解析节点连接地址，返回 null 表示全链路失败（调用方显式报错，不 NRE）
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(Server server, AddressFamily inet = AddressFamily.Unspecified)
    {
        // IP 字面量（订阅直给 IP / Hosts 映射值为 IP）：无需任何解析
        if (IPAddress.TryParse(server.Hostname, out var literal))
            return literal;

        var subscription = FindSubscription(server.Group);
        if (subscription?.NodeDns is { Count: > 0 })
        {
            foreach (var url in subscription.NodeDns)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var ip = await DoHDnsClient.ResolveAsync(url.Trim(), server.Hostname, inet);
                if (ip != null)
                    return ip;
            }
            // DoH 全部失败回退系统 DNS（可用性优先：上游临时不可达不该让节点直接死掉）
        }

        return await DnsUtils.LookupAsync(server.Hostname, inet);
    }

    /// <summary>
    ///     按组名找启用中的订阅；组名即订阅 Remark（SubscriptionUtil 更新时赋值），手动节点组（NONE）不匹配
    /// </summary>
    internal static Subscription? FindSubscription(string group)
    {
        if (group == Constants.DefaultGroup)
            return null;

        return Global.Settings.Subscription.FirstOrDefault(s => s.Enable && s.Remark == group);
    }

    /// <summary>
    ///     Hosts 匹配：精确 key 或 "+.suffix" 泛域名（suffix 自身及任意子域，mihomo 语义），大小写不敏感；
    ///     其他 key 形态按精确处理。未命中返回 null
    /// </summary>
    internal static string? MatchHosts(IReadOnlyDictionary<string, string> hosts, string hostname)
    {
        var name = hostname.TrimEnd('.');

        foreach (var (key, value) in hosts)
        {
            if (key.StartsWith("+.", StringComparison.Ordinal))
            {
                var suffix = key[2..];
                if (name.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || (name.Length > suffix.Length + 1 && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name[^(suffix.Length + 1)] == '.'))
                    return value;
            }
            else if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
