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
                servers = ShareLink.ParseText(result);
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

        if (!string.IsNullOrEmpty(item.UserAgent))
            request.UserAgent = item.UserAgent;

        if (!string.IsNullOrEmpty(proxyServer))
            request.ProxyUrl = proxyServer;

        return WebUtil.DownloadStringAsync(request);
    }
}