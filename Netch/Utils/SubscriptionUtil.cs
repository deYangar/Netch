using System.Net;
using Netch.Models;
using Netch.Localization;

namespace Netch.Utils;

public static class SubscriptionUtil
{
    private static readonly object ServerLock = new();

    /// <param name="only">只更新该订阅（订阅页单个更新命令）；null = 全部启用订阅</param>
    public static Task UpdateServersAsync(string? proxyServer = default, Subscription? only = null)
    {
        var targets = only == null
            ? Global.Settings.Subscription
            : new List<Subscription> { only };
        return Task.WhenAll(targets.Select(item => UpdateServerCoreAsync(item, proxyServer)));
    }

    private static async Task UpdateServerCoreAsync(Subscription item, string? proxyServer)
    {
        try
        {
            if (!item.Enable)
                return;

            List<Server> servers;

            var (code, result) = await FetchAsync(item, proxyServer);
            if (code == HttpStatusCode.OK)
            {
                if (ClashConfigParser.TryParse(result, out var clash))
                {
                    servers = clash.Servers;
                    ApplyClashDnsHosts(item, clash);
                }
                else
                {
                    servers = ShareLink.ParseText(result);
                }
            }
            else
                throw new Exception($"{item.Remark} Response Status Code: {code}");

            foreach (var server in servers)
                server.Group = item.Remark;

            lock (ServerLock)
            {
                Global.Settings.Server.RemoveAll(server => server.Group.Equals(item.Remark));
                Global.Settings.Server.AddRange(servers);
            }

            Global.UIHub.NotifyTip(I18N.TF("subscription.update1ServerSFrom", item.Remark, servers.Count));
        }
        catch (Exception e)
        {
            Global.UIHub.NotifyTip($"{I18N.TF("subscription.updateServersFailedFrom0", item.Remark)}\n{e.Message}", info: false);
            Log.Warning(e, "Update servers failed");
        }
    }

    /// <summary>
    ///     Clash 订阅自带 dns/hosts 整体接管（咩咩 2026-11-18 拍板覆盖语义：订阅是事实源，有则覆盖、无则清空——
    ///     机场换域名映射后跟随，手填值对 Clash 订阅不保留）。变化才提示，避免每次更新都弹；
    ///     字段随外层 UpdateServersAsync 之后的 Configuration.SaveAsync 一并持久化
    /// </summary>
    private static void ApplyClashDnsHosts(Subscription item, ClashConfigParser.Result clash)
    {
        var dnsChanged = !item.NodeDns.SequenceEqual(clash.NodeDns);
        var hostsChanged = !item.Hosts.Keys.SequenceEqual(clash.Hosts.Keys) || item.Hosts.Any(kv => clash.Hosts.GetValueOrDefault(kv.Key) != kv.Value);
        if (!dnsChanged && !hostsChanged)
            return;

        item.NodeDns = clash.NodeDns.ToList();
        item.Hosts = clash.Hosts.ToDictionary(kv => kv.Key, kv => kv.Value);
        Global.UIHub.NotifyTip(I18N.TF("subscription.clashDnsHostsApplied", item.Remark, clash.NodeDns.Count, clash.Hosts.Count));
    }

    /// <summary>
    ///     顺序「代理（若运行）→ 直连」，各一次：墙内订阅地址直连常失败，
    ///     但代理本身故障时也不能让订阅永远拉不到
    /// </summary>
    private static async Task<(HttpStatusCode, string)> FetchAsync(Subscription item, string? proxyServer)
    {
        var useProxy = !string.IsNullOrEmpty(proxyServer) || WebUtil.ShouldUseProxy;

        if (!useProxy)
            return await SendAsync(item, null, false);

        try
        {
            return await SendAsync(item, proxyServer, true);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Subscription {Remark} fetch via proxy failed, retrying direct", item.Remark);
            return await SendAsync(item, null, false);
        }
    }

    private static Task<(HttpStatusCode, string)> SendAsync(Subscription item, string? proxyServer, bool useProxy)
    {
        var request = WebUtil.CreateRequest(item.Link, useProxy: useProxy);

        // 订阅 UA 三级：显式自定义 > SubscriptionDefaultUserAgent（clash 系，换完整 YAML+新协议）；
        // 不能落 CreateRequest 的浏览器默认——订阅后端按 UA 分流，浏览器 UA 只给 base64 节点列表
        request.UserAgent = string.IsNullOrEmpty(item.UserAgent) ? WebUtil.SubscriptionDefaultUserAgent : item.UserAgent;

        if (!string.IsNullOrEmpty(proxyServer))
            request.ProxyUrl = proxyServer;

        return WebUtil.DownloadStringAsync(request);
    }
}