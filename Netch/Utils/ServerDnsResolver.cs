using System.Net;
using System.Net.Sockets;
using Netch.Models;

namespace Netch.Utils;

/// <summary>
///     节点域名解析统一入口：按节点所属订阅（Group = 订阅 Remark）应用两项订阅级配置——
///     Hosts 映射（源域名 → 目标域名/IP：连接地址换目标解析结果，TLS SNI / HTTP Host 保持原域名，mihomo hosts 语义）
///     与节点解析 DoH（NodeDns 逐个尝试，全部失败回退系统 DNS）。
///     sing-box/Xray 出站地址与 GUI 测速/启动预检共用此链路（内核收到的恒为 IP，两内核行为一致）。
///     未分组（DefaultGroup）或订阅未配置时行为与历史一致（系统 DNS、无映射）。
/// </summary>
public static class ServerDnsResolver
{
    /// <summary>
    ///     解析节点连接地址，返回 null 表示全链路失败（调用方显式报错，不 NRE）
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(Server server, AddressFamily inet = AddressFamily.Unspecified)
    {
        var subscription = FindSubscription(server.Group);

        var target = server.Hostname;
        if (subscription?.Hosts is { Count: > 0 })
        {
            var mapped = MatchHosts(subscription.Hosts, server.Hostname);
            if (mapped != null)
                target = mapped;
        }

        // 映射值直接是 IP 字面量：无需解析
        if (IPAddress.TryParse(target, out var literal))
            return literal;

        if (subscription?.NodeDns is { Count: > 0 })
        {
            foreach (var url in subscription.NodeDns)
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var ip = await DoHDnsClient.ResolveAsync(url.Trim(), target, inet);
                if (ip != null)
                    return ip;
            }
            // DoH 全部失败回退系统 DNS（可用性优先：上游临时不可达不该让节点直接死掉）
        }

        return await DnsUtils.LookupAsync(target, inet);
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
