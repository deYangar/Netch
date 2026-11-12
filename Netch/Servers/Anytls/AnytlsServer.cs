using Netch.Models;

namespace Netch.Servers;

public class AnytlsServer : Server
{
    public override string Type { get; } = "AnyTLS";

    public override string MaskedData()
    {
        return "";
    }

    /// <summary>
    ///     密码
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     TLS SNI（空=使用节点地址）
    /// </summary>
    public string? Host { get; set; }
}
