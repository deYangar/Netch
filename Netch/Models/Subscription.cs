namespace Netch.Models;

public class Subscription
{
    /// <summary>
    ///     启用状态
    /// </summary>
    public bool Enable { get; set; } = true;

    /// <summary>
    ///     链接
    /// </summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>
    ///     备注
    /// </summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>
    ///     User Agent
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    ///     节点域名解析 DoH 上游（完整 URL，可含自定义路径，如 https://host:port/api-query）。
    ///     非空时该订阅节点的域名解析逐个尝试 DoH，全部失败回退系统 DNS；空 = 系统 DNS（现状）
    /// </summary>
    public List<string> NodeDns { get; set; } = new();

    /// <summary>
    ///     Host 映射：源域名 → 目标域名（可填 IP）。键支持精确域名与 "+.suffix" 泛域名（域名自身及任意子域）。
    ///     订阅更新时命中节点的连接目标域名直接替换为目标（连接地址与 TLS SNI 一并跟随）——
    ///     Sparkle js 覆写改 proxies[].server 的等价语义；修改映射后需更新订阅生效
    /// </summary>
    public Dictionary<string, string> Hosts { get; set; } = new();
}