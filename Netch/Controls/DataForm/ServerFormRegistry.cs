using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Netch.Controls.DataForm;

/// <summary>
///     11 协议字段清单（06§A3：每条平移自旧 XxxForm 构造函数的 Create* 调用，旧文件删除前已逐条对照）。
///     Key 直接用旧控件 name（serverForm.&lt;key&gt; 词表 en=旧 remark 文案）。
/// </summary>
public static class ServerFormRegistry
{
    /// <summary>基类三字段：备注 / 地址:端口（同行）</summary>
    public static IEnumerable<FieldDescriptor> Common(Server _) =>
    [
        F.Text("remark", s => s.Remark, (s, v) => s.Remark = v as string ?? ""),
        F.Text("hostname", s => s.Hostname, (s, v) => s.Hostname = v as string ?? "", v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
        F.Text("port", s => s.Port.ToString(), (s, v) => s.Port = ushort.Parse(v as string ?? "0"), F.ValidatePort, 0.4),
    ];

    public static IReadOnlyDictionary<string, ServerFormDefinition> All { get; } = new Dictionary<string, ServerFormDefinition>
    {
        ["SS"] = Def<ShadowsocksServer>("Shadowsocks", s =>
        [
            ..Common(s),
            F.Password("ss.password", x => ((ShadowsocksServer)x).Password, (x, v) => ((ShadowsocksServer)x).Password = v as string ?? "",
                v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
            F.Select("ss.encryptMethod", x => ((ShadowsocksServer)x).EncryptMethod, (x, v) => ((ShadowsocksServer)x).EncryptMethod = v as string ?? "",
                SSGlobal.EncryptMethods),
            F.Text("ss.plugin", x => ((ShadowsocksServer)x).Plugin, (x, v) => ((ShadowsocksServer)x).Plugin = v as string ?? ""),
            F.Text("ss.pluginOptions", x => ((ShadowsocksServer)x).PluginOption, (x, v) => ((ShadowsocksServer)x).PluginOption = v as string ?? ""),
        ]),

        ["SSR"] = Def<ShadowsocksRServer>("ShadowsocksR", s =>
        [
            ..Common(s),
            F.Password("ssr.password", x => ((ShadowsocksRServer)x).Password, (x, v) => ((ShadowsocksRServer)x).Password = v as string ?? "",
                v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
            F.Select("ssr.encryptMethod", x => ((ShadowsocksRServer)x).EncryptMethod, (x, v) => ((ShadowsocksRServer)x).EncryptMethod = v as string ?? "",
                SSRGlobal.EncryptMethods),
            F.Select("ssr.protocol", x => ((ShadowsocksRServer)x).Protocol, (x, v) => ((ShadowsocksRServer)x).Protocol = v as string ?? "",
                SSRGlobal.Protocols),
            F.Text("ssr.protocolParam", x => ((ShadowsocksRServer)x).ProtocolParam, (x, v) => ((ShadowsocksRServer)x).ProtocolParam = v as string ?? ""),
            F.Select("ssr.obfs", x => ((ShadowsocksRServer)x).OBFS, (x, v) => ((ShadowsocksRServer)x).OBFS = v as string ?? "",
                SSRGlobal.OBFSs),
            F.Text("ssr.obfsParam", x => ((ShadowsocksRServer)x).OBFSParam, (x, v) => ((ShadowsocksRServer)x).OBFSParam = v as string ?? ""),
            F.Notice("ssr.deprecation"),
        ]),

        ["SOCKS"] = Def<Socks5Server>("Socks5", s =>
        [
            ..Common(s),
            F.Text("socks5.username", x => ((Socks5Server)x).Username.ValueOrDefault(), (x, v) => ((Socks5Server)x).Username = v as string),
            F.Password("socks5.password", x => ((Socks5Server)x).Password.ValueOrDefault(), (x, v) => ((Socks5Server)x).Password = v as string),
            F.Select("socks5.version", x => ((Socks5Server)x).Version, (x, v) => ((Socks5Server)x).Version = v as string ?? "",
                SOCKSGlobal.Versions),
            F.Text("socks5.remoteHostname", x => ((Socks5Server)x).RemoteHostname.ValueOrDefault(), (x, v) => ((Socks5Server)x).RemoteHostname = v as string),
        ]),

        ["SSH"] = Def<SSHServer>("SSH", s =>
        [
            ..Common(s),
            F.Text("ssh.user", x => ((SSHServer)x).User, (x, v) => ((SSHServer)x).User = v as string ?? ""),
            F.Password("ssh.password", x => ((SSHServer)x).Password, (x, v) => ((SSHServer)x).Password = v as string ?? ""),
            F.Password("ssh.privateKey", x => ((SSHServer)x).PrivateKey, (x, v) => ((SSHServer)x).PrivateKey = v as string ?? ""),
            F.Text("ssh.publicKey", x => ((SSHServer)x).PublicKey, (x, v) => ((SSHServer)x).PublicKey = v as string ?? ""),
        ]),

        ["Trojan"] = Def<TrojanServer>("Trojan", s =>
        [
            ..Common(s),
            F.Password("trojan.password", x => ((TrojanServer)x).Password, (x, v) => ((TrojanServer)x).Password = v as string ?? ""),
            F.Text("trojan.host", x => ((TrojanServer)x).Host, (x, v) => ((TrojanServer)x).Host = v as string ?? ""),
            F.Select("trojan.tlsSecure", x => ((TrojanServer)x).TLSSecureType, (x, v) => ((TrojanServer)x).TLSSecureType = v as string ?? "",
                VLESSGlobal.TLSSecure),
        ]),

        ["TUIC"] = Def<TUICServer>("TUIC", s =>
        [
            ..Common(s),
            F.Text("tuic.uuid", x => ((TUICServer)x).Uuid, (x, v) => ((TUICServer)x).Uuid = v as string ?? "",
                v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
            F.Password("tuic.password", x => ((TUICServer)x).Password, (x, v) => ((TUICServer)x).Password = v as string ?? ""),
            F.Text("tuic.host", x => ((TUICServer)x).Host, (x, v) => ((TUICServer)x).Host = v as string ?? ""),
            F.Select("tuic.congestionControl", x => ((TUICServer)x).CongestionControl, (x, v) => ((TUICServer)x).CongestionControl = v as string ?? "",
                TUICGlobal.CongestionControls),
            F.Select("tuic.udpRelayMode", x => ((TUICServer)x).UdpRelayMode, (x, v) => ((TUICServer)x).UdpRelayMode = v as string ?? "",
                TUICGlobal.UdpRelayModes),
            F.Text("tuic.alpn", x => ((TUICServer)x).Alpn, (x, v) => ((TUICServer)x).Alpn = v as string ?? ""),
        ]),

        ["VLESS"] = Def<VLESSServer>("VLESS", s =>
        [
            ..Common(s),
            ..VlessVmessCommon<VLESSServer>("vless.", x => (VLESSServer)(object)x),
        ]),

        ["VMess"] = Def<VMessServer>("VMess", s =>
        [
            ..Common(s),
            ..VlessVmessCommon<VMessServer>("vmess.", x => (VMessServer)(object)x),
        ]),

        ["WireGuard"] = Def<WireGuardServer>("WireGuard", s =>
        [
            ..Common(s),
            F.Text("wg.localAddresses", x => ((WireGuardServer)x).LocalAddresses, (x, v) => ((WireGuardServer)x).LocalAddresses = v as string ?? ""),
            F.Text("wg.peerPublicKey", x => ((WireGuardServer)x).PeerPublicKey, (x, v) => ((WireGuardServer)x).PeerPublicKey = v as string ?? ""),
            F.Password("wg.privateKey", x => ((WireGuardServer)x).PrivateKey, (x, v) => ((WireGuardServer)x).PrivateKey = v as string ?? ""),
            F.Text("wg.preSharedKey", x => ((WireGuardServer)x).PreSharedKey, (x, v) => ((WireGuardServer)x).PreSharedKey = v as string ?? ""),
            F.Text("wg.mtu", x => ((WireGuardServer)x).MTU.ToString(), (x, v) => ((WireGuardServer)x).MTU = int.Parse(v as string ?? "0"), F.ValidateInt, 0.4),
        ]),

        ["AnyTLS"] = Def<AnytlsServer>("AnyTLS", s =>
        [
            ..Common(s),
            F.Password("anytls.password", x => ((AnytlsServer)x).Password, (x, v) => ((AnytlsServer)x).Password = v as string ?? "",
                v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
            F.Text("anytls.host", x => ((AnytlsServer)x).Host, (x, v) => ((AnytlsServer)x).Host = v as string ?? ""),
        ]),

        ["Hysteria2"] = Def<Hysteria2Server>("Hysteria2", s =>
        [
            ..Common(s),
            F.Password("hy2.password", x => ((Hysteria2Server)x).Password, (x, v) => ((Hysteria2Server)x).Password = v as string ?? "",
                v => v.IsNullOrWhiteSpace() ? F.ErrorRequired : null),
            F.Text("hy2.host", x => ((Hysteria2Server)x).Host, (x, v) => ((Hysteria2Server)x).Host = v as string ?? ""),
            F.Text("hy2.upMbps", x => ((Hysteria2Server)x).UpMbps, (x, v) => ((Hysteria2Server)x).UpMbps = int.TryParse(v as string, out var up) ? up : 0,
                v => v is string su && (su.Length == 0 || int.TryParse(su, out _)) ? null : F.ErrorInt, 0.4),
            F.Text("hy2.downMbps", x => ((Hysteria2Server)x).DownMbps, (x, v) => ((Hysteria2Server)x).DownMbps = int.TryParse(v as string, out var down) ? down : 0,
                v => v is string sd && (sd.Length == 0 || int.TryParse(sd, out _)) ? null : F.ErrorInt, 0.4),
            F.Password("hy2.obfsPassword", x => ((Hysteria2Server)x).ObfsPassword, (x, v) => ((Hysteria2Server)x).ObfsPassword = v as string ?? ""),
        ]),
    };

    /// <summary>VLESS/VMess 共有字段组（两协议同名属性、选项表同源 VMessGlobal）</summary>
    private static IEnumerable<FieldDescriptor> VlessVmessCommon<TServer>(string prefix, Func<Server, TServer> cast) where TServer : VMessServer
    {
        yield return F.Text(prefix + "sni", x => cast(x).ServerName, (x, v) => cast(x).ServerName = v as string ?? "");

        var isVless = typeof(TServer) == typeof(VLESSServer);
        if (isVless)
        {
            yield return F.Text("vless.uuid", x => ((VLESSServer)(object)cast(x)).UserID,
                (x, v) => ((VLESSServer)(object)cast(x)).UserID = v as string ?? "");
            yield return F.Select("vless.flow", x => ((VLESSServer)(object)cast(x)).Flow,
                (x, v) => ((VLESSServer)(object)cast(x)).Flow = v as string ?? "", VLESSGlobal.FlowTypes);
            yield return F.Text("vless.fingerprint", x => ((VLESSServer)(object)cast(x)).Fingerprint,
                (x, v) => ((VLESSServer)(object)cast(x)).Fingerprint = v as string ?? "");
            yield return F.Text("vless.realityPublicKey", x => ((VLESSServer)(object)cast(x)).RealityPublicKey,
                (x, v) => ((VLESSServer)(object)cast(x)).RealityPublicKey = v as string ?? "");
            yield return F.Text("vless.realityShortId", x => ((VLESSServer)(object)cast(x)).RealityShortId,
                (x, v) => ((VLESSServer)(object)cast(x)).RealityShortId = v as string ?? "");
            yield return F.Text("vless.encryptMethod", x => ((VLESSServer)(object)cast(x)).EncryptMethod,
                (x, v) => ((VLESSServer)(object)cast(x)).EncryptMethod = string.IsNullOrWhiteSpace(v as string) ? "none" : v as string ?? "none");
        }
        else
        {
            yield return F.Text("vmess.userId", x => cast(x).UserID, (x, v) => cast(x).UserID = v as string ?? "");
            yield return F.Text("vmess.alterId", x => cast(x).AlterID.ToString(),
                (x, v) => cast(x).AlterID = int.Parse(v as string ?? "0"), F.ValidateInt, 0.4);
            yield return F.Select("vmess.encryptMethod", x => cast(x).EncryptMethod,
                (x, v) => cast(x).EncryptMethod = v as string ?? "", VMessGlobal.EncryptMethods);
        }

        yield return F.Select(prefix + "transferProtocol", x => cast(x).TransferProtocol,
            (x, v) => cast(x).TransferProtocol = v as string ?? "", VMessGlobal.TransferProtocols);
        yield return F.Select(prefix + "packetEncoding", x => cast(x).PacketEncoding,
            (x, v) => cast(x).PacketEncoding = v as string ?? "", VMessGlobal.PacketEncodings);
        yield return F.Select(prefix + "fakeType", x => cast(x).FakeType,
            (x, v) => cast(x).FakeType = v as string ?? "", VMessGlobal.FakeTypes);
        yield return F.Text(prefix + "host", x => cast(x).Host, (x, v) => cast(x).Host = v as string ?? "");
        yield return F.Text(prefix + "path", x => cast(x).Path, (x, v) => cast(x).Path = v as string ?? "");
        yield return F.Select(prefix + "quicSecurity", x => cast(x).QUICSecure,
            (x, v) => cast(x).QUICSecure = v as string ?? "", VMessGlobal.QUIC);
        yield return F.Text(prefix + "quicSecret", x => cast(x).QUICSecret, (x, v) => cast(x).QUICSecret = v as string ?? "");
        yield return F.Select(prefix + "useMux", x => cast(x).UseMux switch { null => "", true => "true", false => "false" },
            (x, v) => cast(x).UseMux = (v as string) switch { "true" => true, "false" => false, _ => null },
            new[] { "", "true", "false" });
        yield return F.Select(prefix + "tlsSecure", x => cast(x).TLSSecureType,
            (x, v) => cast(x).TLSSecureType = v as string ?? "", isVless ? VLESSGlobal.TLSSecure : VMessGlobal.TLSSecure);
    }

    private static ServerFormDefinition Def<T>(string type, Func<Server, IEnumerable<FieldDescriptor>> build) where T : Server
        => new() { ProtocolType = type, Fields = build(null!).ToList() };
}
