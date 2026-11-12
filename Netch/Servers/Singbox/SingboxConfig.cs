using System.Text.Json.Serialization;

namespace Netch.Servers;

public class SingboxConfig
{
    [JsonPropertyName("log")]
    public SingboxLog Log { get; } = new();

    [JsonPropertyName("inbounds")]
    public List<object> Inbounds { get; } = new();

    [JsonPropertyName("outbounds")]
    public List<object>? Outbounds { get; set; }

    [JsonPropertyName("endpoints")]
    public List<object>? Endpoints { get; set; }

    [JsonPropertyName("http_clients")]
    public List<SingboxHttpClient>? HttpClients { get; set; }

    [JsonPropertyName("dns")]
    public SingboxDns? Dns { get; set; }

    [JsonPropertyName("route")]
    public SingboxRoute Route { get; } = new();

    [JsonPropertyName("experimental")]
    public SingboxExperimental? Experimental { get; set; }
}

/// <summary>
///     sing-box 1.12+ DNS 配置（legacy address 形式已于 1.14 移除）：
///     servers 新格式 {type, tag, server, detour, domain_resolver}，规则与 final 指定上游
/// </summary>
public class SingboxDns
{
    [JsonPropertyName("servers")]
    public List<SingboxDnsServer> Servers { get; } = new();

    [JsonPropertyName("rules")]
    public List<SingboxDnsRule>? Rules { get; set; }

    [JsonPropertyName("final")]
    public string? Final { get; set; }

    [JsonPropertyName("strategy")]
    public string? Strategy { get; set; }
}

public class SingboxDnsServer
{
    /// <summary>udp / https（DoH）/ local</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "udp";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("server_port")]
    public int? ServerPort { get; set; }

    /// <summary>查询经指定出站（proxy=经节点，治本 #889 运营商劫持）</summary>
    [JsonPropertyName("detour")]
    public string? Detour { get; set; }

    /// <summary>DoH 服务器地址为域名时的解析器（避免鸡生蛋）</summary>
    [JsonPropertyName("domain_resolver")]
    public string? DomainResolver { get; set; }
}

public class SingboxDnsRule
{
    [JsonPropertyName("rule_set")]
    public List<string>? RuleSet { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";
}

public class SingboxHttpClient
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "rule-set-fetch";

    /// <summary>direct（默认，直连下载）或 proxy（经节点出站下载）</summary>
    [JsonPropertyName("detour")]
    public string? Detour { get; set; }
}

/// <summary>
///     sing-box tun 入站（M4-1）：auto_route 接管默认路由，dns_mode 不显式写
///     （auto_route 开启时内核默认 hijack），节点服务器/内网段经 route_exclude_address 放行
/// </summary>
public class SingboxTunInbound
{
    [JsonPropertyName("type")]
    public string Type { get; } = "tun";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "tun-in";

    [JsonPropertyName("interface_name")]
    public string InterfaceName { get; set; } = "netch";

    [JsonPropertyName("address")]
    public List<string> Address { get; } = new() { "172.19.0.1/30" };

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 9000;

    [JsonPropertyName("auto_route")]
    public bool AutoRoute { get; set; } = true;

    /// <summary>防 DNS 泄漏（Windows 多宿主解析）；与局域网共享类软件冲突时可关</summary>
    [JsonPropertyName("strict_route")]
    public bool StrictRoute { get; set; } = true;

    /// <summary>不进 tun 的段（根本不劫持，比规则更彻底）：内网 Bypass + 节点服务器 /32</summary>
    [JsonPropertyName("route_exclude_address")]
    public List<string>? RouteExcludeAddress { get; set; }

    /// <summary>FullCone NAT（游戏 UDP 语义优先）</summary>
    [JsonPropertyName("endpoint_independent_nat")]
    public bool EndpointIndependentNat { get; set; } = true;

    /// <summary>system / gvisor / mixed（mixed=TCP system + UDP gvisor）</summary>
    [JsonPropertyName("stack")]
    public string Stack { get; set; } = "mixed";
}

public class SingboxExperimental
{
    [JsonPropertyName("clash_api")]
    public SingboxClashApi? ClashApi { get; set; }

    [JsonPropertyName("cache_file")]
    public SingboxCacheFile? CacheFile { get; set; }
}

/// <summary>
///     remote 规则集下载缓存（断网冷启动时用缓存；与 initial_path 共同构成离线兜底）
/// </summary>
public class SingboxCacheFile
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public class SingboxClashApi
{
    [JsonPropertyName("external_controller")]
    public string ExternalController { get; set; } = "";
}

public class SingboxLog
{
    // warn 起步：TUN 全局接管下 info 为每包日志（实机实测 4 分钟 2.3GB），stdout 管道逐行
    // 转发是 CPU 与临时端口耗尽的元凶；启动判定不依赖日志关键字，异常由 error/fatal 与
    // 进程退出事件兜底可见
    [JsonPropertyName("level")]
    public string Level { get; set; } = "warn";

    [JsonPropertyName("timestamp")]
    public bool Timestamp { get; set; } = true;
}

public class SingboxRoute
{
    [JsonPropertyName("rules")]
    public List<object> Rules { get; } = new();

    [JsonPropertyName("final")]
    public string Final { get; set; } = "proxy";

    /// <summary>域名出站/DoH 服务器地址的默认解析器（1.14 起 dns 配置存在时必须显式指定）</summary>
    [JsonPropertyName("default_domain_resolver")]
    public string? DefaultDomainResolver { get; set; }

    [JsonPropertyName("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;

    [JsonPropertyName("rule_set")]
    public List<SingboxRuleSet>? RuleSet { get; set; }
}

/// <summary>
///     remote 规则集（.srs）：内核按 update_interval 自动下载更新，缓存依赖 experimental.cache_file
/// </summary>
public class SingboxRuleSet
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "remote";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("format")]
    public string Format { get; set; } = "binary";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>无缓存时的初始文件（相对 sing-box 工作目录），仅内置规则集携带</summary>
    [JsonPropertyName("initial_path")]
    public string? InitialPath { get; set; }

    /// <summary>引用顶层 http_clients[] 的 tag，避免每条重复内嵌 detour</summary>
    [JsonPropertyName("http_client")]
    public string? HttpClient { get; set; }

    [JsonPropertyName("update_interval")]
    public string? UpdateInterval { get; set; }
}

/// <summary>
///     sing-box route rule（1.11+ action 语义）：默认 action=route 需指定 outbound，
///     reject/sniff 等动作不携带 outbound
/// </summary>
public class SingboxRouteRule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "route";

    [JsonPropertyName("outbound")]
    public string? Outbound { get; set; }

    [JsonPropertyName("domain_suffix")]
    public List<string>? DomainSuffix { get; set; }

    [JsonPropertyName("domain")]
    public List<string>? Domain { get; set; }

    [JsonPropertyName("domain_keyword")]
    public List<string>? DomainKeyword { get; set; }

    [JsonPropertyName("domain_regex")]
    public List<string>? DomainRegex { get; set; }

    [JsonPropertyName("ip_cidr")]
    public List<string>? IpCidr { get; set; }

    [JsonPropertyName("port")]
    public List<int>? Port { get; set; }

    [JsonPropertyName("port_range")]
    public List<string>? PortRange { get; set; }

    [JsonPropertyName("protocol")]
    public List<string>? Protocol { get; set; }

    [JsonPropertyName("rule_set")]
    public List<string>? RuleSet { get; set; }
}

public class SingboxOutbound
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "proxy";

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("server_port")]
    public ushort? ServerPort { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("congestion_control")]
    public string? CongestionControl { get; set; }

    [JsonPropertyName("udp_relay_mode")]
    public string? UdpRelayMode { get; set; }

    [JsonPropertyName("up_mbps")]
    public int? UpMbps { get; set; }

    [JsonPropertyName("down_mbps")]
    public int? DownMbps { get; set; }

    [JsonPropertyName("obfs")]
    public SingboxHysteria2Obfs? Obfs { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("plugin")]
    public string? Plugin { get; set; }

    [JsonPropertyName("plugin_opts")]
    public string? PluginOpts { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("security")]
    public string? Security { get; set; }

    [JsonPropertyName("alter_id")]
    public int? AlterId { get; set; }

    [JsonPropertyName("flow")]
    public string? Flow { get; set; }

    [JsonPropertyName("packet_encoding")]
    public string? PacketEncoding { get; set; }

    [JsonPropertyName("tls")]
    public SingboxTls? Tls { get; set; }

    [JsonPropertyName("transport")]
    public SingboxTransport? Transport { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    [JsonPropertyName("private_key_passphrase")]
    public string? PrivateKeyPassphrase { get; set; }

    [JsonPropertyName("host_key")]
    public string[]? HostKey { get; set; }
}

public class SingboxSelectorOutbound
{
    [JsonPropertyName("type")]
    public string Type { get; } = "selector";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "proxy";

    [JsonPropertyName("outbounds")]
    public List<string> Outbounds { get; } = new();

    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("interrupt_exist_connections")]
    public bool InterruptExistConnections { get; set; }
}

public class SingboxUrltestOutbound
{
    [JsonPropertyName("type")]
    public string Type { get; } = "urltest";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "auto";

    [JsonPropertyName("outbounds")]
    public List<string> Outbounds { get; } = new();

    [JsonPropertyName("url")]
    public string Url { get; set; } = "https://www.gstatic.com/generate_204";

    [JsonPropertyName("interval")]
    public string Interval { get; set; } = "3m";

    [JsonPropertyName("tolerance")]
    public int Tolerance { get; set; } = 50;

    [JsonPropertyName("idle_timeout")]
    public string IdleTimeout { get; set; } = "30m";
}

public class SingboxHysteria2Obfs
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "salamander";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class SingboxTls
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("disable_sni")]
    public bool DisableSni { get; set; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("insecure")]
    public bool Insecure { get; set; }

    [JsonPropertyName("alpn")]
    public List<string>? Alpn { get; set; }

    [JsonPropertyName("utls")]
    public SingboxUtls? Utls { get; set; }

    [JsonPropertyName("reality")]
    public SingboxReality? Reality { get; set; }
}

public class SingboxUtls
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }
}

public class SingboxReality
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("short_id")]
    public string? ShortId { get; set; }
}

public class SingboxTransport
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("host")]
    public List<string>? Host { get; set; }

    [JsonPropertyName("service_name")]
    public string? ServiceName { get; set; }
}

public class SingboxWireGuardPeer
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("pre_shared_key")]
    public string? PreSharedKey { get; set; }

    [JsonPropertyName("allowed_ips")]
    public List<string> AllowedIps { get; } = new();
}

public class SingboxWireGuardEndpoint
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "wireguard";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "proxy";

    [JsonPropertyName("system")]
    public bool System { get; set; }

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 1420;

    [JsonPropertyName("address")]
    public List<string> Address { get; } = new();

    [JsonPropertyName("private_key")]
    public string PrivateKey { get; set; } = "";

    [JsonPropertyName("peers")]
    public List<SingboxWireGuardPeer> Peers { get; } = new();
}
