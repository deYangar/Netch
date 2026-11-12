using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Models.Modes.TunMode;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Servers;

public static class SingboxConfigUtils
{
    public static Task<(SingboxConfig Config, Dictionary<string, Server> OutboundTagMap)> GenerateClientConfigAsync(Server server, Redirector? mode = null)
    {
        return GenerateClientConfigAsync(new[] { server }, server, null, mode);
    }

    /// <summary>
    ///     生成 sing-box 客户端配置。servers 多于一个时进入多节点模式：
    ///     可表达的节点全部生成出站并汇入 selector(proxy)/urltest(auto)，开启 Clash API；
    ///     sing-box 无法表达的节点跳过（日志可见），primary 无法表达则上抛。
    ///     mode 携带路由层规则时生成 route.rules + 入站嗅探，并同样需要 Clash API（分流观测）。
    ///     tunMode 非空时追加 tun 入站（TUN 全局接管，与 mixed 入站并存），
    ///     路由规则优先取 tunMode（Rules，旧模式 Handle 等价迁移），DNS 恒接管。
    /// </summary>
    /// <returns>OutboundTagMap：节点出站 tag 到 Server 的映射（Clash API delay 测试回填用）</returns>
    public static async Task<(SingboxConfig Config, Dictionary<string, Server> OutboundTagMap)> GenerateClientConfigAsync(IReadOnlyList<Server> servers, Server primary, ushort? clashApiPort, Redirector? mode = null, TunMode? tunMode = null)
    {
        var config = new SingboxConfig();
        config.Inbounds.Add(new
        {
            type = "mixed",
            tag = "mixed-in",
            listen = Global.Settings.LocalAddress,
            listen_port = Global.Settings.Socks5LocalPort
        });

        var ordered = servers.Where(s => s != null).Distinct().ToList();
        if (!ordered.Contains(primary))
            ordered.Insert(0, primary);

        var multi = ordered.Count > 1;
        var outbounds = new List<object>();
        var endpoints = new List<object>();
        var tagMap = new Dictionary<string, Server>();
        var nodeTags = new List<string>();
        // TUN 模式防回环：收集全部节点的解析后地址（AutoResolveHostnameAsync 结果），生成 route_exclude_address /32
        var serverAddresses = new List<string>();
        // primary 必须能生成（单节点模式唯一节点；多节点模式 selector 默认选中项），失败上抛
        foreach (var s in ordered)
        {
            object outbound;
            try
            {
                outbound = await BuildServerOutboundAsync(s);
            }
            catch (Exception e)
            {
                if (!multi)
                    throw;

                Log.Warning(e, "Skip server [{Type}]{Remark} for sing-box multi-node config: {Message}", s.Type, s.Remark, e.Message);
                continue;
            }

            CollectServerAddress(outbound, serverAddresses);

            var tag = multi ? $"node-{outbounds.Count + endpoints.Count}" : GetOutboundTag(outbound);
            SetOutboundTag(outbound, tag);

            if (outbound is SingboxWireGuardEndpoint)
                endpoints.Add(outbound);
            else
                outbounds.Add(outbound);

            tagMap[tag] = s;
            nodeTags.Add(tag);
        }

        if (!tagMap.ContainsValue(primary))
            throw new MessageException(I18N.TF("kernel.serverType0NotSupported", primary.Type));

        if (multi)
        {
            if (clashApiPort == null)
                throw new InvalidOperationException("multi-node config requires clash api port");

            config.Experimental = new SingboxExperimental
            {
                ClashApi = new SingboxClashApi { ExternalController = $"127.0.0.1:{clashApiPort}" }
            };

            var urltest = new SingboxUrltestOutbound { Tag = "auto" };
            urltest.Outbounds.AddRange(nodeTags);

            var selector = new SingboxSelectorOutbound { Tag = "proxy" };
            selector.Outbounds.AddRange(nodeTags);
            selector.Outbounds.Add(urltest.Tag);
            // 分组自动选优时默认走 urltest（由 Clash API 周期测速挑最低延迟节点），否则固定选中启动时选择的节点
            selector.Default = Global.Settings.Kernel.GroupAutoSelect
                ? urltest.Tag
                : tagMap.First(kv => ReferenceEquals(kv.Value, primary)).Key;

            outbounds.Add(selector);
            outbounds.Add(urltest);
        }

        if (outbounds.Any())
            config.Outbounds = outbounds;

        if (endpoints.Any())
            config.Endpoints = endpoints;

        // Clash API 一律开启（EV-1，M2-3）：显式传端口即绑定（连接表观测/延迟测试），
        // 与节点数/路由规则无关；单参重载不传端口保持最小配置
        if (clashApiPort != null)
        {
            config.Experimental ??= new SingboxExperimental();
            config.Experimental.ClashApi ??= new SingboxClashApi { ExternalController = $"127.0.0.1:{clashApiPort}" };
        }

        if (tunMode != null)
            config.Inbounds.Add(BuildTunInbound(tunMode, primary, serverAddresses));

        FillRoute(config, outbounds, mode, tunMode, multi, clashApiPort);

        FillDns(config, mode, tunMode != null);

        return (config, tagMap);
    }

    /// <summary>
    ///     将模式路由层规则翻译为内核 route.rules：首条嗅探，规则按序映射，
    ///     direct/proxy 路由到对应出站、block 拒绝。规则为空时保持全代理行为（无 rules）。
    ///     TUN 模式规则源为 tunMode（Rules 优先，旧 Handle 语义等价迁移），与进程模式同构。
    /// </summary>
    private static void FillRoute(SingboxConfig config, List<object> outbounds, Redirector? mode, TunMode? tunMode, bool multi, ushort? clashApiPort)
    {
        var rules = tunMode != null ? EffectiveTunRules(tunMode) : mode?.Rules;
        if (rules == null || !rules.Any())
            return;

        // RE-1§2.2/§2.4：先校验原始互斥语义，再展开 Local；展开结果走完整既有校验
        foreach (var rule in rules)
            ValidateRouteRule(rule);

        rules = LocalRuleSetExpand.Expand(rules);
        foreach (var rule in rules)
            ValidateRouteRule(rule);

        if (clashApiPort == null)
            throw new InvalidOperationException("routed config requires clash api port");

        // 嗅探（TLS SNI / HTTP Host / QUIC），置于规则链首条
        config.Route.Rules.Add(new SingboxRouteRule { Action = "sniff" });

        foreach (var rule in rules)
        {
            var singboxRule = BuildRouteRule(rule);
            if (singboxRule != null)
                config.Route.Rules.Add(singboxRule);
            // null = 规则仅引用了被禁用/停用的规则集（matcher 摘光），整条跳过（R 线 §4.4 单条禁用语义）
        }

        FillRuleSets(config, rules);

        if (rules.Any(r => r.Action == RouteRuleAction.Direct))
        {
            outbounds.Insert(0, new
            {
                type = "direct",
                tag = "direct"
            });
            config.Outbounds = outbounds;
        }
    }

    /// <summary>
    ///     TUN 模式生效规则：Rules 优先（新语义，RouteForm 保存目标）；
    ///     旧模式无 Rules 时 Handle(IP 段) 等价迁移为一条 ipCidr=Handle 的 Proxy 规则
    ///     （加载兼容：字段保留在模式文件中，RouteForm 保存时升级为 Rules）
    /// </summary>
    internal static List<RouteRule>? EffectiveTunRules(TunMode tunMode)
    {
        if (tunMode.Rules is { Count: > 0 })
            return tunMode.Rules;

        if (tunMode.Handle is not { Count: > 0 })
            return null;

        return new List<RouteRule> { new() { IpCidr = tunMode.Handle.ToList(), Action = RouteRuleAction.Proxy } };
    }

    /// <summary>
    ///     构建 tun 入站：地址固定 172.19.0.1/30（不再使用 TUNConfig.Address/Netmask），
    ///     route_exclude_address = 节点服务器 /32（防回环）∪ 设置级 BypassIPs ∪ 模式级 Bypass。
    ///     dns_mode 不显式写（auto_route 开启时内核默认 hijack：设接口 DNS + 53 端口劫持）。
    /// </summary>
    private static SingboxTunInbound BuildTunInbound(TunMode tunMode, Server primary, List<string> serverAddresses)
    {
        var tuntap = Global.Settings.TUNTAP;
        var inbound = new SingboxTunInbound
        {
            StrictRoute = tuntap.StrictRoute,
            Stack = tuntap.Stack
        };

        var exclude = new List<string>();
        foreach (var address in serverAddresses)
        {
            var cidr = NormalizeCidr(address);
            if (!exclude.Contains(cidr))
                exclude.Add(cidr);
        }

        // 链式 socks5 的实际远端（旧 TUNController 的 _serverRemoteAddress /32 路由等价物）
        var remoteHostname = (primary as Socks5Server)?.RemoteHostname.ValueOrDefault();
        if (remoteHostname != null)
        {
            try
            {
                // 同步配置构建器内的有界 DNS 解析（LookupAsync 自带 3s 超时，无死等风险）；
                // 全链路 async 化属结构性改造，此处按仓库惯例语句级豁免
#pragma warning disable VSTHRD002
                var remote = DnsUtils.LookupAsync(remoteHostname).GetAwaiter().GetResult()?.ToString();
#pragma warning restore VSTHRD002
                if (remote != null && !exclude.Contains($"{remote}/32"))
                    exclude.Add($"{remote}/32");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Lookup tun mode remote hostname {Host} failed", remoteHostname);
            }
        }

        foreach (var cidr in tuntap.BypassIPs.Concat(tunMode.Bypass))
        {
            var normalized = NormalizeCidr(RouteRuleValidator.NormalizeCidr(cidr));
            if (!exclude.Contains(normalized))
                exclude.Add(normalized);
        }

        if (exclude.Any())
            inbound.RouteExcludeAddress = exclude;

        return inbound;
    }

    /// <summary>
    ///     从生成的出站提取节点服务器地址（AutoResolveHostnameAsync 解析结果）
    /// </summary>
    private static void CollectServerAddress(object outbound, List<string> addresses)
    {
        switch (outbound)
        {
            case SingboxOutbound { Server: not null } o:
                addresses.Add(o.Server);
                break;
            case SingboxWireGuardEndpoint { Peers.Count: > 0 } e:
                addresses.Add(e.Peers[0].Address);
                break;
        }
    }

    /// <summary>
    ///     收集规则引用的规则集 tag（去重保序）；DNS 分流规则（dns.rules）复用同一份定义
    /// </summary>
    internal static List<string> CollectRuleSets(IEnumerable<RouteRule> rules)
    {
        return rules.SelectMany(r => r.RuleSet ?? Enumerable.Empty<string>())
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     生成 route.rule_set[] + 顶层 http_clients + experimental.cache_file：
    ///     内置 tag 用 BuiltInRuleSets 定义（带 initial_path 快照），自定义取 Settings.RuleSets；
    ///     下载出站统一 direct（ExternalResources.ViaProxy=true 时经 proxy），下载失败不阻塞启动（cache/initial_path 兜底）
    /// </summary>
    private static void FillRuleSets(SingboxConfig config, IEnumerable<RouteRule> rules)
    {
        var tags = CollectRuleSets(rules);
        if (!tags.Any())
            return;

        var definitions = new List<SingboxRuleSet>();
        foreach (var tag in tags)
        {
            var builtIn = BuiltInRuleSets.Find(tag);
            if (builtIn != null)
            {
                definitions.Add(new SingboxRuleSet
                {
                    Tag = builtIn.Tag,
                    Url = builtIn.Url,
                    InitialPath = builtIn.InitialPath,
                    UpdateInterval = builtIn.UpdateInterval
                });
                continue;
            }

            if (BuiltInRuleSets.IsRegistered(tag))
                // 内置 tag 但被单条禁用/总开关关闭：定义不注入（引用摘除在 BuildRouteRule/FillDns），非错误
                continue;

            var custom = Global.Settings.RuleSets.FirstOrDefault(r => r.Tag == tag && r.Type == RuleSetKind.Remote);
            if (custom == null)
                throw new MessageException(I18N.TF("kernel.unknownRuleSet0", tag));

            definitions.Add(new SingboxRuleSet
            {
                Tag = custom.Tag,
                Url = custom.Url,
                UpdateInterval = string.IsNullOrWhiteSpace(custom.UpdateInterval) ? null : custom.UpdateInterval
            });
        }

        config.Route.RuleSet = definitions;
        // detour 缺省即直连；显式 detour:"direct" 指向空 direct 出站会被 1.14 拒绝
        // （"detour to an empty direct outbound makes no sense"），仅经代理时写 proxy
        config.HttpClients = new List<SingboxHttpClient>
        {
            new() { Detour = Global.Settings.ExternalResources.ViaProxy ? "proxy" : null }
        };
        foreach (var definition in definitions)
            definition.HttpClient = "rule-set-fetch";

        config.Experimental ??= new SingboxExperimental();
        config.Experimental.CacheFile = new SingboxCacheFile { Enabled = true };
    }

    /// <summary>
    ///     DNS 劫持开启时生成 sing-box dns 节（与 mode.Rules 是否为空无关——纯全代理模式同样接管 DNS），
    ///     并在 route 插入 {port:53, action:hijack-dns}（TCP CONNECT 与 UDP 包通吃，修 #1063）。
    ///     上游经节点出站查询（detour=proxy），从根上解决 #889 运营商劫持。
    ///     TUN 模式强制接管（forceDns）：tun dns_mode=hijack 依赖 dns 节存在。
    /// </summary>
    private static void FillDns(SingboxConfig config, Redirector? mode, bool forceDns = false)
    {
        var dnsEnabled = forceDns || (mode?.FilterDNS ?? Global.Settings.Redirector.FilterDNS);
        if (!dnsEnabled)
            return;

        var policy = Global.Settings.DNS;
        var dns = new SingboxDns { Strategy = "prefer_ipv4" };

        // 国内上游：udp 直连（域名形式需 domain_resolver，生成侧限定 IP，非 IP 显式报错）
        var (chinaHost, chinaPort) = ParseDnsEndpoint(policy.ChinaDns, 53);
        if (!System.Net.IPAddress.TryParse(chinaHost, out _))
            throw new MessageException(I18N.TF("kernel.invalidDnsServer0", policy.ChinaDns));

        dns.Servers.Add(new SingboxDnsServer
        {
            Type = "udp",
            Tag = "dns-china",
            Server = chinaHost,
            ServerPort = chinaPort == 53 ? null : chinaPort
        });

        dns.Servers.Add(BuildRemoteDnsServer(policy));

        switch (policy.Policy)
        {
            case DnsPolicies.ProxyAll:
                dns.Final = "dns-remote";
                break;
            case DnsPolicies.DirectAll:
                dns.Final = "dns-china";
                break;
            case DnsPolicies.Auto:
            case DnsPolicies.Custom:
            default:
                // 国内域名走国内上游（geosite-cn rule_set），其余 final 经节点；定义复用 M2-1 基础设施
                // 内置规则集被停用时不再注入分流（全部查询走 final），与 Find() 的“视同不存在”语义一致
                if (Global.Settings.UseBuiltInRuleSets && BuiltInRuleSets.Find("geosite-cn") != null)
                {
                    dns.Rules = new List<SingboxDnsRule> { new() { RuleSet = new List<string> { "geosite-cn" }, Server = "dns-china" } };
                    EnsureBuiltInRuleSetDefined(config, "geosite-cn");
                }
                // geosite-cn 被单条禁用：分流不注入，全部查询走 final（与总开关关闭同语义）

                dns.Final = "dns-remote";
                break;
        }

        config.Dns = dns;

        // 1.14 起 dns 配置存在时必须显式给域名出站指定解析器（否则内核 FATAL）
        config.Route.DefaultDomainResolver = "dns-china";

        // 插入位置：sniff（若存在）之后、第一条用户规则之前——用户规则可能把 53 端口流量 direct/proxy 走掉，
        // DNS 语义必须先接管；无规则则插在首位
        var index = config.Route.Rules.TakeWhile(r => r is SingboxRouteRule s && s.Action == "sniff").Count();
        config.Route.Rules.Insert(index, new SingboxRouteRule { Port = new List<int> { 53 }, Action = "hijack-dns" });
    }

    /// <summary>
    ///     远程上游：默认 DoH 经 proxy 出站（IP 直连 DoH 无需 domain_resolver）；
    ///     域名形式额外设 domain_resolver=dns-china（经国内解析 DoH 服务器地址，避免鸡生蛋）；
    ///     udp:// 前缀（Custom 保底）→ 经节点明文 UDP 查询
    /// </summary>
    private static SingboxDnsServer BuildRemoteDnsServer(DnsConfig policy)
    {
        var remote = policy.RemoteDns.Trim();
        if (remote.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = ParseDnsEndpoint(remote["udp://".Length..], 53);
            return new SingboxDnsServer
            {
                Type = "udp",
                Tag = "dns-remote",
                Server = host,
                ServerPort = port == 53 ? null : port,
                Detour = "proxy"
            };
        }

        var (rHost, rPort) = ParseDnsEndpoint(remote, 443);
        var server = new SingboxDnsServer
        {
            Type = "https",
            Tag = "dns-remote",
            Server = rHost,
            ServerPort = rPort == 443 ? null : rPort,
            Detour = "proxy"
        };

        if (!System.Net.IPAddress.TryParse(rHost, out _))
            server.DomainResolver = "dns-china";

        return server;
    }

    /// <summary>
    ///     解析 "host" / "host:port"（IPv6 字面量只允许裸地址形式）
    /// </summary>
    private static (string Host, int Port) ParseDnsEndpoint(string value, int defaultPort)
    {
        value = value.Trim();
        if (value.LastIndexOf(':') is var colon && colon > 0 && !value.StartsWith('['))
        {
            if (int.TryParse(value[(colon + 1)..], out var port))
                return (value[..colon], port);
        }

        return (value, defaultPort);
    }

    /// <summary>
    ///     DNS 分流引用 geosite-cn 时确保 route.rule_set 含其定义（用户路由规则未引用时补定义，
    ///     http_clients / cache_file 基础设施同步补齐）
    /// </summary>
    private static void EnsureBuiltInRuleSetDefined(SingboxConfig config, string tag)
    {
        config.Route.RuleSet ??= new List<SingboxRuleSet>();
        if (config.Route.RuleSet.Any(r => r.Tag == tag))
            return;

        var builtIn = BuiltInRuleSets.Find(tag) ?? throw new MessageException(I18N.TF("kernel.unknownRuleSet0", tag));

        config.Route.RuleSet.Add(new SingboxRuleSet
        {
            Tag = builtIn.Tag,
            Url = builtIn.Url,
            InitialPath = builtIn.InitialPath,
            UpdateInterval = builtIn.UpdateInterval,
            HttpClient = "rule-set-fetch"
        });

        config.HttpClients ??= new List<SingboxHttpClient>
        {
            new() { Detour = Global.Settings.ExternalResources.ViaProxy ? "proxy" : null }
        };
        config.Experimental ??= new SingboxExperimental();
        config.Experimental.CacheFile ??= new SingboxCacheFile { Enabled = true };
    }

    /// <summary>
    ///     规则合法性校验（与生成解耦：全部规则先校验，任何一条非法即整体上抛，不产生半截配置）
    /// </summary>
    private static void ValidateRouteRule(RouteRule rule)
    {
        RouteRuleValidator.Validate(rule);
    }

    private static SingboxRouteRule? BuildRouteRule(RouteRule rule)
    {
        // 规则集引用摘除（R 线 §4.4）：被禁用/停用的 tag 不进入生成物；
        // 全部 matcher 摘光（纯规则集规则）→ 整条规则失效，返回 null 由调用方跳过
        var effectiveRuleSets = rule.RuleSet?
            .Where(tag => BuiltInRuleSets.Find(tag) != null || Global.Settings.RuleSets.Any(r => r.Tag == tag && r.Type == RuleSetKind.Remote))
            .ToList();

        var hasMatcher = rule.Enabled().Any() || (effectiveRuleSets != null && effectiveRuleSets.Any());
        if (!hasMatcher)
            return null;

        var singboxRule = new SingboxRouteRule
        {
            Action = rule.Action switch
            {
                RouteRuleAction.Block => "reject",
                _ => "route"
            }
        };

        if (rule.Action != RouteRuleAction.Block)
            singboxRule.Outbound = rule.Action == RouteRuleAction.Direct ? "direct" : "proxy";

        if (rule.DomainSuffix != null && rule.DomainSuffix.Any())
            singboxRule.DomainSuffix = rule.DomainSuffix.ToList();

        if (rule.Domain != null && rule.Domain.Any())
            singboxRule.Domain = rule.Domain.ToList();

        if (rule.DomainKeyword != null && rule.DomainKeyword.Any())
            singboxRule.DomainKeyword = rule.DomainKeyword.ToList();

        if (rule.DomainRegex != null && rule.DomainRegex.Any())
            singboxRule.DomainRegex = rule.DomainRegex.ToList();

        if (rule.IpCidr != null && rule.IpCidr.Any())
            singboxRule.IpCidr = rule.IpCidr.ToList();

        if (rule.Port != null && rule.Port.Any())
        {
            var ports = new List<int>();
            var portRanges = new List<string>();
            foreach (var port in rule.Port)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(port, @"^\d+$"))
                    ports.Add(RouteRuleValidator.ParsePort(port));
                else
                    portRanges.Add(port);
            }

            if (ports.Any())
                singboxRule.Port = ports;

            if (portRanges.Any())
                singboxRule.PortRange = portRanges;
        }

        // 嗅探结果 protocol 值恒为小写（校验用 IgnoreCase 放行用户输入），生成侧归一避免大写规则静默失配
        if (rule.Protocol != null && rule.Protocol.Any())
            singboxRule.Protocol = rule.Protocol.Select(p => p.ToLowerInvariant()).ToList();

        if (effectiveRuleSets is { Count: > 0 })
            singboxRule.RuleSet = effectiveRuleSets;

        return singboxRule;
    }

    private static async Task<object> BuildServerOutboundAsync(Server server)
    {
        return server switch
        {
            AnytlsServer anytls => await BuildAnytlsAsync(anytls),
            Hysteria2Server hysteria2 => await BuildHysteria2Async(hysteria2),
            TUICServer tuic => await BuildTuicAsync(tuic),
            ShadowsocksRServer => throw new MessageException(I18N.T("kernel.shadowsocksrNotSupportedSingBox")),
            ShadowsocksServer ss => await BuildShadowsocksAsync(ss),
            TrojanServer trojan => await BuildTrojanAsync(trojan),
            VLESSServer vless => await BuildVLessAsync(vless),
            VMessServer vmess => await BuildVMessAsync(vmess),
            Socks5Server socks => await BuildSocks5Async(socks),
            SSHServer ssh => await BuildSSHAsync(ssh),
            WireGuardServer wireguard => await BuildWireGuardEndpointAsync(wireguard),
            _ => throw new MessageException(I18N.TF("kernel.serverType0NotSupported", server.Type))
        };
    }

    private static string GetOutboundTag(object outbound)
    {
        return outbound switch
        {
            SingboxOutbound o => o.Tag,
            SingboxWireGuardEndpoint e => e.Tag,
            _ => throw new ArgumentOutOfRangeException(nameof(outbound))
        };
    }

    private static void SetOutboundTag(object outbound, string tag)
    {
        switch (outbound)
        {
            case SingboxOutbound o:
                o.Tag = tag;
                break;
            case SingboxWireGuardEndpoint e:
                e.Tag = tag;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outbound));
        }
    }

    private static async Task<SingboxOutbound> BuildAnytlsAsync(AnytlsServer server)
    {
        return new SingboxOutbound
        {
            Type = "anytls",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Password = server.Password,
            Tls = BuildRequiredTls(server.Host.ValueOrDefault() ?? server.Hostname)
        };
    }

    private static async Task<SingboxOutbound> BuildHysteria2Async(Hysteria2Server server)
    {
        var obfsPassword = server.ObfsPassword.ValueOrDefault();

        return new SingboxOutbound
        {
            Type = "hysteria2",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Password = server.Password,
            UpMbps = server.UpMbps > 0 ? server.UpMbps : null,
            DownMbps = server.DownMbps > 0 ? server.DownMbps : null,
            Obfs = obfsPassword == null ? null : new SingboxHysteria2Obfs { Password = obfsPassword },
            Tls = BuildRequiredTls(server.Host.ValueOrDefault() ?? server.Hostname)
        };
    }

    private static async Task<SingboxOutbound> BuildTuicAsync(TUICServer server)
    {
        var tls = BuildRequiredTls(server.Host.ValueOrDefault() ?? server.Hostname);
        var alpn = server.Alpn.ValueOrDefault();
        if (alpn != null)
            tls.Alpn = new List<string> { alpn };

        return new SingboxOutbound
        {
            Type = "tuic",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Uuid = server.Uuid,
            Password = server.Password,
            CongestionControl = server.CongestionControl,
            UdpRelayMode = server.UdpRelayMode,
            Tls = tls
        };
    }

    private static async Task<SingboxOutbound> BuildShadowsocksAsync(ShadowsocksServer server)
    {
        return new SingboxOutbound
        {
            Type = "shadowsocks",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Method = server.EncryptMethod,
            Password = server.Password,
            Plugin = ConvertSsPlugin(server.Plugin),
            PluginOpts = server.PluginOption.ValueOrDefault()
        };
    }

    private static async Task<SingboxOutbound> BuildTrojanAsync(TrojanServer server)
    {
        return new SingboxOutbound
        {
            Type = "trojan",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Password = server.Password,
            Tls = BuildRequiredTls(server.Host.ValueOrDefault() ?? server.Hostname)
        };
    }

    private static async Task<SingboxOutbound> BuildVLessAsync(VLESSServer server)
    {
        return new SingboxOutbound
        {
            Type = "vless",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Uuid = V2rayConfigUtils.getUUID(server.UserID),
            Flow = server.Flow.ValueOrDefault(),
            PacketEncoding = ConvertPacketEncoding(server.PacketEncoding),
            Tls = BuildOptionalTls(server),
            Transport = BuildTransport(server)
        };
    }

    private static async Task<SingboxOutbound> BuildVMessAsync(VMessServer server)
    {
        return new SingboxOutbound
        {
            Type = "vmess",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Uuid = V2rayConfigUtils.getUUID(server.UserID),
            Security = server.EncryptMethod,
            AlterId = server.AlterID,
            PacketEncoding = ConvertPacketEncoding(server.PacketEncoding),
            Tls = BuildOptionalTls(server),
            Transport = BuildTransport(server)
        };
    }

    private static async Task<SingboxOutbound> BuildSocks5Async(Socks5Server server)
    {
        return new SingboxOutbound
        {
            Type = "socks",
            Version = "5",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            Username = server.Username.ValueOrDefault(),
            Password = server.Password.ValueOrDefault()
        };
    }

    private static async Task<SingboxOutbound> BuildSSHAsync(SSHServer server)
    {
        var hostKey = server.PublicKey.ValueOrDefault();

        return new SingboxOutbound
        {
            Type = "ssh",
            Server = await server.AutoResolveHostnameAsync(),
            ServerPort = server.Port,
            User = server.User,
            Password = server.Password.ValueOrDefault(),
            PrivateKey = server.PrivateKey.ValueOrDefault(),
            HostKey = hostKey != null ? new[] { hostKey } : null
        };
    }

    private static async Task<SingboxWireGuardEndpoint> BuildWireGuardEndpointAsync(WireGuardServer server)
    {
        var addresses = server.LocalAddresses.SplitOrDefault()?.Select(NormalizeCidr).ToList() ?? new List<string> { "172.16.0.2/32" };

        var peer = new SingboxWireGuardPeer
        {
            Address = await server.AutoResolveHostnameAsync(),
            Port = server.Port,
            PublicKey = server.PeerPublicKey,
            // 全零 pre-shared key 等价于无 PSK（wireguard-go 语义）
            PreSharedKey = server.PreSharedKey.ValueOrDefault() ?? Convert.ToBase64String(new byte[32])
        };
        peer.AllowedIps.Add("0.0.0.0/0");
        peer.AllowedIps.Add("::/0");

        var endpoint = new SingboxWireGuardEndpoint
        {
            Mtu = server.MTU,
            PrivateKey = server.PrivateKey
        };
        endpoint.Address.AddRange(addresses);
        endpoint.Peers.Add(peer);

        return endpoint;
    }

    private static SingboxTls BuildRequiredTls(string serverName)
    {
        return new SingboxTls
        {
            Enabled = true,
            ServerName = serverName,
            Insecure = Global.Settings.V2RayConfig.AllowInsecure
        };
    }

    private static SingboxTls? BuildOptionalTls(VMessServer server)
    {
        if (server.TLSSecureType == "none")
            return null;

        var serverName = server.ServerName.ValueOrDefault() ?? server.Host.SplitOrDefault()?.FirstOrDefault() ?? server.Hostname;

        var tls = BuildRequiredTls(serverName);

        // reality 出站强制要求 utls 指纹与公钥，缺失在生成侧显式失败而非让内核 check 兜底
        if (server is VLESSServer vless && vless.TLSSecureType == "reality")
        {
            if (string.IsNullOrWhiteSpace(vless.RealityPublicKey))
                throw new MessageException(I18N.T("kernel.vlessRealityRequiresRealityPublickey"));

            tls.Utls = new SingboxUtls
            {
                Enabled = true,
                Fingerprint = vless.Fingerprint.ValueOrDefault() ?? "chrome"
            };
            tls.Reality = new SingboxReality
            {
                Enabled = true,
                PublicKey = vless.RealityPublicKey,
                ShortId = vless.RealityShortId.ValueOrDefault()
            };
        }

        return tls;
    }

    private static SingboxTransport? BuildTransport(VMessServer server)
    {
        switch (server.TransferProtocol)
        {
            case "tcp":
                if (server.FakeType != "none")
                    throw new MessageException(I18N.T("kernel.vmessVlessTcpHeaderObfuscation"));

                return null;
            case "ws":
            {
                var host = server.Host.ValueOrDefault();

                return new SingboxTransport
                {
                    Type = "ws",
                    Path = server.Path.ValueOrDefault(),
                    Headers = host == null ? null : new Dictionary<string, string> { ["Host"] = host }
                };
            }
            // sing-box 未实现 v2ray 的 mKCP 与 QUIC 传输（QUIC 无 v2ray 的额外加密/伪装支持），MainController 路由层将其导向 Xray 兜底
            case "kcp":
            case "quic":
                throw new MessageException(I18N.T("kernel.vmessVlessTransportNotSupported"));
            case "h2":
                return new SingboxTransport
                {
                    Type = "http",
                    Host = server.Host.SplitOrDefault()?.ToList(),
                    Path = server.Path.ValueOrDefault()
                };
            case "grpc":
                return new SingboxTransport
                {
                    Type = "grpc",
                    ServiceName = server.Path.ValueOrDefault()
                };
            default:
                throw new MessageException($"transfer protocol \"{server.TransferProtocol}\" is not supported by sing-box kernel");
        }
    }

    private static string ConvertSsPlugin(string? plugin)
    {
        return plugin switch
        {
            "simple-obfs" => "obfs-local",
            "obfs" => "obfs-local",
            _ => plugin.ValueOrDefault()!
        };
    }

    private static string? ConvertPacketEncoding(string packetEncoding)
    {
        return packetEncoding switch
        {
            "packet" => "packetaddr",
            "xudp" => "xudp",
            _ => null
        };
    }

    private static string NormalizeCidr(string address)
    {
        if (address.Contains('/'))
            return address;

        return address.Contains(':') ? $"{address}/128" : $"{address}/32";
    }
}
