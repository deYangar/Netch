using System.Net.Http.Json;
using System.Text.Json;
using Netch.Servers;
using Netch.Models;

namespace Netch.Utils;

/// <summary>
///     sing-box experimental.clash_api 客户端（仅本机回环）。
///     当前用途：经节点出站的真实延迟测试（GET /proxies/{tag}/delay），
///     替代裸 TCPing/ICMPing（后者只测本机到节点服务器的连通性）
/// </summary>
public static class ClashApiClient
{
    // P-6④（2026-11-04 审查）：禁系统代理——目标永远 127.0.0.1 回环，第三方代理软件劫持 loopback
    // 请求时测速/连接表会整体失败（此时本地内核根本不经系统代理）
    private static readonly HttpClient HttpClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string DelayTestUrl = "https://www.gstatic.com/generate_204";

    /// <summary>
    ///     经指定出站访问测试地址的真实延迟
    /// </summary>
    /// <returns>延迟毫秒；出站不可用/超时/接口异常返回 null</returns>
    public static async Task<int?> GetProxyDelayAsync(ushort port, string tag)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/proxies/{Uri.EscapeDataString(tag)}/delay?timeout=5000&url={Uri.EscapeDataString(DelayTestUrl)}";

            using var response = await HttpClient.GetAsync(url);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (response.IsSuccessStatusCode && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var ms))
                return ms;

            Log.Warning("Clash API delay test failed for {Tag}: {Body}", tag, json.ToString());
            return null;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Clash API delay test failed for {Tag}", tag);
            return null;
        }
    }

    /// <summary>
    ///     连接表快照（GET /connections）：嗅探域名/规则命中/出站链/流量。
    ///     端口轮询失败返回 null（内核未运行/端点缺失），调用方据此显示空态而非刷异常
    /// </summary>
    public static async Task<ClashConnectionsResponse?> GetConnectionsAsync(ushort port)
    {
        try
        {
            return await HttpClient.GetFromJsonAsync<ClashConnectionsResponse>($"http://127.0.0.1:{port}/connections");
        }
        catch (Exception)
        {
            // 轮询路径的常规失败（内核退出间隙），不进日志防刷屏
            return null;
        }
    }
}
