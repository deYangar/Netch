namespace Netch.Enums;

/// <summary>
///     Server.Delay 的测量来源：两者语义不同（Clash API 经出站访问测试地址的全链路延迟；
///     TCPing/ICMP 仅本机到节点服务器的握手延迟），显示层据此区分，且运行期互不覆盖
/// </summary>
public enum DelaySource
{
    None,

    /// <summary>本机到节点服务器的 TCPing/ICMPing（Server.PingAsync）</summary>
    TCPing,

    /// <summary>经节点出站的真实访问延迟（Clash API，仅多节点 sing-box 实例运行期可用）</summary>
    ClashApi
}
