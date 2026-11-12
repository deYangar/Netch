// sing-box 配置生成验证工具：对每种受支持协议生成样例配置，
// 供 sing-box check 校验字段合法性。用法：
//   dotnet run --project SingboxConfigVerifier -c Release -- <输出目录>
using System.Text.Json;
using Netch;
using Netch.Controllers;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Models.Modes.TunMode;
using Netch.Servers;
using Netch.Utils;

var outDir = args.Length > 0 ? args[0] : "configs";
Directory.CreateDirectory(outDir);

Global.Settings.V2RayConfig.AllowInsecure = false;

var samples = new Dictionary<string, Server>
{
    ["anytls"] = new AnytlsServer
    {
        Hostname = "localhost",
        Port = 443,
        Password = "test-password",
        Host = "sni.example.com",
        Remark = "anytls sample"
    },
    ["hysteria2"] = new Hysteria2Server
    {
        Hostname = "localhost",
        Port = 443,
        Password = "test-password",
        Host = "sni.example.com",
        UpMbps = 100,
        DownMbps = 500,
        ObfsPassword = "obfs-pass",
        Remark = "hysteria2 sample"
    },
    ["hysteria2-minimal"] = new Hysteria2Server
    {
        Hostname = "localhost",
        Port = 443,
        Password = "test-password"
    },
    ["tuic"] = new TUICServer
    {
        Hostname = "localhost",
        Port = 443,
        Uuid = "2DD61D93-75D8-4DA4-AC0E-6AECE7EAC365",
        Password = "test-password",
        Host = "sni.example.com",
        CongestionControl = "bbr",
        UdpRelayMode = "native",
        Alpn = "h3"
    },
    ["ss"] = new ShadowsocksServer
    {
        Hostname = "localhost",
        Port = 8388,
        EncryptMethod = "aes-256-gcm",
        Password = "test-password"
    },
    ["ss-plugin"] = new ShadowsocksServer
    {
        Hostname = "localhost",
        Port = 8388,
        EncryptMethod = "aes-256-gcm",
        Password = "test-password",
        Plugin = "simple-obfs",
        PluginOption = "obfs=http;obfs-host=www.example.com"
    },
    ["trojan"] = new TrojanServer
    {
        Hostname = "localhost",
        Port = 443,
        Password = "test-password",
        Host = "sni.example.com"
    },
    ["vmess-ws-tls"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        EncryptMethod = "auto",
        TransferProtocol = "ws",
        TLSSecureType = "tls",
        ServerName = "sni.example.com",
        Path = "/v2ray",
        Host = "ws.example.com"
    },
    ["vmess-grpc"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "grpc",
        Path = "grpc-service"
    },
    ["vmess-tcp"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "tcp",
        FakeType = "none"
    },
    ["vless-h2"] = new VLESSServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "h2",
        TLSSecureType = "tls",
        Host = "h2.example.com",
        Path = "/h2path"
    },
    ["vless-reality"] = new VLESSServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "tcp",
        TLSSecureType = "reality",
        ServerName = "sni.example.com",
        Flow = "xtls-rprx-vision",
        Fingerprint = "chrome",
        RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
        RealityShortId = "0123456789abcdef"
    },
    ["socks5"] = new Socks5Server
    {
        Hostname = "localhost",
        Port = 1080,
        Username = "user",
        Password = "pass"
    },
    ["wireguard"] = new WireGuardServer
    {
        Hostname = "localhost",
        Port = 51820,
        LocalAddresses = "172.16.0.2",
        PeerPublicKey = "Z1XXLsKYkYxuiYjJIkRvtIKFepCYHTgON+GwPq7SOV4=",
        PrivateKey = "YNXtAzepDqRv9H52osJVDQnznT5AM11eCK3ESpwSt04=",
        PreSharedKey = "31aIhAPwktDGpH4JDhA8GNvjFXEf/a6+UaQRyOAiyfM=",
        MTU = 1420
    },
    ["ssh"] = new SSHServer
    {
        Hostname = "localhost",
        Port = 22,
        User = "root",
        Password = "test-password"
    }
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    // 与 Global.NewCustomJsonSerializerOptions 对齐：null 字段不输出（sing-box 严格解析拒绝未知字段）
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

// 分享链接解析/生成往返回归：reality 链接字段不能丢
var realityLink = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@example.com:443" +
                  "?security=reality&sni=sni.example.com&fp=chrome&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0" +
                  "&sid=0123456789abcdef&type=tcp&flow=xtls-rprx-vision#reality-node";
var parsed = (VLESSServer)V2rayUtils.ParseVUri(realityLink).Single();
Console.WriteLine(
    (parsed.TLSSecureType == "reality" &&
     parsed.ServerName == "sni.example.com" &&
     parsed.Flow == "xtls-rprx-vision" &&
     parsed.Fingerprint == "chrome" &&
     parsed.RealityPublicKey == "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0" &&
     parsed.RealityShortId == "0123456789abcdef" &&
     parsed.Hostname == "example.com" && parsed.Port == 443)
        ? "OK   vless-reality link parse"
        : $"FAIL vless-reality link parse: {parsed.TLSSecureType} sni={parsed.ServerName} flow={parsed.Flow} fp={parsed.Fingerprint} pbk={parsed.RealityPublicKey} sid={parsed.RealityShortId} host={parsed.Hostname}:{parsed.Port}");

var roundTrip = (VLESSServer)V2rayUtils.ParseVUri(V2rayUtils.GetVShareLink(parsed, "vless")).Single();
Console.WriteLine(
    (roundTrip.RealityPublicKey == parsed.RealityPublicKey &&
     roundTrip.RealityShortId == parsed.RealityShortId &&
     roundTrip.Flow == parsed.Flow &&
     roundTrip.Fingerprint == parsed.Fingerprint &&
     roundTrip.ServerName == parsed.ServerName)
        ? "OK   vless-reality link roundtrip"
        : $"FAIL vless-reality link roundtrip: {V2rayUtils.GetVShareLink(parsed, "vless")}");

// 多节点分组样例：trojan + vmess + vless-reality + trojan-plain 进 outbounds/selector/urltest，
// SSR（sing-box 不支持）应被跳过
Global.Settings.Server.Clear();
Global.Settings.Server.AddRange(new Server[]
{
    new TrojanServer { Group = "group-a", Hostname = "localhost", Port = 443, Password = "test-password", Host = "sni.example.com", Remark = "trojan-node" },
    new VMessServer { Group = "group-a", Hostname = "localhost", Port = 443, UserID = "b831381d-6324-4d53-ad4f-8cda48b30811", TransferProtocol = "ws", TLSSecureType = "tls", ServerName = "sni.example.com", Path = "/v2ray", Remark = "vmess-node" },
    new VLESSServer { Group = "group-a", Hostname = "localhost", Port = 443, UserID = "b831381d-6324-4d53-ad4f-8cda48b30811", TLSSecureType = "reality", ServerName = "sni.example.com", Flow = "xtls-rprx-vision", RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", Remark = "vless-reality-node" },
    new ShadowsocksRServer { Group = "group-a", Hostname = "localhost", Port = 443, EncryptMethod = "aes-256-cfb", Protocol = "origin", OBFS = "plain", Password = "test-password", Remark = "ssr-node-skip" },
    new TrojanServer { Group = "group-a", Hostname = "localhost", Port = 443, Password = "test-password", TLSSecureType = "none", Remark = "trojan-plain-node" }
});

var failed = new List<string>();

try
{
    var (multiConfig, tagMap) = await SingboxConfigUtils.GenerateClientConfigAsync(
        Global.Settings.Server.Where(s => s.Group == "group-a").ToList(),
        Global.Settings.Server[0],
        9090);

    var skipped = Global.Settings.Server.Count(s => s.Group == "group-a") - tagMap.Count;
    var selector = multiConfig.Outbounds!.OfType<SingboxSelectorOutbound>().Single();
    var urltest = multiConfig.Outbounds!.OfType<SingboxUrltestOutbound>().Single();
    Console.WriteLine(
        (tagMap.Count == 4 && skipped == 1 &&
         selector.Outbounds.Count == 5 && selector.Default == tagMap.First(kv => kv.Value == Global.Settings.Server[0]).Key &&
         urltest.Outbounds.Count == 4 && multiConfig.Experimental?.ClashApi?.ExternalController == "127.0.0.1:9090")
            ? $"OK   multi-node config (tags=[{string.Join(',', tagMap.Keys)}])"
            : $"FAIL multi-node config: tagMap={tagMap.Count} skipped={skipped} selector={selector.Outbounds.Count}/{selector.Default} urltest={urltest.Outbounds.Count} api={multiConfig.Experimental?.ClashApi?.ExternalController}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "multi-node.json"), JsonSerializer.Serialize(multiConfig, jsonOptions));
    Console.WriteLine($"OK   multi-node -> {Path.Combine(outDir, "multi-node.json")}");
}
catch (Exception e)
{
    failed.Add("multi-node");
    Console.WriteLine($"FAIL multi-node: {e}");
}
foreach (var (name, server) in samples)
{
    try
    {
        var (config, _) = await SingboxConfigUtils.GenerateClientConfigAsync(server);
        var path = Path.Combine(outDir, $"{name}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, jsonOptions));
        Console.WriteLine($"OK   {name} -> {path}");
    }
    catch (Exception e)
    {
        failed.Add(name);
        Console.WriteLine($"FAIL {name}: {e.Message}");
    }
}

// ===== 路由层规则（M2 分流）=====

// Steam 分流样例：下载 CDN 直连、商店社区走代理、SMTP 拒绝
var steamMode = new Redirector
{
    Rules = new List<RouteRule>
    {
        new()
        {
            DomainSuffix = new List<string> { "steamcontent.com", "steamserver.net" },
            Action = RouteRuleAction.Direct
        },
        new()
        {
            DomainSuffix = new List<string> { "steampowered.com", "steamcommunity.com" },
            Action = RouteRuleAction.Proxy
        },
        new()
        {
            Port = new List<string> { "25" },
            Action = RouteRuleAction.Block
        }
    }
};

try
{
    var (routedConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9091, steamMode);

    var routeRules = routedConfig.Route.Rules.OfType<SingboxRouteRule>().ToList();
    var hasDirectOutbound = routedConfig.Outbounds!.Any(o =>
        JsonDocument.Parse(JsonSerializer.Serialize(o, jsonOptions)).RootElement.TryGetProperty("tag", out var tag) &&
        tag.GetString() == "direct");

    Console.WriteLine(
        (routeRules.Count == 5 &&
         routeRules[0].Action == "sniff" && routeRules[0].Outbound == null &&
         routeRules[1].Action == "hijack-dns" && routeRules[1].Outbound == null && routeRules[1].Port!.Single() == 53 &&
         routeRules[2].Action == "route" && routeRules[2].Outbound == "direct" &&
         routeRules[2].DomainSuffix!.SequenceEqual(new[] { "steamcontent.com", "steamserver.net" }) &&
         routeRules[3].Outbound == "proxy" &&
         routeRules[4].Action == "reject" && routeRules[4].Outbound == null &&
         routeRules[4].Port!.Single() == 25 &&
         routedConfig.Dns != null &&
         routedConfig.Experimental?.ClashApi?.ExternalController == "127.0.0.1:9091" &&
         hasDirectOutbound)
            ? $"OK   routed config (rules=[{string.Join(" -> ", routeRules.Select(r => $"{r.Action}:{r.Outbound ?? "-"}"))}], dns servers={routedConfig.Dns.Servers.Count})"
            : Fail("routed-single", $"rules={routeRules.Count} dns={routedConfig.Dns != null} api={routedConfig.Experimental?.ClashApi?.ExternalController}"));

    await File.WriteAllTextAsync(Path.Combine(outDir, "routed-single.json"), JsonSerializer.Serialize(routedConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("routed-single");
    Console.WriteLine($"FAIL routed-single: {e}");
}

// 多节点 + 路由规则 + ip_cidr / port_range / protocol / domain_regex 维度
try
{
    var multiRoutedMode = new Redirector
    {
        Rules = new List<RouteRule>
        {
            new()
            {
                DomainSuffix = new List<string> { "steamcontent.com" },
                IpCidr = new List<string> { "162.254.0.0/16", "153.254.86.0/24" },
                Action = RouteRuleAction.Direct
            },
            new()
            {
                DomainRegex = new List<string> { "^stun\\..+" },
                Protocol = new List<string> { "stun" },
                Action = RouteRuleAction.Proxy
            },
            new()
            {
                Port = new List<string> { "1000:2000", "443" },
                DomainKeyword = new List<string> { "blocked" },
                Action = RouteRuleAction.Block
            }
        }
    };

    var (multiRouted, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        Global.Settings.Server.Where(s => s.Group == "group-a").ToList(),
        Global.Settings.Server[0],
        9092,
        multiRoutedMode);

    var multiRouteRules = multiRouted.Route.Rules.OfType<SingboxRouteRule>().ToList();
    var directFirst = JsonDocument.Parse(JsonSerializer.Serialize(multiRouted.Outbounds!.First(), jsonOptions))
        .RootElement.TryGetProperty("type", out var firstType) && firstType.GetString() == "direct";
    var portRangeRule = multiRouteRules.Single(r => r.PortRange != null);

    Console.WriteLine(
        (multiRouteRules.Count == 5 &&
         multiRouteRules[0].Action == "sniff" &&
         multiRouteRules[1].Action == "hijack-dns" &&
         multiRouteRules[2].Outbound == "direct" &&
         multiRouteRules[2].IpCidr!.Count == 2 &&
         multiRouteRules[3].Protocol!.Single() == "stun" &&
         portRangeRule.PortRange!.SequenceEqual(new[] { "1000:2000" }) &&
         portRangeRule.Port!.Single() == 443 &&
         directFirst)
            ? "OK   routed multi-node (hijack-dns inserted, direct outbound placed first, port/port_range split)"
            : $"FAIL routed multi-node: rules={multiRouteRules.Count} directFirst={directFirst}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "routed-multi.json"), JsonSerializer.Serialize(multiRouted, jsonOptions));
}
catch (Exception e)
{
    failed.Add("routed-multi");
    Console.WriteLine($"FAIL routed-multi: {e}");
}

// ===== 连接表观测（M2-3）：Clash API 一律开启（EV-1）=====

// 单参重载（最小配置）不开 API（字节不变用例）；显式传端口则一律开（含单节点无规则）
try
{
    var (minimal, _) = await SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"]);
    var (withPort, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9100, null);
    Console.WriteLine(
        (minimal.Experimental?.ClashApi == null &&
         withPort.Experimental?.ClashApi?.ExternalController == "127.0.0.1:9100")
            ? "OK   clash api: always on when port given, minimal overload stays off"
            : $"FAIL clash api always-on: minimal={minimal.Experimental?.ClashApi != null} withPort={withPort.Experimental?.ClashApi?.ExternalController}");
}
catch (Exception e)
{
    failed.Add("clash-api-always-on");
    Console.WriteLine($"FAIL clash-api-always-on: {e}");
}

// 无规则模式（FilterDNS 默认 true）：无用户路由规则、不开 Clash API；dns 节 + hijack-dns 生成（行为增强），
// rule-set 基础设施（cache_file/http_clients）随 DNS 分流引入
try
{
    var (plainConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"]);
    var plainRules = plainConfig.Route.Rules.OfType<SingboxRouteRule>().ToList();
    Console.WriteLine(
        (plainRules.Count == 1 && plainRules[0].Action == "hijack-dns" && plainRules[0].Port!.Single() == 53 &&
         plainConfig.Dns != null && plainConfig.Dns.Servers.Count == 2 &&
         plainConfig.Experimental?.ClashApi == null && plainConfig.Experimental?.CacheFile?.Enabled == true)
            ? "OK   unrouted config: dns hijack only, no clash api"
            : $"FAIL unrouted config: rules={plainRules.Count} dns={plainConfig.Dns != null} clashApi={plainConfig.Experimental?.ClashApi != null} cache={plainConfig.Experimental?.CacheFile?.Enabled}");
}
catch (Exception e)
{
    failed.Add("unrouted-plain");
    Console.WriteLine($"FAIL unrouted-plain: {e}");
}

// ===== DNS 子系统（M2-2 hijack-dns + 策略分流）=====

// 用例 1：Auto 策略（默认）——国内 udp 直连 / 远程 DoH 经 proxy / geosite-cn 分流规则
try
{
    var (autoConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9097, steamMode);
    var dns = autoConfig.Dns!;
    var china = dns.Servers.Single(s => s.Tag == "dns-china");
    var remote = dns.Servers.Single(s => s.Tag == "dns-remote");
    var hijack = autoConfig.Route.Rules.OfType<SingboxRouteRule>().Single(r => r.Action == "hijack-dns");

    Console.WriteLine(
        (china.Type == "udp" && china.Server == "223.5.5.5" && china.Detour == null &&
         remote.Type == "https" && remote.Server == "1.1.1.1" && remote.Detour == "proxy" && remote.DomainResolver == null &&
         dns.Rules!.Single().RuleSet!.Single() == "geosite-cn" && dns.Rules.Single().Server == "dns-china" &&
         dns.Final == "dns-remote" && dns.Strategy == "prefer_ipv4" &&
         hijack.Port!.Single() == 53 && dns.Servers.Count == 2 &&
         autoConfig.Route.RuleSet!.Any(r => r.Tag == "geosite-cn"))
            ? "OK   dns Auto policy (china udp direct, remote DoH via proxy, geosite-cn rule)"
            : $"FAIL dns Auto: china={china.Type}/{china.Detour} remote={remote.Type}/{remote.Detour} final={dns.Final}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "dns-auto.json"), JsonSerializer.Serialize(autoConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("dns-auto");
    Console.WriteLine($"FAIL dns-auto: {e}");
}

// 用例 2/3/4：ProxyAll / DirectAll / udp:// Custom / 域名 RemoteDns
try
{
    Global.Settings.DNS.Policy = DnsPolicies.ProxyAll;
    var (proxyAllConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9098, steamMode);
    Console.WriteLine(
        proxyAllConfig.Dns!.Rules == null && proxyAllConfig.Dns.Final == "dns-remote"
            ? "OK   dns ProxyAll (no rules, final=remote)"
            : $"FAIL dns ProxyAll: rules={proxyAllConfig.Dns.Rules != null} final={proxyAllConfig.Dns.Final}");

    Global.Settings.DNS.Policy = DnsPolicies.DirectAll;
    var (directAllConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9098, steamMode);
    Console.WriteLine(
        directAllConfig.Dns!.Rules == null && directAllConfig.Dns.Final == "dns-china"
            ? "OK   dns DirectAll (no rules, final=china)"
            : $"FAIL dns DirectAll: final={directAllConfig.Dns.Final}");

    Global.Settings.DNS.Policy = DnsPolicies.Custom;
    Global.Settings.DNS.RemoteDns = "udp://8.8.8.8:5353";
    var (udpCustomConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9098, steamMode);
    var udpRemote = udpCustomConfig.Dns!.Servers.Single(s => s.Tag == "dns-remote");
    Console.WriteLine(
        udpRemote.Type == "udp" && udpRemote.Server == "8.8.8.8" && udpRemote.ServerPort == 5353 && udpRemote.Detour == "proxy"
            ? "OK   dns Custom udp:// remote via proxy"
            : $"FAIL dns Custom udp: type={udpRemote.Type} server={udpRemote.Server} port={udpRemote.ServerPort} detour={udpRemote.Detour}");
    await File.WriteAllTextAsync(Path.Combine(outDir, "dns-udp-custom.json"), JsonSerializer.Serialize(udpCustomConfig, jsonOptions));

    Global.Settings.DNS.RemoteDns = "dns.google";
    var (domainConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9098, steamMode);
    var domainRemote = domainConfig.Dns!.Servers.Single(s => s.Tag == "dns-remote");
    Console.WriteLine(
        domainRemote.Type == "https" && domainRemote.Server == "dns.google" && domainRemote.DomainResolver == "dns-china"
            ? "OK   dns domain remote sets domain_resolver=dns-china"
            : $"FAIL dns domain remote: resolver={domainRemote.DomainResolver}");
    await File.WriteAllTextAsync(Path.Combine(outDir, "dns-domain-remote.json"), JsonSerializer.Serialize(domainConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("dns-policies");
    Console.WriteLine($"FAIL dns-policies: {e}");
}
finally
{
    Global.Settings.DNS = new DnsConfig();
}

// 用例 5：FilterDNS=false → 无 dns 节、无 hijack-dns（配置字节回到存量形态）
try
{
    Global.Settings.Redirector.FilterDNS = false;
    var (noDnsConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9099, steamMode);
    Console.WriteLine(
        noDnsConfig.Dns == null && noDnsConfig.Route.Rules.OfType<SingboxRouteRule>().All(r => r.Action != "hijack-dns")
            ? "OK   dns FilterDNS=false: no dns node, no hijack rule"
            : "FAIL dns FilterDNS=false leaked dns fields");
}
catch (Exception e)
{
    failed.Add("dns-filter-off");
    Console.WriteLine($"FAIL dns-filter-off: {e}");
}
finally
{
    Global.Settings.Redirector.FilterDNS = true;
}

// 用例 6：非法国内上游（域名形式）→ MessageException
try
{
    Global.Settings.DNS.ChinaDns = "dns.alidns.com";
    ExpectMessageException("dns-invalid-china", () =>
        SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"]).GetAwaiter().GetResult());
}
finally
{
    Global.Settings.DNS.ChinaDns = "223.5.5.5";
}

// 内置 Steam 模式重写为 JSON 的回归：加载 + 规则字段 + 生成配置
foreach (var modeFile in new[] { "Storage/mode/Steam.json", "Storage/mode/Steam Games.json", "Storage/mode/Steam Games (with Steam).json" })
{
    try
    {
        var mode = (Redirector)ModeHelper.LoadMode(modeFile);
        var downloadRule = mode.Rules!.Single(r => r.Action == RouteRuleAction.Direct);
        var handleCount = mode.Handle.Count;

        var (modeConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
            new[] { samples["socks5"] }, samples["socks5"], 9093, mode);
        var modeRouteRules = modeConfig.Route.Rules.OfType<SingboxRouteRule>().ToList();

        Console.WriteLine(
            (handleCount > 0 && mode.Rules.Count == 2 &&
             downloadRule.DomainSuffix!.Contains("steamcontent.com") &&
             modeRouteRules.Count == 4 &&
             modeRouteRules[1].Action == "hijack-dns" &&
             modeRouteRules[2].Outbound == "direct")
                ? $"OK   builtin mode \"{Path.GetFileName(modeFile)}\" (handle={handleCount}, rules={mode.Rules.Count})"
                : $"FAIL builtin mode \"{Path.GetFileName(modeFile)}\": handle={handleCount} rules={mode.Rules.Count} routeRules={modeRouteRules.Count}");
    }
    catch (Exception e)
    {
        failed.Add(modeFile);
        Console.WriteLine($"FAIL builtin mode {modeFile}: {e.Message}");
    }
}

// 嗅探协议生成侧小写归一（M2-0 FIX-1）：校验 IgnoreCase 放行大写输入，生成必须归一为内核的小写语义
try
{
    var protocolCaseMode = new Redirector
    {
        Rules = new List<RouteRule>
        {
            new()
            {
                Protocol = new List<string> { "TLS", "quic" },
                Action = RouteRuleAction.Proxy
            }
        }
    };

    var (protocolCaseConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9094, protocolCaseMode);
    var normalized = protocolCaseConfig.Route.Rules.OfType<SingboxRouteRule>()
        .Single(r => r.Protocol != null).Protocol!;

    Console.WriteLine(
        normalized.SequenceEqual(new[] { "tls", "quic" })
            ? "OK   protocol lower-case normalization (FIX-1)"
            : $"FAIL protocol normalization: [{string.Join(',', normalized)}]");

    await File.WriteAllTextAsync(Path.Combine(outDir, "protocol-case.json"), JsonSerializer.Serialize(protocolCaseConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("protocol-case");
    Console.WriteLine($"FAIL protocol-case: {e}");
}

// ===== 规则集订阅（M2-1 rule_set/.srs）=====

// 用例 1/5：引用内置 geosite-cn + ExternalResources.ViaProxy 开关
try
{
    var ruleSetMode = new Redirector
    {
        Rules = new List<RouteRule>
        {
            new()
            {
                RuleSet = new List<string> { "geosite-cn" },
                Action = RouteRuleAction.Direct
            }
        }
    };

    var (ruleSetConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9095, ruleSetMode);

    var rs = ruleSetConfig.Route.RuleSet!.Single();
    var ruleRef = ruleSetConfig.Route.Rules.OfType<SingboxRouteRule>().Single(r => r.RuleSet != null);
    var useProxy = Global.Settings.ExternalResources.ViaProxy;
    Console.WriteLine(
        (rs.Tag == "geosite-cn" && rs.Type == "remote" && rs.Format == "binary" &&
         rs.Url.Contains("geosite-cn.srs") && rs.InitialPath == @"rule-set\geosite-cn.srs" &&
         rs.HttpClient == "rule-set-fetch" && rs.UpdateInterval == "1d" &&
         ruleSetConfig.HttpClients!.Single().Detour == (useProxy ? "proxy" : null) &&
         ruleSetConfig.Experimental?.CacheFile?.Enabled == true &&
         ruleRef.RuleSet!.SequenceEqual(new[] { "geosite-cn" }) && ruleRef.Outbound == "direct")
            ? $"OK   rule-set builtin geosite-cn (detour={(useProxy ? "proxy" : "default-direct")})"
            : $"FAIL rule-set builtin: tag={rs.Tag} url={rs.Url} initial={rs.InitialPath} detour={ruleSetConfig.HttpClients!.Single().Detour}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "rule-set-builtin.json"), JsonSerializer.Serialize(ruleSetConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("rule-set-builtin");
    Console.WriteLine($"FAIL rule-set-builtin: {e}");
}

// 用例 5：ExternalResources.ViaProxy=true → http_clients detour=proxy
try
{
    Global.Settings.ExternalResources.ViaProxy = true;
    Global.Settings.RuleSets.Add(new RuleSetConfig { Tag = "geosite-category-ads", Url = "https://example.com/ads.srs", UpdateInterval = "6h" });

    var viaProxyMode = new Redirector
    {
        Rules = new List<RouteRule>
        {
            new() { RuleSet = new List<string> { "geosite-cn", "geosite-category-ads" }, Action = RouteRuleAction.Block }
        }
    };

    var (viaProxyConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9096, viaProxyMode);
    var custom = viaProxyConfig.Route.RuleSet!.Single(r => r.Tag == "geosite-category-ads");

    Console.WriteLine(
        (viaProxyConfig.HttpClients!.Single().Detour == "proxy" &&
         custom.InitialPath == null && custom.UpdateInterval == "6h" &&
         viaProxyConfig.Route.RuleSet!.Count == 2)
            ? "OK   rule-set custom tag + via-proxy detour"
            : $"FAIL rule-set custom/via-proxy: detour={viaProxyConfig.HttpClients!.Single().Detour} initial={custom.InitialPath} interval={custom.UpdateInterval}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "rule-set-custom.json"), JsonSerializer.Serialize(viaProxyConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("rule-set-custom");
    Console.WriteLine($"FAIL rule-set-custom: {e}");
}
finally
{
    Global.Settings.ExternalResources.ViaProxy = false;
    Global.Settings.RuleSets.Clear();
}

// 用例 3：未知 tag → MessageException
ExpectMessageException("rule-set-unknown-tag", () =>
    SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"], new Redirector
    {
        Rules = new List<RouteRule> { new() { RuleSet = new List<string> { "geosite-nothing" }, Action = RouteRuleAction.Direct } }
    }).GetAwaiter().GetResult());

// 用例 4（M2-2 后语义）：无规则 + FilterDNS=false 才是完全存量形态（dns 接管会引入 geosite-cn rule_set 基础设施）

// ===== 内置模式库回归（M2-4 全清单）=====
// 遍历 Storage/mode/**/*.json：反序列化成功、handle 非空；平台级模式含 Direct 规则（快照断言）
try
{
    var modeDir = Path.Combine(Directory.GetCurrentDirectory(), "Storage", "mode");
    var jsonModes = Directory.EnumerateFiles(modeDir, "*.json", SearchOption.AllDirectories).ToList();
    var loaded = 0;
    var platformWithRules = new List<string>();
    var tunModes = 0;

    foreach (var file in jsonModes)
    {
        var mode = ModeHelper.LoadMode(file);
        loaded++;

        if (mode is TunMode)
        {
            tunModes++;
            continue;
        }

        if (mode is not Redirector { Handle.Count: > 0 })
        {
            failed.Add($"mode-empty:{Path.GetFileName(file)}");
            Console.WriteLine($"FAIL builtin mode empty handle: {file}");
            continue;
        }

        var proc = (Redirector)mode;
        if (proc.Rules?.Any(r => r.Action == RouteRuleAction.Direct) == true)
            platformWithRules.Add(Path.GetFileNameWithoutExtension(file));
    }

    // 平台级 Direct 规则快照（M2-4 转换时 DNS 验证过的下载 CDN 清单）
    string[] expectedDirect = {
        "Steam", "Steam Games", "Steam Games (with Steam)",
        "Battlenet", "Discord", "Discord (with Steam Apps)",
        "EA Desktop", "EA Desktop Games", "EA Desktop Games (with EA Desktop)",
        "Origin", "Origin Games"
    };
    var missing = expectedDirect.Except(platformWithRules).ToList();
    Console.WriteLine(
        (loaded == jsonModes.Count && jsonModes.Count >= 60 && tunModes >= 3 && !missing.Any())
            ? $"OK   builtin mode library ({loaded} modes, {platformWithRules.Count} with CDN direct rules, {tunModes} tun modes)"
            : $"FAIL builtin mode library: loaded={loaded}/{jsonModes.Count} tun={tunModes} missingDirect=[{string.Join(',', missing)}]");

    // EA/Discord/Battlenet 三个平台模式生成完整配置过 sing-box check
    foreach (var name in new[] { "EA Desktop", "Discord", "Battlenet" })
    {
        var mode = (Redirector)ModeHelper.LoadMode(Path.Combine(modeDir, $"{name}.json"));
        var (cfg, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
            new[] { samples["socks5"] }, samples["socks5"], 9110, mode);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"mode-{name.Replace(' ', '_')}.json"), JsonSerializer.Serialize(cfg, jsonOptions));
        var directRule = cfg.Route.Rules.OfType<SingboxRouteRule>().FirstOrDefault(r => r.Outbound == "direct");
        Console.WriteLine(
            directRule?.DomainSuffix != null && directRule.DomainSuffix.Any()
                ? $"OK   platform mode config: {name} direct rule ({directRule.DomainSuffix.Count} domains)"
                : $"FAIL platform mode config: {name} no direct rule");
    }
}
catch (Exception e)
{
    failed.Add("builtin-mode-library");
    Console.WriteLine($"FAIL builtin mode library: {e}");
}

// 生成侧错误路径：全空匹配器 / 非法端口 / 未知嗅探协议
void ExpectMessageException(string name, Action action)
{
    try
    {
        action();
        failed.Add(name);
        Console.WriteLine($"FAIL {name}: expected MessageException, no throw");
    }
    catch (MessageException)
    {
        Console.WriteLine($"OK   {name} rejected");
    }
    catch (Exception e)
    {
        failed.Add(name);
        Console.WriteLine($"FAIL {name}: unexpected {e.GetType().Name}: {e.Message}");
    }
}

ExpectMessageException("route-empty-matcher", () =>
    SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"], new Redirector
    {
        Rules = new List<RouteRule> { new() { Action = RouteRuleAction.Direct } }
    }).GetAwaiter().GetResult());

ExpectMessageException("route-invalid-port", () =>
    SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"], new Redirector
    {
        Rules = new List<RouteRule> { new() { Port = new List<string> { "443a" }, Action = RouteRuleAction.Block } }
    }).GetAwaiter().GetResult());

ExpectMessageException("route-unknown-protocol", () =>
    SingboxConfigUtils.GenerateClientConfigAsync(samples["socks5"], new Redirector
    {
        Rules = new List<RouteRule> { new() { Protocol = new List<string> { "quic2" }, Action = RouteRuleAction.Proxy } }
    }).GetAwaiter().GetResult());

// ===== TUN 模式统一（M4-1 tun 入站）=====

// 用例 1：TunMode.Rules（新语义）+ route_exclude_address 全字段 + sing-box check
try
{
    Global.Settings.TUNTAP.BypassIPs = new List<string> { "192.168.0.0/16", "10.0.0.0/8" };
    var tunMode = new TunMode
    {
        Bypass = new List<string> { "172.16.0.0/12" },
        Rules = new List<RouteRule>
        {
            new() { RuleSet = new List<string> { "geoip-cn" }, Action = RouteRuleAction.Direct },
            new() { IpCidr = new List<string> { "104.16.0.0/12" }, Action = RouteRuleAction.Proxy }
        }
    };

    var (tunConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9097, null, tunMode);

    var tun = tunConfig.Inbounds.OfType<SingboxTunInbound>().Single();
    var exclude = tun.RouteExcludeAddress!;
    // 节点解析地址 /32（IPv6 字面量 /128，localhost 在部分环境解析为 ::1）+ BypassIPs ∪ 模式 Bypass，去重保序
    var nodeServer = tunConfig.Outbounds!.OfType<SingboxOutbound>().First(o => o.Type == "socks").Server!;
    var excludeOk = exclude.Any(e => e.StartsWith($"{nodeServer}/")) && exclude.Contains("192.168.0.0/16") &&
                    exclude.Contains("10.0.0.0/8") && exclude.Contains("172.16.0.0/12");
    var mixedStill = tunConfig.Inbounds.Count == 2;
    var dnsTaken = tunConfig.Dns != null && tunConfig.Route.Rules.OfType<SingboxRouteRule>()
        .Any(r => r.Action == "hijack-dns");
    var routeRules = tunConfig.Route.Rules.OfType<SingboxRouteRule>()
        .Where(r => r.Action is "route" or "reject").ToList();
    var rulesOk = routeRules.Count == 2 &&
                  routeRules[0].RuleSet!.Single() == "geoip-cn" && routeRules[0].Outbound == "direct" &&
                  routeRules[1].IpCidr!.Single() == "104.16.0.0/12" && routeRules[1].Outbound == "proxy";
    var sniff = tunConfig.Route.Rules.OfType<SingboxRouteRule>().Any(r => r.Action == "sniff");

    Console.WriteLine(
        (tun.Type == "tun" && tun.Tag == "tun-in" && tun.InterfaceName == "netch" &&
         tun.Address.Single() == "172.19.0.1/30" && tun.Mtu == 9000 && tun.AutoRoute &&
         tun.StrictRoute && tun.EndpointIndependentNat && tun.Stack == "mixed" &&
         excludeOk && mixedStill && dnsTaken && rulesOk && sniff &&
         tunConfig.Route.RuleSet!.Any(r => r.Tag == "geoip-cn") &&
         tunConfig.Outbounds!.Any(o => o is SingboxOutbound { Type: "socks" }))
            ? "OK   tun inbound full fields + rules/exclude/dns"
            : $"FAIL tun inbound: exclude=[{string.Join(',', exclude)}] mixed={mixedStill} dns={dnsTaken} rules={rulesOk} sniff={sniff}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "tun-rules.json"), JsonSerializer.Serialize(tunConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("tun-rules");
    Console.WriteLine($"FAIL tun-rules: {e}");
}
finally
{
    Global.Settings.TUNTAP.BypassIPs = new List<string>();
}

// 用例 2：旧模式兼容——无 Rules，Handle(IP 段) 等价迁移为 Proxy 规则
try
{
    var legacyTun = new TunMode { Handle = new List<string> { "1.2.3.0/24", "5.6.7.0/24" } };
    var (legacyConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9098, null, legacyTun);

    var legacyRule = legacyConfig.Route.Rules.OfType<SingboxRouteRule>()
        .Single(r => r.IpCidr != null);

    Console.WriteLine(
        (legacyRule.Action == "route" && legacyRule.Outbound == "proxy" &&
         legacyRule.IpCidr!.SequenceEqual(new[] { "1.2.3.0/24", "5.6.7.0/24" }))
            ? "OK   tun legacy Handle migrated to ipCidr proxy rule"
            : "FAIL tun legacy-handle migration");

    await File.WriteAllTextAsync(Path.Combine(outDir, "tun-legacy-handle.json"), JsonSerializer.Serialize(legacyConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("tun-legacy-handle");
    Console.WriteLine($"FAIL tun-legacy-handle: {e}");
}

// 用例 3：无规则纯全局（Global.json 语义）——tun 入站在、无 route.rules、DNS 仍接管
try
{
    var (pureConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9099, null, new TunMode());

    var pureHasTun = pureConfig.Inbounds.OfType<SingboxTunInbound>().Any();
    // 纯全局 = 无用户路由规则（FillDns 的 hijack-dns 一条除外），DNS 仍强制接管
    var pureNoRules = pureConfig.Route.Rules.OfType<SingboxRouteRule>().Count() == 1 &&
                      pureConfig.Route.Rules.OfType<SingboxRouteRule>().Single().Action == "hijack-dns";
    var pureDns = pureConfig.Dns != null;

    Console.WriteLine(
        (pureHasTun && pureNoRules && pureDns)
            ? "OK   tun pure global (no rules, dns hijack still on)"
            : $"FAIL tun pure global: tun={pureHasTun} noRules={pureNoRules} dns={pureDns}");

    await File.WriteAllTextAsync(Path.Combine(outDir, "tun-pure-global.json"), JsonSerializer.Serialize(pureConfig, jsonOptions));
}
catch (Exception e)
{
    failed.Add("tun-pure-global");
    Console.WriteLine($"FAIL tun-pure-global: {e}");
}

// 用例 4：StrictRoute/Stack 设置透传 + Bypass 全空时 route_exclude_address 缺省（节点 /32 仍排除）
try
{
    Global.Settings.TUNTAP.StrictRoute = false;
    Global.Settings.TUNTAP.Stack = "system";
    var (customConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(
        new[] { samples["socks5"] }, samples["socks5"], 9100, null, new TunMode());

    var customTun = customConfig.Inbounds.OfType<SingboxTunInbound>().Single();
    var customNodeServer = customConfig.Outbounds!.OfType<SingboxOutbound>().First(o => o.Type == "socks").Server!;
    Console.WriteLine(
        (!customTun.StrictRoute && customTun.Stack == "system" &&
         customTun.RouteExcludeAddress!.Count == 1 &&
         customTun.RouteExcludeAddress[0].StartsWith($"{customNodeServer}/"))
            ? "OK   tun strict_route/stack passthrough, node /32 still excluded"
            : $"FAIL tun passthrough: strict={customTun.StrictRoute} stack={customTun.Stack} exclude={customTun.RouteExcludeAddress?.Count}");
}
catch (Exception e)
{
    failed.Add("tun-passthrough");
    Console.WriteLine($"FAIL tun-passthrough: {e}");
}
finally
{
    Global.Settings.TUNTAP.StrictRoute = true;
    Global.Settings.TUNTAP.Stack = "mixed";
}

// 用例 5（验收场景 9）：sing-box 表达不了的节点（SSR/旧XTLS 已明示拒绝，mKCP/xhttp 走 Xray 兜底）
// 恒不落 sing-box——内核路由不落 sing-box 即 TUN 分支显式报错的条件
try
{
    var conflicts = new Server[]
    {
        new ShadowsocksRServer { Hostname = "localhost", Port = 443 },
        new VMessServer { Hostname = "localhost", Port = 443, TransferProtocol = "kcp" },
        new VLESSServer { Hostname = "localhost", Port = 443, TransferProtocol = "xhttp" }
    };
    var allRoutedAway = conflicts.All(s =>
    {
        try
        {
            return MainController.CreateServerController(s) is not SingboxController;
        }
        catch (MessageException)
        {
            // SSR / 旧 XTLS 启动即明确拒绝（2026-10-31 拍板 A）——拒绝同样等于不落 sing-box
            return true;
        }
    });
    Console.WriteLine(allRoutedAway
        ? "OK   tun conflict servers (SSR/mKCP/xhttp) never route to sing-box"
        : "FAIL tun conflict servers: some route to sing-box");
    if (!allRoutedAway)
        failed.Add("tun-conflict");
}
catch (Exception e)
{
    failed.Add("tun-conflict");
    Console.WriteLine($"FAIL tun-conflict: {e}");
}

// RE-1§2.8：Local 集按引用顺序逐集展开，集内维度枚举序聚合，不生成 Local 定义
try
{
    Global.Settings.RuleSets.AddRange(new[]
    {
        new RuleSetConfig
        {
            Tag = "steam-local", Type = RuleSetKind.Local,
            Entries = new List<RuleSetEntry>
            {
                new() { Kind = RouteRuleDimension.IpCidr, Value = "192.0.2.0/24" },
                new() { Kind = RouteRuleDimension.DomainSuffix, Value = "steamcontent.com" },
                new() { Kind = RouteRuleDimension.DomainSuffix, Value = "steamserver.net" }
            }
        },
        new RuleSetConfig
        {
            Tag = "second-local", Type = RuleSetKind.Local,
            Entries = new List<RuleSetEntry> { new() { Kind = RouteRuleDimension.Port, Value = "443" } }
        }
    });
    var localMode = new Redirector
    {
        FilterDNS = false,
        Rules = new List<RouteRule>
        {
            new() { RuleSet = new List<string> { "steam-local", "geosite-cn", "second-local" }, Action = RouteRuleAction.Direct }
        }
    };
    var (localConfig, _) = await SingboxConfigUtils.GenerateClientConfigAsync(new[] { samples["socks5"] }, samples["socks5"], 9102, localMode);
    var expanded = localConfig.Route.Rules.OfType<SingboxRouteRule>().Where(r => r.Action != "sniff" && r.Action != "hijack-dns").ToList();
    var localOk = expanded.Count == 4 &&
                  expanded[0].DomainSuffix!.SequenceEqual(new[] { "steamcontent.com", "steamserver.net" }) &&
                  expanded[1].IpCidr!.Single() == "192.0.2.0/24" &&
                  expanded[2].RuleSet!.Single() == "geosite-cn" &&
                  expanded[3].Port!.Single() == 443 && expanded.All(r => r.Outbound == "direct") &&
                  localConfig.Route.RuleSet!.All(r => r.Tag != "steam-local" && r.Tag != "second-local");
    Console.WriteLine(localOk ? "OK   Local rule sets expand in reference order with no Local definitions"
        : Fail("local-rulesets", $"expanded={expanded.Count} order={string.Join(",", expanded.Select(r => r.GetType().Name))}"));
    await File.WriteAllTextAsync(Path.Combine(outDir, "local-rulesets.json"), JsonSerializer.Serialize(localConfig, jsonOptions));

    var mixed = new Redirector { Rules = new List<RouteRule> { new() { RuleSet = new List<string> { "steam-local" }, DomainSuffix = new List<string> { "bad.example" } } } };
    try
    {
        _ = await SingboxConfigUtils.GenerateClientConfigAsync(new[] { samples["socks5"] }, samples["socks5"], 9103, mixed);
        Console.WriteLine(Fail("local-exclusive", "mixed rule was accepted"));
    }
    catch (MessageException e) when (e.Message == Netch.Localization.I18N.T("kernel.ruleRefInlineExclusive"))
    {
        Console.WriteLine("OK   mixed Local reference/inline rejected before expansion");
    }
}
catch (Exception e)
{
    failed.Add("local-rulesets");
    Console.WriteLine($"FAIL local-rulesets: {e}");
}
finally
{
    Global.Settings.RuleSets.Clear();
}

if (args.Contains("--check"))
{
    var checkIndex = Array.IndexOf(args, "--check");
    var singbox = args.Length > checkIndex + 1
        ? args[checkIndex + 1]
        : throw new ArgumentException("--check requires the path to sing-box.exe");

    foreach (var json in Directory.GetFiles(outDir, "*.json"))
    {
        var name = Path.GetFileNameWithoutExtension(json);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = singbox,
            Arguments = $"check -c \"{json}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Console.WriteLine(process.ExitCode == 0 ? $"CHECK PASS {name}" : $"CHECK FAIL {name}: {error.Trim()}");
        if (process.ExitCode != 0)
            failed.Add(name);
    }
}

return failed.Any() ? 1 : 0;

string Fail(string name, string detail)
{
    failed.Add(name);
    return $"FAIL {name}: {detail}";
}
