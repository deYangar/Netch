using Netch.Models;

namespace Netch.Servers;

public class TUICServer : Server
{
    public override string Type { get; } = "TUIC";

    public override string MaskedData()
    {
        return $"{CongestionControl} + {UdpRelayMode}";
    }

    /// <summary>
    ///     用户 UUID
    /// </summary>
    public string Uuid { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     用户密码
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     TLS SNI（空=使用节点地址）
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    ///     QUIC 拥塞控制算法
    /// </summary>
    public string CongestionControl { get; set; } = TUICGlobal.CongestionControls[0];

    /// <summary>
    ///     UDP 中继模式
    /// </summary>
    public string UdpRelayMode { get; set; } = TUICGlobal.UdpRelayModes[0];

    /// <summary>
    ///     TLS ALPN（空=内核默认 h3）
    /// </summary>
    public string? Alpn { get; set; }
}

public static class TUICGlobal
{
    public static readonly List<string> CongestionControls = new()
    {
        "bbr",
        "cubic",
        "new_reno"
    };

    public static readonly List<string> UdpRelayModes = new()
    {
        "native",
        "quic"
    };
}
