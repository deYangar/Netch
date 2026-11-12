using System.Text.Json.Serialization;
using Netch.Models.Modes;

namespace Netch.Models.Modes.TunMode;

public class TunMode : Mode
{
    public override ModeType Type => ModeType.TunMode;

    public List<string> Bypass { get; set; } = new();

    public List<string> Handle { get; set; } = new();

    /// <summary>
    ///     路由层规则（与进程模式同一 RouteRule 模型，sing-box route 执行，按序匹配）。
    ///     null/空 = 无规则纯全局；旧模式文件无此字段，Handle(IP 段) 的等价语义由生成层迁移。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RouteRule>? Rules { get; set; }
}