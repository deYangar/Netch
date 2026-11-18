using System.Text.RegularExpressions;
using Netch.Models;
using Netch.Servers;
using YamlDotNet.Serialization;

namespace Netch.Utils;

/// <summary>
///     Clash/mihomo YAML 订阅解析：proxies 全类型映射 + dns 段 DoH 提取 + hosts 段提取。
///     订阅原文是 Clash 配置（特征：顶层 proxies: 键）时的唯一解析入口——
///     节点字段映射与 subconverter 语义对齐；dns/hosts 由 SubscriptionUtil 更新时整体接管（覆盖回填）
/// </summary>
public static partial class ClashConfigParser
{
    public class Result
    {
        public List<Server> Servers { get; } = new();

        /// <summary>
        ///     dns.nameserver + dns.proxy-server-nameserver 中 https:// 开头的 DoH（去重保序，剥 # 注释尾缀）；
        ///     udp:// 自指、纯 IP 引导等条目不适用于 Netch 节点解析场景，跳过
        /// </summary>
        public List<string> NodeDns { get; } = new();

        public Dictionary<string, string> Hosts { get; } = new();
    }

    [GeneratedRegex(@"^proxies:", RegexOptions.Multiline)]
    private static partial Regex ProxiesKeyRegex();

    private static readonly Lazy<IDeserializer> Yaml = new(() => new DeserializerBuilder().IgnoreUnmatchedProperties().Build());

    /// <summary>
    ///     判定 + 解析：文本含顶层 proxies: 键即视为 Clash 配置（bool = 是否 Clash，与节点解析成败无关；
    ///     是 Clash 但 0 可用节点也返回 true，让上层按「订阅无可用节点」如实报错，而不是走错格式分支再静默一遍）
    /// </summary>
    public static bool TryParse(string text, out Result result)
    {
        result = new Result();
        if (!ProxiesKeyRegex().IsMatch(text))
            return false;

        object? root;
        try
        {
            root = Yaml.Value.Deserialize(text);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Clash YAML deserialization failed");
            return false;
        }

        if (root as Dictionary<object, object?> is not { } rootMap)
            return false;

        if (rootMap.TryGetValue("proxies", out var proxiesObj) && proxiesObj as List<object?> is { } proxies)
        {
            foreach (var node in proxies)
            {
                try
                {
                    if (MapNode(node) is { } server)
                        result.Servers.Add(server);
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Clash proxy entry skipped");
                }
            }
        }

        if (rootMap.TryGetValue("dns", out var dnsObj) && dnsObj as Dictionary<object, object?> is { } dnsMap)
        {
            foreach (var key in new[] { "nameserver", "proxy-server-nameserver" })
            {
                if (dnsMap.TryGetValue(key, out var nsObj) && nsObj as List<object?> is { } nsList)
                {
                    foreach (var entry in nsList)
                    {
                        var url = entry?.ToString()?.Trim();
                        if (url == null)
                            continue;

                        // mihomo 允许条目尾缀注释（如 https://host/dns-query#DNS\u2081 展示名），RFC 8484 客户端不认
                        var hashIndex = url.IndexOf('#');
                        if (hashIndex >= 0)
                            url = url[..hashIndex].Trim();

                        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !result.NodeDns.Contains(url, StringComparer.OrdinalIgnoreCase))
                            result.NodeDns.Add(url);
                    }
                }
            }
        }

        if (rootMap.TryGetValue("hosts", out var hostsObj) && hostsObj as Dictionary<object, object?> is { } hostsMap)
        {
            foreach (var (keyObj, valueObj) in hostsMap)
            {
                string? value;
                switch (valueObj)
                {
                    // mihomo hosts 值可为数组（多 IP 轮询），Netch Hosts 单值——取首个
                    case List<object?> { Count: > 0 } list:
                        value = list[0]?.ToString();
                        break;
                    case null:
                        continue;
                    default:
                        value = valueObj.ToString();
                        break;
                }

                if (keyObj.ToString() is not { } key || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;

                // mihomo 通配 *.suffix 与 Netch Hosts 键形态 +.suffix 的差异仅「suffix 自身是否匹配」，统一转 +.
                if (key.StartsWith("*."))
                    key = "+" + key[1..];

                result.Hosts[key] = value;
            }
        }

        return true;
    }

    #region proxies 节点映射（Clash 字段名 → Netch Server，subconverter 语义）

    private static Server? MapNode(object? node)
    {
        if (node as Dictionary<object, object?> is not { } map)
            return null;

        var type = GetStr(map, "type");
        var name = GetStr(map, "name");
        var server = GetStr(map, "server");
        var port = GetPort(map);
        if (type == null || server == null || port == null)
            return null;

        Server result = type switch
        {
            "ss" => MapSS(map),
            "ssr" => MapSSR(map),
            "vmess" => MapVMess(map),
            "vless" => MapVLESS(map),
            "trojan" => MapTrojan(map),
            "hysteria2" or "hy2" => MapHysteria2(map),
            "tuic" => MapTUIC(map),
            "socks5" => MapSocks5(map),
            "wireguard" => MapWireGuard(map),
            "ssh" => MapSSH(map),
            "anytls" => MapAnytls(map),
            _ => throw new NotSupportedException($"Clash proxy type \"{type}\" is not supported")
        };

        result.Hostname = server;
        result.Port = port.Value;
        result.Remark = name ?? $"{server}:{port}";
        return result;
    }

    private static ShadowsocksServer MapSS(Dictionary<object, object?> map)
    {
        var server = new ShadowsocksServer
        {
            EncryptMethod = NormalizeSsCipher(GetStr(map, "cipher")) ?? SSGlobal.EncryptMethods[4],
            Password = GetStr(map, "password") ?? ""
        };

        var plugin = GetStr(map, "plugin");
        var opts = GetMap(map, "plugin-opts");
        string pluginMode = "", pluginHost = "", pluginPath = "", pluginMux = "", pluginTls = "";
        switch (plugin)
        {
            case "obfs":
            case "simple-obfs":
                // Clash plugin-opts {mode, host} → SIP008 obfs-local opts "obfs=<mode>[;obfs-host=<host>]"
                pluginMode = GetStr(opts, "mode") ?? "http";
                pluginHost = GetStr(opts, "host") ?? "";
                server.Plugin = "obfs-local";
                server.PluginOption = "obfs=" + pluginMode + (pluginHost.Length > 0 ? ";obfs-host=" + pluginHost : "");
                break;
            case "v2ray-plugin":
                // Clash {mode(默认websocket), host, path, tls(bool), mux(bool)} → SIP008 "mode=..;tls;host=..;path=..;mux=4"（subconverter 同构）
                pluginMode = GetStr(opts, "mode") ?? "websocket";
                pluginHost = GetStr(opts, "host") ?? "";
                pluginPath = GetStr(opts, "path") ?? "";
                pluginTls = GetBool(opts, "tls") == true ? "tls;" : "";
                pluginMux = GetBool(opts, "mux") == true ? "4" : "";
                server.Plugin = "v2ray-plugin";
                server.PluginOption = "mode=" + pluginMode + ";" + pluginTls
                    + (pluginHost.Length > 0 ? "host=" + pluginHost + ";" : "")
                    + (pluginPath.Length > 0 ? "path=" + pluginPath + ";" : "")
                    + (pluginMux.Length > 0 ? "mux=" + pluginMux + ";" : "");
                break;
            case null or "":
                // 老式无 plugin 的顶层 obfs/obfs-host 字段（go-shadowsocks2 时代配置，subconverter 兼容）
                if (GetStr(map, "obfs") is { } legacyObfs)
                {
                    server.Plugin = "obfs-local";
                    server.PluginOption = "obfs=" + legacyObfs + (GetStr(map, "obfs-host") is { } h ? ";obfs-host=" + h : "");
                }
                break;
            default:
                // shadow-tls / restls 等插件 Netch SS 插件体系不支持：保留节点、丢弃插件并留痕
                Log.Warning("Clash ss plugin \"{Plugin}\" is not supported, plugin dropped for node {Remark}", plugin, GetStr(map, "name"));
                break;
        }

        return server;
    }

    /// <summary>
    ///     go-shadowsocks2 大写 AEAD_* 加密名归一化为 Netch/sing-box 形态（subconverter 同构）
    /// </summary>
    private static string? NormalizeSsCipher(string? cipher)
    {
        if (cipher == null)
            return null;

        if (cipher == "AEAD_CHACHA20_POLY1305")
            return "chacha20-ietf-poly1305";

        if (cipher.StartsWith("AEAD_", StringComparison.Ordinal))
            return cipher.Replace("AEAD_", "").Replace('_', '-').ToLowerInvariant();

        return cipher;
    }

    private static ShadowsocksRServer MapSSR(Dictionary<object, object?> map)
    {
        // 老配置字段名无连字符变体（protocolparam/obfsparam，subconverter 兼容）；cipher dummy = 无加密
        var cipher = GetStr(map, "cipher");
        if (cipher == "dummy")
            cipher = "none";

        return new ShadowsocksRServer
        {
            Password = GetStr(map, "password") ?? "",
            EncryptMethod = cipher ?? SSRGlobal.EncryptMethods[4],
            Protocol = GetStr(map, "protocol") ?? SSRGlobal.Protocols[0],
            ProtocolParam = GetStrOrNull(map, "protocol-param") ?? GetStrOrNull(map, "protocolparam"),
            OBFS = GetStr(map, "obfs") ?? SSRGlobal.OBFSs[0],
            OBFSParam = GetStrOrNull(map, "obfs-param") ?? GetStrOrNull(map, "obfsparam")
        };
    }

    private static VMessServer MapVMess(Dictionary<object, object?> map)
    {
        var server = new VMessServer
        {
            UserID = GetStr(map, "uuid") ?? "",
            AlterID = GetInt(map, "alterId") ?? 0,
            EncryptMethod = GetStr(map, "cipher") ?? VMessGlobal.EncryptMethods[0],
            PacketEncoding = GetStrOrNull(map, "packet-encoding") ?? VMessGlobal.PacketEncodings[2],
            ServerName = GetStrOrNull(map, "servername")
        };

        MapV2RayTransport(server, map);
        return server;
    }

    private static VLESSServer MapVLESS(Dictionary<object, object?> map)
    {
        // SNI：sni 优先于 servername（subconverter 同构）
        var server = new VLESSServer
        {
            UserID = GetStr(map, "uuid") ?? "",
            Flow = GetStrOrNull(map, "flow") ?? VLESSGlobal.FlowTypes[0],
            Fingerprint = GetStrOrNull(map, "client-fingerprint") ?? "chrome",
            ServerName = GetStrOrNull(map, "sni") ?? GetStrOrNull(map, "servername")
        };

        MapV2RayTransport(server, map);

        // reality-opts 存在即 Reality（VLESS 独占，优先于 tls 字段）
        if (GetMap(map, "reality-opts") is { } reality)
        {
            server.RealityPublicKey = GetStr(reality, "public-key");
            server.RealityShortId = GetStr(reality, "short-id");
            server.TLSSecureType = "reality";
        }

        return server;
    }

    /// <summary>
    ///     vmess/vless 共用传输层映射：network/tls/ws-opts/h2-opts/grpc-opts/http-opts/xhttp-opts。
    ///     network: http 是 v2ray「tcp + headerType=http」语义（v2rayN 同构处理）；
    ///     ws path 缺省 "/"（mihomo/subconverter 默认值）；xhttp 是 Xray 独占传输（Netch 路由 Xray 辅内核）
    /// </summary>
    private static void MapV2RayTransport(VMessServer server, Dictionary<object, object?> map)
    {
        var network = GetStr(map, "network") ?? "tcp";
        if (network == "http")
        {
            server.TransferProtocol = "tcp";
            server.FakeType = "http";
            var httpOpts = GetMap(map, "http-opts");
            server.Path = GetScalar(httpOpts, "path");
            server.Host = GetHeadersHost(httpOpts);
        }
        else
        {
            server.TransferProtocol = network;
            switch (network)
            {
                case "ws":
                    var wsOpts = GetMap(map, "ws-opts");
                    server.Path = GetStr(wsOpts, "path") ?? "/";
                    server.Host = GetHeadersHost(wsOpts);
                    break;
                case "h2":
                    var h2Opts = GetMap(map, "h2-opts");
                    server.Host = GetScalar(h2Opts, "host");
                    server.Path = GetStr(h2Opts, "path") ?? "/";
                    break;
                case "grpc":
                    server.Path = GetStrOrNull(GetMap(map, "grpc-opts"), "grpc-service-name");
                    break;
                case "xhttp":
                    var xhttpOpts = GetMap(map, "xhttp-opts");
                    server.Path = GetStr(xhttpOpts, "path") ?? "/";
                    server.Host = GetStr(xhttpOpts, "host") ?? GetHeadersHost(xhttpOpts);
                    break;
            }
        }

        // Clash tls 字段是 bool（tls: true）或字符串；vmess 不支持 reality（遇 reality-opts 留痕忽略）
        if (GetBool(map, "tls") == true || GetStr(map, "tls") == "tls")
            server.TLSSecureType = "tls";

        if (GetMap(map, "reality-opts") != null && server is not VLESSServer)
            Log.Warning("Clash {Type} node {Remark} carries reality-opts which is not supported, ignored", server.Type, GetStr(map, "name"));
    }

    private static TrojanServer MapTrojan(Dictionary<object, object?> map)
    {
        // Netch Trojan 模型无传输层字段：Clash trojan 的 network: ws/grpc 留痕忽略
        var network = GetStr(map, "network");
        if (network != null && network != "tcp")
            Log.Warning("Clash trojan node {Remark} network \"{Network}\" is not supported, ignored", GetStr(map, "name"), network);

        return new TrojanServer
        {
            Password = GetStr(map, "password") ?? "",
            Host = GetStrOrNull(map, "sni"),
            TLSSecureType = "tls"
        };
    }

    private static Hysteria2Server MapHysteria2(Dictionary<object, object?> map)
    {
        var server = new Hysteria2Server
        {
            // password 空时兜底老字段 auth（subconverter 同构）
            Password = GetStr(map, "password") is { Length: > 0 } password ? password : GetStr(map, "auth") ?? "",
            Host = GetStrOrNull(map, "sni"),
            UpMbps = ParseBandwidth(GetStr(map, "up")),
            DownMbps = ParseBandwidth(GetStr(map, "down"))
        };

        // Clash obfs: salamander（唯一取值）；obfs-password 缺失时 sing-box 侧空混淆
        if (GetStr(map, "obfs") == "salamander")
            server.ObfsPassword = GetStrOrNull(map, "obfs-password");

        return server;
    }

    /// <summary>
    ///     Clash 带宽值兼容 "100 Mbps" / "1 GB" / 纯数字（YamlDotNet 标量解出 int），取前导数字
    /// </summary>
    private static int ParseBandwidth(string? value)
    {
        if (value == null)
            return 0;

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var mbps) ? mbps : 0;
    }

    private static TUICServer MapTUIC(Dictionary<object, object?> map)
    {
        var server = new TUICServer
        {
            Uuid = GetStr(map, "uuid") ?? Guid.NewGuid().ToString(),
            Password = GetStr(map, "password") ?? "",
            Host = GetStrOrNull(map, "sni"),
            CongestionControl = GetStr(map, "congestion-controller") ?? TUICGlobal.CongestionControls[0],
            UdpRelayMode = GetStr(map, "udp-relay-mode") ?? TUICGlobal.UdpRelayModes[0]
        };

        if (GetList(map, "alpn") is { Count: > 0 } alpn)
            server.Alpn = string.Join(",", alpn.Where(a => a != null));

        return server;
    }

    private static Socks5Server MapSocks5(Dictionary<object, object?> map)
    {
        return new Socks5Server
        {
            Username = GetStrOrNull(map, "username"),
            Password = GetStrOrNull(map, "password")
        };
    }

    private static WireGuardServer MapWireGuard(Dictionary<object, object?> map)
    {
        if (GetList(map, "reserved") is { Count: > 0 })
            Log.Warning("Clash wireguard node {Remark} reserved bytes are not supported, ignored", GetStr(map, "name"));

        var addresses = new List<string>();
        foreach (var key in new[] { "ip", "ipv6" })
        {
            foreach (var address in GetList(map, key) ?? new List<object?>())
            {
                var value = address?.ToString();
                if (value != null)
                    addresses.Add(value);
            }
        }

        return new WireGuardServer
        {
            PrivateKey = GetStr(map, "private-key") ?? "",
            PeerPublicKey = GetStr(map, "public-key") ?? "",
            PreSharedKey = GetStrOrNull(map, "preshared-key"),
            LocalAddresses = addresses.Count > 0 ? string.Join(",", addresses) : "172.16.0.2",
            MTU = GetInt(map, "mtu") ?? 1420
        };
    }

    private static SSHServer MapSSH(Dictionary<object, object?> map)
    {
        return new SSHServer
        {
            User = GetStr(map, "username") ?? "root",
            Password = GetStr(map, "password") ?? "",
            PrivateKey = GetStr(map, "private-key") ?? ""
        };
    }

    private static AnytlsServer MapAnytls(Dictionary<object, object?> map)
    {
        return new AnytlsServer
        {
            Password = GetStr(map, "password") ?? "",
            Host = GetStrOrNull(map, "sni")
        };
    }

    #endregion

    #region YamlDotNet 动态树取值辅助

    private static Dictionary<object, object?>? GetMap(Dictionary<object, object?>? map, string key)
    {
        return map != null && map.TryGetValue(key, out var value) ? value as Dictionary<object, object?> : null;
    }

    private static List<object?>? GetList(Dictionary<object, object?>? map, string key)
    {
        return map != null && map.TryGetValue(key, out var value) ? value as List<object?> : null;
    }

    /// <summary>
    ///     ws-opts/http-opts 的 headers.Host（伪装域名）；http-opts 的 Host/path 是数组形态（取首），
    ///     ws-opts 的 Host 是标量——统一两吃；h2-opts 的 host 是裸列表不走此函数
    /// </summary>
    private static string? GetHeadersHost(Dictionary<object, object?>? opts)
    {
        var headers = GetMap(opts, "headers");
        return GetScalar(headers, "Host") ?? GetScalar(headers, "host");
    }

    /// <summary>
    ///     标量或单元素数组统一取值（mihomo 各传输段数组/标量形态不统一）
    /// </summary>
    private static string? GetScalar(Dictionary<object, object?>? map, string key)
    {
        if (map == null || !map.TryGetValue(key, out var value) || value == null)
            return null;

        return value is List<object?> { Count: > 0 } list ? list[0]?.ToString() : value.ToString();
    }

    private static string? GetStr(Dictionary<object, object?>? map, string key)
    {
        return map != null && map.TryGetValue(key, out var value) && value != null ? value.ToString() : null;
    }

    private static string? GetStrOrNull(Dictionary<object, object?>? map, string key)
    {
        var value = GetStr(map, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? GetInt(Dictionary<object, object?>? map, string key)
    {
        return int.TryParse(GetStr(map, key), out var value) ? value : null;
    }

    /// <summary>
    ///     YAML bool 标量在动态树里可能是 bool 也可能是字符串（"true"/"True"），标量化后统一 TryParse（大小写不敏感）
    /// </summary>
    private static bool? GetBool(Dictionary<object, object?>? map, string key)
    {
        return bool.TryParse(GetScalar(map, key), out var value) ? value : null;
    }

    private static ushort? GetPort(Dictionary<object, object?> map)
    {
        return ushort.TryParse(GetStr(map, "port"), out var port) ? port : null;
    }

    #endregion
}
