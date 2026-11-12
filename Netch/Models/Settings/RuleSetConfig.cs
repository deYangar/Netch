using Netch.Models.Modes;

namespace Netch.Models;

public enum RuleSetKind
{
    /// <summary>远程 .srs（既有形态）：URL 下载，生成 route.rule_set 定义（RE-1§2.1）</summary>
    Remote = 0,
    /// <summary>本地条目集（RE 新增）：条目存 settings.json，生成时在引用位置展开为内联规则</summary>
    Local = 1
}

/// <summary>本地规则集条目：一维度一值；同维度多值=多条目，生成时聚合（RE-1§2.3）</summary>
public class RuleSetEntry
{
    public RouteRuleDimension Kind { get; set; } = RouteRuleDimension.DomainSuffix;
    public string Value { get; set; } = "";
}

/// <summary>
///     用户自定义规则集：Remote 汇入 route.rule_set，Local 在引用位置展开为内联规则
/// </summary>
public class RuleSetConfig
{
    /// <summary>
    ///     规则集 tag，路由/DNS 规则以 tag 引用；与内置 tag 冲突时内置优先并告警
    /// </summary>
    public string Tag { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>
    ///     sing-box update_interval，"1d" 形式；空用默认
    /// </summary>
    public string? UpdateInterval { get; set; }

    /// <summary>形态；缺省 0=Remote，旧 settings.json 无此字段反序列化即 Remote，零迁移（RE-1§2.1）</summary>
    public RuleSetKind Type { get; set; } = RuleSetKind.Remote;

    /// <summary>Local 形态条目；Remote 恒 null（序列化 WhenWritingNull 不落盘）</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleSetEntry>? Entries { get; set; }
}
