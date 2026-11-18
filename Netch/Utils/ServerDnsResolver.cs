using System.Net;
using System.Net.Sockets;
using Netch.Models;

namespace Netch.Utils;

/// <summary>
///     订阅级节点域名服务：Hosts 映射与节点解析 DoH。
///     两个消费时机（与 Sparkle js 覆写同构——订阅原文永远保留原始域名，每次生成运行时配置时应用映射）：
///     ①连接链路：两内核配置生成入口对节点克隆体改写 Hostname（连接目标与 TLS SNI 一并跟随目标，
///       js 覆写改 proxies[].server 的等价语义）；改映射停→启即生效，无需重新拉取订阅；
///     ②解析链路（测速/启动预检/TUN 防回环）：ResolveAsync 按映射后的目标解析，与实际连接一致。
///     订阅 NodeDns 非空时 DoH 逐个尝试，全部失败回退系统 DNS；
///     未分组（DefaultGroup）或订阅未配置时行为与历史一致（系统 DNS、无映射）。
/// </summary>
public static class ServerDnsResolver
{
    /// <summary>
    ///     配置生成入口用：克隆节点并应用 Hosts 映射（命中即改写克隆体 Hostname）。
    ///     克隆体进内核配置（原件保留原始域名，映射随时可改可撤）；tagMap 等回填场景仍持原件
    /// </summary>
    public static Server CloneWithHosts(Server server)
    {
        var node = (Server)server.Clone();

        var subscription = FindSubscription(node.Group);
        if (subscription?.Hosts is { Count: > 0 })
        {
            var mapped = MatchHosts(subscription.Hosts, node.Hostname);
            if (mapped != null)
                node.Hostname = mapped;
        }

        return node;
    }

    /// <summary>
    ///     解析节点连接地址（测速/预检链路：按映射后的目标解析，与实际连接一致），
    ///     返回 null 表示全链路失败（调用方显式报错，不 NRE）
    /// </summary>
    public static Task<IPAddress?> ResolveAsync(Server server, AddressFamily inet = AddressFamily.Unspecified)
    {
        return ResolveHostnameAsync(server.Group, server.Hostname, inet);
    }

    /// <summary>
    ///     按订阅上下文解析一个域名（链式 socks5 的 RemoteHostname 等无 Server 实例的场景同用）：
    ///     Hosts 命中 → 解析目标（目标为 IP 字面量直接返回）→ 订阅 DoH → 系统 DNS 兜底
    /// </summary>
    public static async Task<IPAddress?> ResolveHostnameAsync(string group, string hostname, AddressFamily inet = AddressFamily.Unspecified)
    {
        var subscription = FindSubscription(group);

        var target = hostname;
        if (subscription?.Hosts is { Count: > 0 })
        {
            var mapped = MatchHosts(subscription.Hosts, hostname);
            if (mapped != null)
                target = mapped;
        }

        // 映射值直接是 IP 字面量（或订阅直给 IP 节点）：无需解析
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
