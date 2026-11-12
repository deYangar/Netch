using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Netch.Models;
using Netch.Utils;

namespace Tests;

/// <summary>
///     ClashTrafficMonitor 分类与增量累计单测：合成 /connections 快照驱动，不依赖网络与内核进程
/// </summary>
[TestClass]
public class ClashTrafficMonitorTests
{
    [TestInitialize]
    public void SetUp()
    {
        ClashTrafficMonitor.Reset();
    }

    [TestMethod]
    public void Classify_DirectChain()
    {
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Direct, ClashTrafficMonitor.Classify(new List<string> { "direct" }));
    }

    [TestMethod]
    public void Classify_ProxyChain()
    {
        // 节点出站/selector 链一律算代理
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Proxy, ClashTrafficMonitor.Classify(new List<string> { "proxy" }));
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Proxy, ClashTrafficMonitor.Classify(new List<string> { "node-7", "proxy" }));
    }

    [TestMethod]
    public void Classify_IgnoredChains()
    {
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Ignored, ClashTrafficMonitor.Classify(new List<string> { "block" }));
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Ignored, ClashTrafficMonitor.Classify(new List<string> { "dns" }));
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Ignored, ClashTrafficMonitor.Classify(null));
        Assert.AreEqual(ClashTrafficMonitor.OutboundKind.Ignored, ClashTrafficMonitor.Classify(new List<string>()));
    }

    private static ClashConnection Conn(string id, List<string> chains, long up, long down)
    {
        return new ClashConnection
        {
            Id = id,
            Chains = chains,
            Upload = up,
            Download = down,
            Metadata = new ClashConnectionMetadata { Host = "example.com", DestinationIP = "1.2.3.4", DestinationPort = "443" }
        };
    }

    [TestMethod]
    public void Accumulate_FirstSightingCountsFullValue()
    {
        var snapshot = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection>
            {
                Conn("a", new List<string> { "proxy" }, 100, 2000),
                Conn("b", new List<string> { "direct" }, 10, 20)
            }
        };

        ClashTrafficMonitor.Accumulate(snapshot);

        var totals = ClashTrafficMonitor.Totals;
        Assert.AreEqual(100UL, totals.ProxyUp);
        Assert.AreEqual(2000UL, totals.ProxyDown);
        Assert.AreEqual(10UL, totals.DirectUp);
        Assert.AreEqual(20UL, totals.DirectDown);
    }

    [TestMethod]
    public void Accumulate_NextTickCountsDeltaOnly()
    {
        var first = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        };
        ClashTrafficMonitor.Accumulate(first);

        var second = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 250, 2600) }
        };
        ClashTrafficMonitor.Accumulate(second);

        var totals = ClashTrafficMonitor.Totals;
        Assert.AreEqual(250UL, totals.ProxyUp);
        Assert.AreEqual(2600UL, totals.ProxyDown);
        // 本次增量即速率
        Assert.AreEqual(150UL, ClashTrafficMonitor.Speeds.ProxyUp);
        Assert.AreEqual(600UL, ClashTrafficMonitor.Speeds.ProxyDown);
    }

    [TestMethod]
    public void Accumulate_ClosedConnectionEmitsEventAndHistory()
    {
        var closed = new List<ClashTrafficMonitor.ClosedConnection>();
        ClashTrafficMonitor.ConnectionClosed += entry => closed.Add(entry);

        var first = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        };
        ClashTrafficMonitor.Accumulate(first);

        // 连接消失：按最后一次观测值封顶入账，不再增长
        var second = new ClashConnectionsResponse { Connections = new List<ClashConnection>() };
        ClashTrafficMonitor.Accumulate(second);

        var totals = ClashTrafficMonitor.Totals;
        Assert.AreEqual(100UL, totals.ProxyUp);
        Assert.AreEqual(2000UL, totals.ProxyDown);
        Assert.AreEqual(1, closed.Count);
        Assert.AreEqual("a", closed[0].Connection.Id);
        Assert.AreEqual(1, ClashTrafficMonitor.HistorySnapshot().Count);
    }

    [TestMethod]
    public void Accumulate_PollFailureSkipped_NoDoubleCounting()
    {
        // 模拟轮询失败一轮（快照缺失）：直接构造「快照缺失后连接重新出现且计数已增长」的场景
        var first = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        };
        ClashTrafficMonitor.Accumulate(first);

        // 轮询失败时不调用 Accumulate；下一轮成功，计数继续增长
        var third = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 150, 2100) }
        };
        ClashTrafficMonitor.Accumulate(third);

        var totals = ClashTrafficMonitor.Totals;
        Assert.AreEqual(150UL, totals.ProxyUp);
        Assert.AreEqual(2100UL, totals.ProxyDown);
    }

    [TestMethod]
    public void Accumulate_CounterRegressionCountsZero()
    {
        var first = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        };
        ClashTrafficMonitor.Accumulate(first);

        // 计数回退（内核重启/复用 id）不产生负增量
        var second = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 50, 1800) }
        };
        ClashTrafficMonitor.Accumulate(second);

        var totals = ClashTrafficMonitor.Totals;
        Assert.AreEqual(100UL, totals.ProxyUp);
        Assert.AreEqual(2000UL, totals.ProxyDown);
    }

    [TestMethod]
    public void Accumulate_NullConnectionsListTreatedAsEmpty()
    {
        var first = new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        };
        ClashTrafficMonitor.Accumulate(first);

        // 内核无连接时 /connections 返回 null 列表
        var second = new ClashConnectionsResponse { Connections = null! };
        ClashTrafficMonitor.Accumulate(second);

        Assert.AreEqual(100UL, ClashTrafficMonitor.Totals.ProxyUp);
        Assert.AreEqual(1, ClashTrafficMonitor.HistorySnapshot().Count);
    }

    [TestMethod]
    public void CountKinds_SplitsProxyDirect_IgnoresBlockDns()
    {
        // 连接页汇总分账：block/dns/空链只计入总数、不计入两类（与流量分账同口径）
        var connections = new List<ClashConnection>
        {
            Conn("p1", new List<string> { "proxy" }, 0, 0),
            Conn("p2", new List<string> { "node-7", "proxy" }, 0, 0),
            Conn("d1", new List<string> { "direct" }, 0, 0),
            Conn("b1", new List<string> { "block" }, 0, 0),
            Conn("n1", new List<string>(), 0, 0),
            new ClashConnection { Id = "", Chains = new List<string> { "direct" } } // 空 Id 跳过
        };

        var (proxy, direct) = ClashTrafficMonitor.CountKinds(connections);

        Assert.AreEqual(2, proxy);
        Assert.AreEqual(1, direct);
    }

    [TestMethod]
    public void CountKinds_NullReturnsZero()
    {
        var (proxy, direct) = ClashTrafficMonitor.CountKinds(null);
        Assert.AreEqual(0, proxy);
        Assert.AreEqual(0, direct);
    }

    [TestMethod]
    public void ClosedConnection_CarriesGeoText()
    {
        ClashTrafficMonitor.Accumulate(new ClashConnectionsResponse
        {
            Connections = new List<ClashConnection> { Conn("a", new List<string> { "proxy" }, 100, 2000) }
        });
        var closed = ClashTrafficMonitor.Accumulate(new ClashConnectionsResponse { Connections = new List<ClashConnection>() });

        // 首轮入表 → 快照消失即关闭；GeoText 由 Accumulate 分发前填充（库缺失=空串，不抛错）
        Assert.AreEqual(1, closed.Count);
        Assert.AreEqual("1.2.3.4:443", closed[0].RemoteText);
        Assert.IsNotNull(closed[0].GeoText);
    }
}

/// <summary>
///     GeoLookup 目标解析单测（纯字符串解析，不触库；库路径缺失环境 Lookup 返回空串不抛错）
/// </summary>
[TestClass]
public class GeoLookupTests
{
    [TestMethod]
    public void TryExtractIp_V4Port_V6Port_BareIp_Domain()
    {
        Assert.AreEqual(true, GeoLookup.TryExtractIp("1.2.3.4:443", out var v4));
        Assert.AreEqual("1.2.3.4", v4);

        Assert.AreEqual(true, GeoLookup.TryExtractIp("[2606:4700::6810:84e5]:443", out var v6));
        Assert.AreEqual("2606:4700::6810:84e5", v6);

        Assert.AreEqual(true, GeoLookup.TryExtractIp("2606:4700::6810:84e5", out var bare));
        Assert.AreEqual("2606:4700::6810:84e5", bare);

        Assert.AreEqual(false, GeoLookup.TryExtractIp("example.com", out _));
        Assert.AreEqual(false, GeoLookup.TryExtractIp("", out _));
    }

    [TestMethod]
    public void Lookup_ReservedAddress_NeverThrows()
    {
        // TEST-NET-3 保留段：无库=空串，有库=空串或 PRIVATE（GeoLite2 私网语义）——任何环境只要求不抛错、非 null
        GeoLookup.ResetCache();
        Assert.IsNotNull(GeoLookup.Lookup("203.0.113.7:443"));
    }

    [TestMethod]
    public void Lookup_DomainWithoutIp_ReturnsEmpty()
    {
        // 纯域名无 IP 成分：不做 DNS 解析（连接页 1Hz 不可联网查），任何环境一律空串
        GeoLookup.ResetCache();
        Assert.AreEqual("", GeoLookup.Lookup("example.com"));
    }
}
