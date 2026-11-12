namespace Netch.Servers;

public class VLESSServer : VMessServer
{
    public override string Type { get; } = "VLESS";

    /// <summary>
    ///     加密方式
    /// </summary>
    public override string EncryptMethod { get; set; } = "none";

    /// <summary>
    ///     传输协议
    /// </summary>
    public override string TransferProtocol { get; set; } = VLESSGlobal.TransferProtocols[0];

    /// <summary>
    ///     伪装类型
    /// </summary>
    public override string FakeType { get; set; } = VLESSGlobal.FakeTypes[0];

    /// <summary>
    ///     流控（仅 TLS/Reality 下有效，如 xtls-rprx-vision）
    /// </summary>
    public string? Flow { get; set; } = VLESSGlobal.FlowTypes[0];

    /// <summary>
    ///     uTLS 指纹（Reality 下必配，缺省 chrome）
    /// </summary>
    public string? Fingerprint { get; set; } = "chrome";

    /// <summary>
    ///     Reality 公钥（pbk，reality 下必配）
    /// </summary>
    public string? RealityPublicKey { get; set; }

    /// <summary>
    ///     Reality Short ID（sid）
    /// </summary>
    public string? RealityShortId { get; set; }
}

public class VLESSGlobal
{
    public static readonly List<string> TLSSecure = new()
    {
        "none",
        "tls",
        "xtls",
        "reality"
    };

    public static readonly List<string> FlowTypes = new()
    {
        "",
        "xtls-rprx-vision"
    };

    public static List<string> FakeTypes => VMessGlobal.FakeTypes;

    public static List<string> TransferProtocols => VMessGlobal.TransferProtocols;

    public static List<string> QUIC => VMessGlobal.QUIC;
}