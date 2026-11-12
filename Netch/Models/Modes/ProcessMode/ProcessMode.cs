using System.Text.Json.Serialization;

namespace Netch.Models.Modes.ProcessMode;

public class Redirector : Mode
{
    public override ModeType Type => ModeType.ProcessMode;

    #region Base

    public bool? FilterICMP { get; set; }

    public bool? FilterTCP { get; set; }

    public bool? FilterUDP { get; set; }

    public bool? FilterDNS { get; set; }

    public bool? FilterParent { get; set; }

    public int? ICMPDelay { get; set; }

    public bool? DNSProxy { get; set; }

    public bool? HandleOnlyDNS { get; set; }

    public string? DNSHost { get; set; }

    #endregion

    public bool FilterLoopback { get; set; } = false;

    public bool FilterIntranet { get; set; } = true;

    public List<string> Bypass { get; set; } = new();

    public List<string> Handle { get; set; } = new();

    /// <summary>
    ///     路由层规则（内核 route 执行，按序匹配）：决定进来的流量去哪。
    ///     null/空 = 不启用路由层，全部流量走默认出站（兼容存量模式文件）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RouteRule>? Rules { get; set; }
}