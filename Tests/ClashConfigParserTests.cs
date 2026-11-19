using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Tests;

/// <summary>
///     Clash/mihomo YAML 订阅解析：全类型字段映射（对照 asdlokj1qpi233/subconverter explodeClash 语义）、
///     dns 段 DoH 提取与 hosts 段提取边界。大样例为 TAGMeta 真实订阅脱敏版（234 节点/30 hosts 映射拓扑保留）
/// </summary>
[TestClass]
[DoNotParallelize]
public class ClashConfigParserTests
{
    private static string ReadSanitizedTagMeta()
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "clash_tagmeta_sanitized.yaml"));
    }

    private static string ReadSanitizedMesl()
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "clash_mesl_sanitized.yaml"));
    }

    [TestMethod]
    public void Parse_MeslSanitized_AnytlsNodesAndDnsOnly()
    {
        // mesl 真实订阅脱敏版（2026-11-19，默认 UA 切 clash.meta 后拿到的完整 YAML 形态）：
        // 172 个 anytls 节点；dns 段只有 nameserver（无 proxy-server-nameserver、无 hosts 段）——
        // 「nameserver 即节点 DNS 来源」的形态正是该订阅想要的提取路径
        var ok = ClashConfigParser.TryParse(ReadSanitizedMesl(), out var result);

        Assert.IsTrue(ok, "mesl 应识别为 Clash 配置");
        Assert.AreEqual(172, result.Servers.Count, "172 个 anytls 节点全部解析");
        Assert.IsTrue(result.Servers.All(s => s.Type == "AnyTLS"), "全部节点为 AnyTLS");
        Assert.IsTrue(result.Servers.All(s => s.Port > 0 && !string.IsNullOrWhiteSpace(s.Hostname)), "全部节点地址端口完整");

        // 首节点锚点：server/sni 三地区域名、凭据、alpn 指纹
        var first = (AnytlsServer)result.Servers[0];
        Assert.AreEqual("hkg-server.example.test", first.Hostname);
        Assert.AreEqual((ushort)8443, first.Port);
        Assert.AreEqual("sanitized-pass", first.Password);
        Assert.AreEqual("sni-zgdgs.example.test", first.Host);

        // sni 每节点唯一（172 个互不相同）
        Assert.AreEqual(172, result.Servers.Select(s => ((AnytlsServer)s).Host).Distinct().Count(), "sni 全局唯一");

        // dns：无 proxy-server-nameserver，仅 nameserver 的 2 条 DoH 提取（带端口与路径形态）
        CollectionAssert.AreEqual(
            new[] { "https://doh-a.example.test:39933/api-query", "https://doh-b.example.test/api-query" },
            result.NodeDns);

        // 无 hosts 段
        Assert.AreEqual(0, result.Hosts.Count);
    }

    [TestMethod]
    public void Parse_TagMetaSanitized_AllNodesAndDnsHosts()
    {
        var ok = ClashConfigParser.TryParse(ReadSanitizedTagMeta(), out var result);

        Assert.IsTrue(ok, "TAGMeta 应识别为 Clash 配置");
        Assert.AreEqual(234, result.Servers.Count, "234 个 ss 节点全部解析");
        Assert.IsTrue(result.Servers.All(s => s.Type == "SS"), "全部节点为 ss");
        Assert.IsTrue(result.Servers.All(s => s.Port > 0 && !string.IsNullOrWhiteSpace(s.Hostname)), "全部节点地址端口完整");

        // 首节点锚点：ss + obfs http plugin（plugin: obfs → SIP008 obfs-local + obfs=/obfs-host= 拼接）
        var first = (ShadowsocksServer)result.Servers[0];
        Assert.AreEqual("uuid-000.example-a.test", first.Hostname);
        Assert.AreEqual((ushort)12022, first.Port);
        Assert.AreEqual("aes-128-gcm", first.EncryptMethod);
        Assert.AreEqual("sanitized-pass", first.Password);
        Assert.AreEqual("obfs-local", first.Plugin);
        Assert.AreEqual("obfs=http;obfs-host=obfshex.example-h.test", first.PluginOption);

        // dns：nameserver 的 2 条 DoH；proxy-server-nameserver 的 udp://127.0.0.1 自指跳过
        CollectionAssert.AreEqual(new[] { "https://223.5.5.5/dns-query", "https://doh.pub/dns-query" }, result.NodeDns);

        // hosts：30 条域名→域名映射，首节点 server 的映射目标在 definition-meaning 家族（example-g）
        Assert.AreEqual(30, result.Hosts.Count);
        Assert.AreEqual("uuid-019.example-g.test", result.Hosts["uuid-000.example-a.test"]);
    }

    [TestMethod]
    public void Parse_AllProxyTypes_FieldMapping()
    {
        const string yaml = """
            port: 7890
            proxies:
              - name: "ss-obfs"
                type: ss
                server: a.example.com
                port: 1
                cipher: aes-128-gcm
                password: p1
                plugin: obfs
                plugin-opts:
                  mode: http
                  host: obfs.example.com
              - name: "ss-v2ray-plugin"
                type: ss
                server: a.example.com
                port: 2
                cipher: AEAD_CHACHA20_POLY1305
                password: p2
                plugin: v2ray-plugin
                plugin-opts:
                  mode: websocket
                  host: ws.example.com
                  path: /ws
                  tls: true
                  mux: true
              - name: "ssr"
                type: ssr
                server: b.example.com
                port: 3
                cipher: dummy
                password: p3
                protocol: auth_aes128_md5
                protocol-param: 32:aa
                obfs: tls1.2_ticket_auth
                obfs-param: ob.example.com
              - name: "vmess-ws"
                type: vmess
                server: c.example.com
                port: 4
                uuid: 11111111-2222-3333-4444-555555555555
                alterId: 2
                cipher: chacha20-poly1305
                network: ws
                tls: true
                servername: sni.example.com
                ws-opts:
                  path: /vm
                  headers:
                    Host: ws-h.example.com
              - name: "vmess-http"
                type: vmess
                server: c.example.com
                port: 5
                uuid: 11111111-2222-3333-4444-555555555555
                network: http
                http-opts:
                  path:
                    - /h2p
                  headers:
                    Host:
                      - h2.example.com
              - name: "vless-reality"
                type: vless
                server: d.example.com
                port: 6
                uuid: 66666666-7777-8888-9999-000000000000
                flow: xtls-rprx-vision
                sni: real.example.com
                client-fingerprint: firefox
                network: grpc
                grpc-opts:
                  grpc-service-name: gs
                reality-opts:
                  public-key: pbk-value
                  short-id: sid-value
              - name: "trojan"
                type: trojan
                server: e.example.com
                port: 7
                password: p7
                sni: tsni.example.com
              - name: "hy2"
                type: hysteria2
                server: f.example.com
                port: 8
                password: p8
                sni: hsni.example.com
                up: "100 Mbps"
                down: 200
                obfs: salamander
                obfs-password: obp8
              - name: "tuic"
                type: tuic
                server: g.example.com
                port: 9
                uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
                password: p9
                sni: ts9.example.com
                congestion-controller: bbr
                udp-relay-mode: native
                alpn:
                  - h3
              - name: "socks"
                type: socks5
                server: h.example.com
                port: 10
                username: u10
                password: p10
              - name: "wg"
                type: wireguard
                server: i.example.com
                port: 11
                private-key: priv11
                public-key: pub11
                preshared-key: psk11
                ip:
                  - 172.16.0.2
                ipv6:
                  - fd00::2
                mtu: 1280
              - name: "ssh"
                type: ssh
                server: j.example.com
                port: 12
                username: u12
                password: p12
                private-key: pk12
              - name: "anytls"
                type: anytls
                server: k.example.com
                port: 13
                password: p13
                sni: asni.example.com
            """;

        var ok = ClashConfigParser.TryParse(yaml, out var result);

        Assert.IsTrue(ok);
        Assert.AreEqual(13, result.Servers.Count, "11 类型 13 节点全部映射");

        var ss = (ShadowsocksServer)result.Servers.First(s => s.Remark == "ss-obfs");
        Assert.AreEqual("obfs-local", ss.Plugin);
        Assert.AreEqual("obfs=http;obfs-host=obfs.example.com", ss.PluginOption);

        var ssvp = (ShadowsocksServer)result.Servers.First(s => s.Remark == "ss-v2ray-plugin");
        Assert.AreEqual("v2ray-plugin", ssvp.Plugin);
        Assert.AreEqual("chacha20-ietf-poly1305", ssvp.EncryptMethod, "AEAD_CHACHA20_POLY1305 归一化");
        Assert.AreEqual("mode=websocket;tls;host=ws.example.com;path=/ws;mux=4;", ssvp.PluginOption);

        var ssr = (ShadowsocksRServer)result.Servers.First(s => s.Remark == "ssr");
        Assert.AreEqual("none", ssr.EncryptMethod, "dummy = 无加密");
        Assert.AreEqual("auth_aes128_md5", ssr.Protocol);
        Assert.AreEqual("32:aa", ssr.ProtocolParam);
        Assert.AreEqual("tls1.2_ticket_auth", ssr.OBFS);
        Assert.AreEqual("ob.example.com", ssr.OBFSParam);

        var vmess = (VMessServer)result.Servers.First(s => s.Remark == "vmess-ws");
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", vmess.UserID);
        Assert.AreEqual(2, vmess.AlterID);
        Assert.AreEqual("chacha20-poly1305", vmess.EncryptMethod);
        Assert.AreEqual("ws", vmess.TransferProtocol);
        Assert.AreEqual("tls", vmess.TLSSecureType);
        Assert.AreEqual("sni.example.com", vmess.ServerName);
        Assert.AreEqual("/vm", vmess.Path);
        Assert.AreEqual("ws-h.example.com", vmess.Host);

        var vhttp = (VMessServer)result.Servers.First(s => s.Remark == "vmess-http");
        Assert.AreEqual("tcp", vhttp.TransferProtocol, "network http = tcp + headerType http");
        Assert.AreEqual("http", vhttp.FakeType);
        Assert.AreEqual("/h2p", vhttp.Path, "http-opts path 数组取首");
        Assert.AreEqual("h2.example.com", vhttp.Host, "headers.Host 数组取首");

        var vless = (VLESSServer)result.Servers.First(s => s.Remark == "vless-reality");
        Assert.AreEqual("xtls-rprx-vision", vless.Flow);
        Assert.AreEqual("reality", vless.TLSSecureType);
        Assert.AreEqual("real.example.com", vless.ServerName, "sni 优先于 servername");
        Assert.AreEqual("firefox", vless.Fingerprint);
        Assert.AreEqual("grpc", vless.TransferProtocol);
        Assert.AreEqual("gs", vless.Path);
        Assert.AreEqual("pbk-value", vless.RealityPublicKey);
        Assert.AreEqual("sid-value", vless.RealityShortId);

        var trojan = (TrojanServer)result.Servers.First(s => s.Remark == "trojan");
        Assert.AreEqual("p7", trojan.Password);
        Assert.AreEqual("tsni.example.com", trojan.Host);
        Assert.AreEqual("tls", trojan.TLSSecureType);

        var hy2 = (Hysteria2Server)result.Servers.First(s => s.Remark == "hy2");
        Assert.AreEqual("p8", hy2.Password);
        Assert.AreEqual("hsni.example.com", hy2.Host);
        Assert.AreEqual(100, hy2.UpMbps, "up \"100 Mbps\" 取前导数字");
        Assert.AreEqual(200, hy2.DownMbps, "down 数字标量");
        Assert.AreEqual("obp8", hy2.ObfsPassword);

        var tuic = (TUICServer)result.Servers.First(s => s.Remark == "tuic");
        Assert.AreEqual("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", tuic.Uuid);
        Assert.AreEqual("p9", tuic.Password);
        Assert.AreEqual("bbr", tuic.CongestionControl);
        Assert.AreEqual("native", tuic.UdpRelayMode);
        Assert.AreEqual("h3", tuic.Alpn);

        var socks = (Socks5Server)result.Servers.First(s => s.Remark == "socks");
        Assert.AreEqual("u10", socks.Username);
        Assert.AreEqual("p10", socks.Password);

        var wg = (WireGuardServer)result.Servers.First(s => s.Remark == "wg");
        Assert.AreEqual("priv11", wg.PrivateKey);
        Assert.AreEqual("pub11", wg.PeerPublicKey);
        Assert.AreEqual("psk11", wg.PreSharedKey, "preshared-key（无连字符，subconverter 字段名）");
        Assert.AreEqual("172.16.0.2,fd00::2", wg.LocalAddresses, "ip + ipv6 合并");
        Assert.AreEqual(1280, wg.MTU);

        var ssh = (SSHServer)result.Servers.First(s => s.Remark == "ssh");
        Assert.AreEqual("u12", ssh.User);
        Assert.AreEqual("p12", ssh.Password);
        Assert.AreEqual("pk12", ssh.PrivateKey);

        var anytls = (AnytlsServer)result.Servers.First(s => s.Remark == "anytls");
        Assert.AreEqual("p13", anytls.Password);
        Assert.AreEqual("asni.example.com", anytls.Host);
    }

    [TestMethod]
    public void Parse_DnsAndHosts_EdgeCases()
    {
        const string yaml = """
            proxies: []
            dns:
              nameserver:
                - https://doh.example.com/dns-query#展示名
                - udp://127.0.0.1:7874
                - 223.5.5.5
                - https://doh.example.com/dns-query
              proxy-server-nameserver:
                - https://doh2.example.com/api-query
            hosts:
              array.example.com:
                - 1.2.3.4
                - 5.6.7.8
              "*.wild.example.com": w.example.com
              plain.example.com: 9.9.9.9
            """;

        var ok = ClashConfigParser.TryParse(yaml, out var result);

        Assert.IsTrue(ok);
        // # 注释尾缀剥离、udp:// 与纯 IP 跳过、重复去重
        CollectionAssert.AreEqual(new[] { "https://doh.example.com/dns-query", "https://doh2.example.com/api-query" }, result.NodeDns);

        Assert.AreEqual(3, result.Hosts.Count);
        Assert.AreEqual("1.2.3.4", result.Hosts["array.example.com"], "hosts 数组值取首");
        Assert.AreEqual("w.example.com", result.Hosts["+.wild.example.com"], "*. 通配转 Netch Hosts 键形态 +.");
        Assert.AreEqual("9.9.9.9", result.Hosts["plain.example.com"]);
    }

    [TestMethod]
    public void Parse_SkipsUnknownAndMalformedEntries()
    {
        const string yaml = """
            proxies:
              - name: "snell-not-supported"
                type: snell
                server: s.example.com
                port: 1
                psk: p
              - name: "no-server"
                type: ss
                port: 2
                cipher: aes-128-gcm
                password: p
              - name: "bad-port"
                type: ss
                server: b.example.com
                port: not-a-port
                cipher: aes-128-gcm
                password: p
              - name: "valid-tail"
                type: socks5
                server: ok.example.com
                port: 1080
            """;

        var ok = ClashConfigParser.TryParse(yaml, out var result);

        Assert.IsTrue(ok, "含 proxies: 键即 Clash（与节点解析成败无关）");
        Assert.AreEqual(1, result.Servers.Count, "不认识类型与坏字段节点跳过，不炸整份");
        Assert.AreEqual("valid-tail", result.Servers[0].Remark);
    }

    [TestMethod]
    public void Parse_NonClashText_ReturnsFalse()
    {
        // base64 分享链接列表与 SIP008 JSON 都不是 Clash
        Assert.IsFalse(ClashConfigParser.TryParse("ss://YWVzLTEyOC1nY206cGFzc3dvcmQ@1.2.3.4:8388", out _));
        Assert.IsFalse(ClashConfigParser.TryParse("[{\"server\":\"1.2.3.4\"}]", out _));
        Assert.IsFalse(ClashConfigParser.TryParse("", out _));
    }

    [TestMethod]
    public void ParseText_ClashYamlViaShareLink()
    {
        // ShareLink.ParseText 统一入口：base64/JSON 都不匹配的 Clash 文本走 Clash 分支
        var servers = ShareLink.ParseText(ReadSanitizedTagMeta());
        Assert.AreEqual(234, servers.Count);
    }
}
