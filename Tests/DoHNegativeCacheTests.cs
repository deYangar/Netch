using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Tests;

/// <summary>
///     DoH 失败负缓存（2026-11-19 42s 启动事故治本）：
///     本地假 DoH 服务器（127.0.0.1，HttpListener）可控响应 + 请求计数，
///     断言失败结果被负缓存、同域名短期内不再重试、按域名隔离、成功后正缓存接管；
///     以及配置生成预解析目标的收集语义（去重/IP 跳过/Hosts 映射后目标）
/// </summary>
[TestClass]
[DoNotParallelize]
public class DoHNegativeCacheTests
{
    private Setting _settings = null!;
    private FakeDohServer _server = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = Netch.Global.Settings;
        Netch.Global.Settings = new Setting();
        DoHDnsClient.ClearCache();
        _server = new FakeDohServer();
        _server.Start();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _server.Dispose();
        DoHDnsClient.ClearCache();
        Netch.Global.Settings = _settings;
    }

    #region 负缓存行为（本地假 DoH）

    [TestMethod]
    public async Task NegativeCache_Refused_SecondCallHitsCache()
    {
        _server.Mode = FakeDohServer.ResponseMode.Refused;

        var first = await DoHDnsClient.ResolveAsync(_server.Url, "refused.example.com");
        var second = await DoHDnsClient.ResolveAsync(_server.Url, "refused.example.com");

        Assert.IsNull(first, "REFUSED 解析失败返回 null");
        Assert.IsNull(second, "负缓存命中同样返回 null");
        Assert.AreEqual(1, _server.Requests, "第二次调用命中负缓存，不再发真实请求");
    }

    [TestMethod]
    public async Task NegativeCache_PerHostname_Isolated()
    {
        _server.Mode = FakeDohServer.ResponseMode.Refused;

        await DoHDnsClient.ResolveAsync(_server.Url, "a.example.com");
        await DoHDnsClient.ResolveAsync(_server.Url, "b.example.com");

        Assert.AreEqual(2, _server.Requests, "负缓存按域名隔离，不同域名各自真实请求");
    }

    [TestMethod]
    public async Task NegativeCache_PerDohUrl_Isolated()
    {
        _server.Mode = FakeDohServer.ResponseMode.Refused;

        await DoHDnsClient.ResolveAsync(_server.Url, "shared.example.com");
        await DoHDnsClient.ResolveAsync(_server.Url + "-alt", "shared.example.com");

        // 第二个 URL 不存在（连接失败），同样负缓存——两次都是真实尝试
        Assert.AreEqual(2, _server.Requests, "缓存 key 掺 DoH URL，不同 URL 不串负缓存");
    }

    [TestMethod]
    public async Task NegativeCache_ThenSuccess_PositiveCacheTakesOver()
    {
        _server.Mode = FakeDohServer.ResponseMode.Refused;
        Assert.IsNull(await DoHDnsClient.ResolveAsync(_server.Url, "flip.example.com"));
        Assert.AreEqual(1, _server.Requests);

        // 服务器恢复（清负缓存模拟 TTL 过期）
        DoHDnsClient.ClearCache();
        _server.Mode = FakeDohServer.ResponseMode.Ok;

        var recovered = await DoHDnsClient.ResolveAsync(_server.Url, "flip.example.com");
        Assert.AreEqual(IPAddress.Parse("203.0.113.7"), recovered, "恢复后成功解析");
        Assert.AreEqual(2, _server.Requests);

        var cachedAgain = await DoHDnsClient.ResolveAsync(_server.Url, "flip.example.com");
        Assert.AreEqual(IPAddress.Parse("203.0.113.7"), cachedAgain, "成功结果正缓存命中");
        Assert.AreEqual(2, _server.Requests, "正缓存命中不再发请求");
    }

    [TestMethod]
    public async Task ResolveHostnameAsync_DohRefused_FallsBackToSystemDns()
    {
        // 全链路：订阅 NodeDns 全拒（限速态）→ 系统 DNS 兜底（localhost 离线可解析）
        Netch.Global.Settings.Subscription.Add(new Subscription
        {
            Remark = "mesl",
            Link = "https://mesl.example.com/sub",
            NodeDns = new List<string> { _server.Url }
        });
        _server.Mode = FakeDohServer.ResponseMode.Refused;

        var ip = await ServerDnsResolver.ResolveHostnameAsync("mesl", "localhost");

        Assert.IsTrue(ip != null && IPAddress.IsLoopback(ip), $"DoH 全拒后系统 DNS 兜底成功（实际 {ip}）");
        Assert.AreEqual(1, _server.Requests, "该域名仅真实请求一次（负缓存语义下不再重复打向被限速服务器）");
    }

    #endregion

    #region 预解析目标收集（离线）

    [TestMethod]
    public void CollectResolveTargets_DedupSkipsIpLiteralAndAppliesHosts()
    {
        Netch.Global.Settings.Subscription.Add(new Subscription
        {
            Remark = "TAG",
            Link = "https://tag.example.com/sub",
            Hosts = new Dictionary<string, string> { ["mapped.example.org"] = "203.0.113.9" }
        });

        var servers = new List<Server>
        {
            new TestServer { Group = "TAG", Hostname = "a.entry.example.com" },
            new TestServer { Group = "TAG", Hostname = "a.entry.example.com" }, // 同组同域名去重
            new TestServer { Group = "TAG", Hostname = "mapped.example.org" }, // Hosts 命中 → IP 字面量跳过
            new TestServer { Group = Constants.DefaultGroup, Hostname = "203.0.113.1" }, // IP 字面量跳过
            new TestServer { Group = Constants.DefaultGroup, Hostname = "b.entry.example.com" } // 不同组不去重
        };

        var targets = SingboxConfigUtils.CollectResolveTargets(servers);

        Assert.AreEqual(2, targets.Count, "去重 + IP 跳过后仅 2 个待解析目标");
        Assert.IsTrue(targets.Contains(("TAG", "a.entry.example.com")));
        Assert.IsTrue(targets.Contains((Constants.DefaultGroup, "b.entry.example.com")));
    }

    [TestMethod]
    public void CollectResolveTargets_HostsMappedDomain_ResolvesMappedTarget()
    {
        Netch.Global.Settings.Subscription.Add(new Subscription
        {
            Remark = "TAG",
            Link = "https://tag.example.com/sub",
            Hosts = new Dictionary<string, string> { ["+.cdn.example.org"] = "relay.example.net" }
        });

        var targets = SingboxConfigUtils.CollectResolveTargets(new List<Server>
        {
            new TestServer { Group = "TAG", Hostname = "x.cdn.example.org" }
        });

        // 预解析目标 = 映射后的目标（与循环体 CloneWithHosts 语义一致）
        Assert.AreEqual(1, targets.Count);
        Assert.AreEqual(("TAG", "relay.example.net"), targets[0]);
    }

    #endregion

    private sealed class TestServer : Server
    {
        public override string Type => "test";

        public override string MaskedData() => string.Empty;
    }

    /// <summary>
    ///     本地假 DoH：POST 请求计数，按 Mode 返回 REFUSED（RCODE=5）或单 A 记录（203.0.113.7）。
    ///     响应由请求报文原样改写（保留 question 回显），无需自行编码 QNAME
    /// </summary>
    private sealed class FakeDohServer : IDisposable
    {
        public enum ResponseMode
        {
            Refused,
            Ok
        }

        private readonly HttpListener _listener = new();
        private CancellationTokenSource _cts = new();
        private Task? _loop;
        private string _prefix = null!;

        public volatile ResponseMode Mode = ResponseMode.Refused;

        public int Requests;

        public string Url => _prefix + "dns";

        public void Start()
        {
            // TcpListener(0) 借一个空闲端口给 HttpListener（微小竞态窗口，本机测试可接受）
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                _prefix = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/";
                probe.Stop();
            }

            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener closed
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var request = ctx.Request;
                        var response = ctx.Response;
                        using var body = new MemoryStream();
                        await request.InputStream.CopyToAsync(body);
                        var query = body.ToArray();
                        Interlocked.Increment(ref Requests);

                        var answer = Mode == ResponseMode.Ok
                            ? BuildOkResponse(query)
                            : BuildRefusedResponse(query);

                        response.StatusCode = 200;
                        response.ContentType = "application/dns-message";
                        await response.OutputStream.WriteAsync(answer);
                        response.Close();
                    }
                    catch (Exception)
                    {
                        // ignored：单个请求失败不影响测试主线
                    }
                });
            }
        }

        private static byte[] BuildRefusedResponse(byte[] query)
        {
            var resp = (byte[])query.Clone();
            resp[2] = 0x81; // QR=1 RD=1
            resp[3] = 0x85; // RCODE=5 REFUSED
            return resp;
        }

        private static byte[] BuildOkResponse(byte[] query)
        {
            var resp = (byte[])query.Clone();
            resp[2] = 0x81;
            resp[3] = 0x80; // RCODE=0
            resp[6] = 0x00;
            resp[7] = 0x01; // ANCOUNT=1

            // answer：NAME=压缩指针(question offset 12) + A + IN + TTL + RDLENGTH=4 + 203.0.113.7
            using var answer = new MemoryStream();
            answer.WriteByte(0xC0);
            answer.WriteByte(0x0C);
            answer.WriteByte(0x00);
            answer.WriteByte(0x01); // TYPE=A
            answer.WriteByte(0x00);
            answer.WriteByte(0x01); // CLASS=IN
            answer.WriteByte(0x00);
            answer.WriteByte(0x00);
            answer.WriteByte(0x00);
            answer.WriteByte(0x3C); // TTL=60
            answer.WriteByte(0x00);
            answer.WriteByte(0x04); // RDLENGTH
            answer.WriteByte(203);
            answer.WriteByte(0);
            answer.WriteByte(113);
            answer.WriteByte(7);

            var result = new byte[resp.Length + (int)answer.Length];
            resp.CopyTo(result, 0);
            answer.ToArray().CopyTo(result, resp.Length);
            return result;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Close();
            }
            catch (Exception)
            {
                // ignored
            }

            _cts.Dispose();
        }
    }
}
