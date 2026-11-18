using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch;
using Netch.Models;
using Netch.Utils;
using Netch.Views;

namespace Tests;

/// <summary>
///     订阅级节点解析：DNS wireformat 编解码 round-trip、Hosts 映射匹配、
///     订阅查找与解析入口的离线可测部分（真实 DoH 查询属冒烟，不入单测）
/// </summary>
[TestClass]
[DoNotParallelize]
public class ServerDnsResolverTests
{
    private Setting _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = Netch.Global.Settings;
        Netch.Global.Settings = new Setting();
    }

    [TestCleanup]
    public void Cleanup() => Netch.Global.Settings = _settings;

    #region wireformat

    [TestMethod]
    public void BuildQuery_HeaderAndQuestion()
    {
        var query = DoHDnsClient.BuildQuery("www.google.com", 1);

        // header 12 字节 + QNAME(3www6google3com0 = 16) + QTYPE/QCLASS(4)
        Assert.AreEqual(32, query.Length);
        Assert.AreEqual(0x01, query[2], "RD flag");
        Assert.AreEqual(0x00, query[3], "RCODE=0, opcode=QUERY");
        Assert.AreEqual(0, query[4]);
        Assert.AreEqual(1, query[5], "QDCOUNT=1");
        // QNAME 标签
        var expected = new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 6, (byte)'g', (byte)'o', (byte)'o', (byte)'g', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };
        Assert.IsTrue(query[12..28].SequenceEqual(expected), "QNAME labels");
        // QTYPE=A, QCLASS=IN
        Assert.AreEqual(0, query[28]);
        Assert.AreEqual(1, query[29]);
        Assert.AreEqual(0, query[30]);
        Assert.AreEqual(1, query[31]);
    }

    [TestMethod]
    public void ParseResponse_CnameThenA_Answer()
    {
        var message = BuildResponse("www.example.com", answers: new[]
        {
            (type: (ushort)5, rdata: EncodeName("example.com")), // CNAME，应被跳过
            (type: (ushort)1, rdata: new byte[] { 93, 184, 216, 34 })
        });

        var ip = DoHDnsClient.ParseResponse(message, 1);
        Assert.AreEqual(IPAddress.Parse("93.184.216.34"), ip);
    }

    [TestMethod]
    public void ParseResponse_CompressedPointerName_Aaaa()
    {
        // answer NAME 用压缩指针指向 question 区（offset 12），AAAA 记录
        var message = BuildResponse("a.example.com", answers: new[]
        {
            (type: (ushort)28, rdata: IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946").GetAddressBytes())
        }, compressAnswerName: true);

        var ip = DoHDnsClient.ParseResponse(message, 28);
        Assert.AreEqual(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), ip);
    }

    [TestMethod]
    [DataRow((ushort)3)]
    [DataRow((ushort)2)]
    public void ParseResponse_NonZeroRcode_Throws(ushort rcode)
    {
        var message = BuildResponse("x.com", answers: Array.Empty<(ushort, byte[])>(), rcode: rcode);
        Assert.ThrowsException<InvalidDataException>(() => DoHDnsClient.ParseResponse(message, 1));
    }

    [TestMethod]
    public void ParseResponse_Truncated_Throws()
    {
        Assert.ThrowsException<EndOfStreamException>(() => DoHDnsClient.ParseResponse(new byte[8], 1));
    }

    [TestMethod]
    public void ParseResponse_NoAnswer_ReturnsNull()
    {
        var message = BuildResponse("nx.example.com", answers: Array.Empty<(ushort, byte[])>());
        Assert.IsNull(DoHDnsClient.ParseResponse(message, 1));
    }

    /// <summary>构造 DNS 响应报文：header + 单 question + 若干 answer（NAME 恒为压缩指针或原样域名）</summary>
    private static byte[] BuildResponse(string qname, (ushort type, byte[] rdata)[] answers, bool compressAnswerName = false, ushort rcode = 0)
    {
        var stream = new MemoryStream();
        stream.WriteByte(0x12);
        stream.WriteByte(0x34); // ID
        stream.WriteByte(0x81);
        stream.WriteByte((byte)(0x80 | (rcode & 0x0F))); // QR=1 + RCODE
        WriteU16(stream, 1); // QDCOUNT
        WriteU16(stream, (ushort)answers.Length); // ANCOUNT
        WriteU16(stream, 0); // NSCOUNT
        WriteU16(stream, 0); // ARCOUNT

        // question 紧跟 header 起始于 offset 12（压缩指针指向处）
        var nameBytes = EncodeName(qname);
        const int questionOffset = 12;
        foreach (var b in nameBytes)
            stream.WriteByte(b);
        WriteU16(stream, 1); // QTYPE=A（占位，解析侧跳过）
        WriteU16(stream, 1); // QCLASS=IN

        foreach (var (type, rdata) in answers)
        {
            if (compressAnswerName)
            {
                stream.WriteByte((byte)(0xC0 | (questionOffset >> 8)));
                stream.WriteByte((byte)(questionOffset & 0xFF));
            }
            else
            {
                foreach (var b in nameBytes)
                    stream.WriteByte(b);
            }

            WriteU16(stream, type);
            WriteU16(stream, 1); // CLASS=IN
            WriteU32(stream, 300); // TTL（4 字节，写 2 字节会令后续字段错位）
            WriteU16(stream, (ushort)rdata.Length);
            stream.Write(rdata);
        }

        return stream.ToArray();
    }

    private static void WriteU16(MemoryStream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteU32(MemoryStream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    #endregion

    #region Hosts 匹配

    [TestMethod]
    public void MatchHosts_ExactCaseInsensitive()
    {
        var hosts = new Dictionary<string, string> { ["Api.Example.com"] = "target.example.net" };
        Assert.AreEqual("target.example.net", ServerDnsResolver.MatchHosts(hosts, "api.example.com"));
        Assert.AreEqual("target.example.net", ServerDnsResolver.MatchHosts(hosts, "API.EXAMPLE.COM."));
        Assert.IsNull(ServerDnsResolver.MatchHosts(hosts, "x.api.example.com"), "精确 key 不含子域");
    }

    [TestMethod]
    public void MatchHosts_WildcardSuffix()
    {
        var hosts = new Dictionary<string, string> { ["+.afdiancdn.org"] = "relay.portail-offres.top" };
        Assert.AreEqual("relay.portail-offres.top", ServerDnsResolver.MatchHosts(hosts, "afdiancdn.org"), "后缀自身命中");
        Assert.AreEqual("relay.portail-offres.top", ServerDnsResolver.MatchHosts(hosts, "055c165d-3142.afdiancdn.org"));
        Assert.AreEqual("relay.portail-offres.top", ServerDnsResolver.MatchHosts(hosts, "a.b.c.afdiancdn.org"), "多级子域命中");
        Assert.IsNull(ServerDnsResolver.MatchHosts(hosts, "afdiancdn.org.evil.com"), "后缀伪造不命中");
        Assert.IsNull(ServerDnsResolver.MatchHosts(hosts, "xafdiancdn.org"), "缺分隔点不命中");
    }

    [TestMethod]
    public void MatchHosts_Empty_ReturnsNull()
    {
        Assert.IsNull(ServerDnsResolver.MatchHosts(new Dictionary<string, string>(), "any.example.com"));
    }

    #endregion

    #region 订阅查找与解析入口（离线部分）

    [TestMethod]
    public void FindSubscription_Rules()
    {
        Netch.Global.Settings.Subscription.Add(new Subscription { Remark = "mesl", Link = "https://mesl.example.com/sub" });
        Netch.Global.Settings.Subscription.Add(new Subscription { Remark = "disabled", Link = "https://x.example.com", Enable = false });

        Assert.IsNull(ServerDnsResolver.FindSubscription(Constants.DefaultGroup), "手动节点组不查订阅");
        Assert.IsNull(ServerDnsResolver.FindSubscription("not-exist"));
        Assert.IsNull(ServerDnsResolver.FindSubscription("disabled"), "禁用订阅不生效");
        Assert.AreEqual("mesl", ServerDnsResolver.FindSubscription("mesl")!.Remark);
    }

    [TestMethod]
    public async Task ResolveAsync_HostsMappingToIpLiteral_ShortCircuits()
    {
        Netch.Global.Settings.Subscription.Add(new Subscription
        {
            Remark = "tag",
            Link = "https://tag.example.com/sub",
            Hosts = new Dictionary<string, string> { ["+.afdiancdn.org"] = "203.0.113.7" }
        });

        var server = new TestServer { Group = "tag", Hostname = "055c165d.afdiancdn.org" };
        var ip = await ServerDnsResolver.ResolveAsync(server);
        Assert.AreEqual(IPAddress.Parse("203.0.113.7"), ip, "映射值为 IP 字面量时直接返回，不发起任何解析");
    }

    [TestMethod]
    public async Task ResolveAsync_NoSubscription_SystemDnsPath()
    {
        // 无订阅节点：行为与历史一致（系统 DNS）；localhost 由 hosts 文件解析，离线可测
        var server = new TestServer { Group = Constants.DefaultGroup, Hostname = "localhost" };
        var ip = await ServerDnsResolver.ResolveAsync(server);
        Assert.IsNotNull(ip);
    }

    private sealed class TestServer : Server
    {
        public override string Type => "test";

        public override string MaskedData() => string.Empty;
    }

    #endregion

    #region 订阅对话框文本解析

    [TestMethod]
    public void ParseLines_TrimsDedupesAndSkipsEmpty()
    {
        var lines = SubscriptionDialog.ParseLines("  https://a.example/dns \n\nhttps://b.example/api-query\nhttps://a.example/dns  \n");
        CollectionAssert.AreEqual(new List<string> { "https://a.example/dns", "https://b.example/api-query" }, lines);
    }

    [TestMethod]
    public void ParseHosts_SyntaxRules()
    {
        var hosts = SubscriptionDialog.ParseHosts("# 注释行\na.example.com = b.example.net\n+.cdn.org=target.net\n\n非法行没有等号\n=x.equal.at.start\ntail.equal=\n");
        Assert.AreEqual(2, hosts.Count);
        Assert.AreEqual("b.example.net", hosts["a.example.com"], "等号两侧 trim");
        Assert.AreEqual("target.net", hosts["+.cdn.org"]);
        Assert.IsFalse(hosts.ContainsKey("非法行没有等号"), "非法行跳过");
        Assert.IsFalse(hosts.ContainsKey(""), "首尾等号的行跳过");
    }

    [TestMethod]
    public void ParseHosts_CaseFold()
    {
        var hosts = SubscriptionDialog.ParseHosts("A.example.com=first\na.EXAMPLE.com=second");
        Assert.AreEqual(1, hosts.Count);
        Assert.AreEqual("second", hosts["a.example.com"], "同 key 不同大小写后者覆盖（OrdinalIgnoreCase）");
    }

    #endregion
}
