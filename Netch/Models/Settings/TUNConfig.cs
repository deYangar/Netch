namespace Netch.Models;

/// <summary>
///     TUN 模式配置（sing-box tun 入站）：地址/网关/DNS 等旧字段已随 tun2socks 链路退役
///     （auto_route/DNS 劫持全由内核接管），仅保留排除段与内核栈语义
/// </summary>
public class TUNConfig
{
    /// <summary>
    ///     内核支持的 tun 协议栈
    /// </summary>
    public static readonly IReadOnlySet<string> Stacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "gvisor", "mixed"
    };

    /// <summary>
    ///     sing-box tun 协议栈：system / gvisor / mixed（mixed=TCP system + UDP gvisor）
    /// </summary>
    public string Stack { get; set; } = "mixed";

    /// <summary>
    ///     严格路由（防 DNS 泄漏）；与局域网共享类软件冲突时可关
    /// </summary>
    public bool StrictRoute { get; set; } = true;

    /// <summary>
    ///     Global bypass IPs（route_exclude_address：根本不进 tun 的段）
    /// </summary>
    public List<string> BypassIPs { get; set; } = new();
}