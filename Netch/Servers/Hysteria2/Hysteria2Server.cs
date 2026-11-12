using Netch.Models;

namespace Netch.Servers;

public class Hysteria2Server : Server
{
    public override string Type { get; } = "Hysteria2";

    public override string MaskedData()
    {
        return $"{UpMbps}/{DownMbps}";
    }

    /// <summary>
    ///     认证密码
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     TLS SNI（空=使用节点地址）
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    ///     上行带宽 Mbps（0=使用 BBR 自适应）
    /// </summary>
    public int UpMbps { get; set; }

    /// <summary>
    ///     下行带宽 Mbps（0=使用 BBR 自适应）
    /// </summary>
    public int DownMbps { get; set; }

    /// <summary>
    ///     salamander 混淆密码（空=不混淆）
    /// </summary>
    public string? ObfsPassword { get; set; }
}
