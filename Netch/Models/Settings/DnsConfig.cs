namespace Netch.Models;

/// <summary>
///     进程模式 DNS 策略（sing-box 内核的 dns 模块接管；v2ray/Xray 内核仍走 Redirector 的 DNSHost/DNSProxy）
/// </summary>
public class DnsConfig
{
    /// <summary>
    ///     Auto: geosite-cn→国内直连，其余经节点 DoH；ProxyAll: 全部经节点；DirectAll: 全部国内直连；Custom: 自定义上游
    /// </summary>
    public string Policy { get; set; } = DnsPolicies.Auto;

    /// <summary>
    ///     国内上游，IP[:port]，UDP 查询直连。默认 223.5.5.5
    /// </summary>
    public string ChinaDns { get; set; } = "223.5.5.5";

    /// <summary>
    ///     远程上游，DoH 域名或 IP（https）。默认 1.1.1.1。Custom 策略下可为 udp://ip:port
    /// </summary>
    public string RemoteDns { get; set; } = "1.1.1.1";
}

public static class DnsPolicies
{
    public const string Auto = "Auto";
    public const string ProxyAll = "ProxyAll";
    public const string DirectAll = "DirectAll";
    public const string Custom = "Custom";

    public static readonly List<string> All = new() { Auto, ProxyAll, DirectAll, Custom };
}
