using System.Net;
using System.Text;
using Microsoft.VisualStudio.Threading;
using Netch.Controllers;
using Netch.Enums;

namespace Netch.Utils;

public static class WebUtil
{
    public const string DefaultUserAgent =
        @"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.61 Safari/537.36 Edg/94.0.992.31";

    private static int DefaultGetTimeout => Global.Settings.RequestTimeout;

    /// <summary>
    ///     订阅/更新请求是否应经运行中的 mixed 入站（HTTP 代理语义直接可用，无需另开入站）
    /// </summary>
    public static bool ShouldUseProxy
    {
        get
        {
            try
            {
                // UIHub 未装配时（极早调用）抛 NRE，由 catch 返回 false，与旧可空读取的 null 语义一致
                return Global.Settings.SubscriptionUseProxy &&
                       Global.UIHub.State == State.Started &&
                       MainController.Socks5Server != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    ///     运行中的本地代理（未运行/关闭开关时返回 null = 保持系统默认代理行为）
    /// </summary>
    public static IWebProxy? RunningProxy()
    {
        if (!ShouldUseProxy)
            return null;

        var server = MainController.Socks5Server!;
        return new WebProxy($"http://{server.Hostname}:{server.Port}");
    }

    /// <summary>
    ///     内置代理地址（内核 mixed 入站，HTTP 代理语义直接可用）；内核未运行返回 null。
    ///     不依赖订阅开关——外部资源经内置代理下载的独立通道
    /// </summary>
    public static string? BuiltinProxyUrl()
    {
        try
        {
            if (Global.UIHub.State != State.Started || MainController.Socks5Server == null)
                return null;

            var server = MainController.Socks5Server;
            return $"http://{server.Hostname}:{server.Port}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     请求描述（HttpClient 迁移后取代 HttpWebRequest 的可变请求面：URL/超时/UA/代理策略）。
    ///     ProxyUrl 优先级最高（订阅经远端代理拉取）；否则 UseProxy=false 直连、true 时用运行中本地代理或系统代理。
    /// </summary>
    public sealed class WebReq
    {
        public required string Url { get; init; }

        public int Timeout { get; init; }

        public string UserAgent { get; set; } = DefaultUserAgent;

        public bool UseProxy { get; init; } = true;

        /// <summary>显式代理 URL；null = 按 UseProxy 语义（本地运行代理 / 系统代理 / 直连）</summary>
        public string? ProxyUrl { get; set; }
    }

    /// <param name="useProxy">false 强制直连（代理失败后的一次性回退）；true 时若代理在运行则走代理</param>
    public static WebReq CreateRequest(string url, int? timeout = null, string? userAgent = null, bool useProxy = true, string? proxyUrl = null)
    {
        return new WebReq
        {
            Url = url,
            Timeout = timeout ?? DefaultGetTimeout,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent,
            UseProxy = useProxy,
            ProxyUrl = proxyUrl
        };
    }

    /// <summary>
    ///     外部资源统一请求（R 线）：开关开且内置代理在跑 → 显式走内置代理；
    ///     开关关或内核未运行 → 直连（不落系统代理，语义即「经内置代理下载」）
    /// </summary>
    public static WebReq CreateResourceRequest(string url, int? timeout = null)
    {
        var proxyUrl = Global.Settings.ExternalResources.ViaProxy ? BuiltinProxyUrl() : null;
        return CreateRequest(url, timeout, useProxy: proxyUrl != null, proxyUrl: proxyUrl);
    }

    /// <summary>
    ///     每请求构建 HttpClient（低频调用：订阅拉取/更新检查/内核下载，无连接池复用诉求）。
    ///     Timeout 只作用于响应头阶段（等价旧 req.Timeout）；正文不限总时长，防大文件下载被整体超时截断。
    /// </summary>
    private static HttpClient BuildClient(WebReq req)
    {
        var handler = new HttpClientHandler();
        if (!req.UseProxy)
            handler.UseProxy = false; // 直连（等价旧 req.Proxy = null）
        else if (req.ProxyUrl != null)
            handler.Proxy = new WebProxy(req.ProxyUrl);
        else if (RunningProxy() is { } proxy)
            handler.Proxy = proxy;
        // 其余情况：UseProxy=true 且 Proxy=null → 系统代理（等价旧 HttpWebRequest 默认行为）

        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, WebReq req)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, req.Url);
        msg.Headers.TryAddWithoutValidation("User-Agent", req.UserAgent);
        msg.Headers.TryAddWithoutValidation("Accept", "*/*");
        msg.Headers.TryAddWithoutValidation("Accept-Charset", "utf-8");

        using var cts = new CancellationTokenSource(req.Timeout);
        var response = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode;
            response.Dispose();
            // 非 2xx 抛异常，等价旧 HttpWebRequest.GetResponseAsync 的 WebException 语义（调用方一律按异常处理失败）
            throw new HttpRequestException($"Response status code does not indicate success: {(int)code} ({code}).");
        }

        return response;
    }

    public static async Task<byte[]> DownloadBytesAsync(WebReq req)
    {
        using var client = BuildClient(req);
        using var response = await SendAsync(client, req);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    public static async Task<(HttpStatusCode, string)> DownloadStringAsync(WebReq req, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        using var client = BuildClient(req);
        using var response = await SendAsync(client, req);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var streamReader = new StreamReader(stream, encoding);

        return (response.StatusCode, await streamReader.ReadToEndAsync());
    }

    public static Task DownloadFileAsync(string address, string fileFullPath, IProgress<int>? progress = null)
    {
        return DownloadFileAsync(CreateRequest(address), fileFullPath, progress);
    }

    /// <summary>HEAD 探测结果（ExternalResourceService 指纹合成用；任一字段可为 null）</summary>
    public sealed record HeadProbe(string? ETag, string? LastModified, long? ContentLength);

    /// <summary>
    ///     HEAD 请求取响应头指纹（ETag / Last-Modified / Content-Length）。
    ///     GitHub release 静态资产 302 后落到 CDN，ETag 为对象指纹稳定可比；raw.githubusercontent 对 HEAD 回 ETag。
    ///     非 2xx / 网络失败上抛，语义与 Get 一致。
    /// </summary>
    public static async Task<HeadProbe> ProbeHeadersAsync(WebReq req)
    {
        using var client = BuildClient(req);
        using var msg = new HttpRequestMessage(HttpMethod.Head, req.Url);
        msg.Headers.TryAddWithoutValidation("User-Agent", req.UserAgent);

        using var cts = new CancellationTokenSource(req.Timeout);
        using var response = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).");

        string? etag = response.Headers.ETag?.Tag;
        if (etag is { Length: > 0 })
            etag = etag.Trim('"');

        var lastModified = response.Content.Headers.LastModified?.ToString("R");
        var length = response.Content.Headers.ContentLength;

        if (etag == null && lastModified == null && length == null)
            return new HeadProbe(null, null, null);

        return new HeadProbe(etag, lastModified, length);
    }

    public static async Task DownloadFileAsync(WebReq req, string fileFullPath, IProgress<int>? progress)
    {
        var fileStream = new FileStream(fileFullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using (fileStream)
        {
            using var client = BuildClient(req);
            using var response = await SendAsync(client, req);
            await using var input = await response.Content.ReadAsStreamAsync();
            using var downloadTask = input.CopyToAsync(fileStream);
            if (progress != null)
                ReportProgressAsync(response.Content.Headers.ContentLength ?? -1, downloadTask, fileStream, progress, 200).Forget();

            await downloadTask;
        }

        progress?.Report(100);
    }

    private static async Task ReportProgressAsync(long total, Task downloadTask, Stream stream, IProgress<int> progress, int interval)
    {
        var n = 0;
        while (!downloadTask.IsCompleted)
        {
            // 低危③（2026-11-04 审查）：响应无 Content-Length 时 total=-1，除以 -1 会报出负百分比；
            // 未知总量不报进度（进度条停在起点，优于误导性的负数）
            if (total > 0)
            {
                var n1 = (int)((double)stream.Length / total * 100);
                if (n != n1)
                {
                    n = n1;
                    progress.Report(n);
                }
            }

            await Task.Delay(interval);
        }
    }
}
