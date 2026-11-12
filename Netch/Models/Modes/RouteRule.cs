using System.Text.Json.Serialization;
using Netch.Localization;

namespace Netch.Models.Modes;

/// <summary>
///     路由层规则（模式内有序列表）：匹配器（至少一项）+ 动作，对应内核 route rule。
///     匹配器为空列表表示未启用该维度；Protocol 依赖入站嗅探结果。
/// </summary>
public class RouteRule
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainSuffix { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Domain { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainKeyword { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainRegex { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IpCidr { get; set; }

    /// <summary>
    ///     端口或端口范围，"443" / "1000:2000"，生成时拆分到 port / port_range
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Port { get; set; }

    /// <summary>
    ///     嗅探协议：http / tls / quic / dns / stun / bittorrent / dtls / ssh / rdp / ntp
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Protocol { get; set; }

    /// <summary>
    ///     引用的规则集 tag（定义在设置的规则集列表或内置清单，如 geosite-cn / geoip-cn）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RuleSet { get; set; }

    public RouteRuleAction Action { get; set; } = RouteRuleAction.Proxy;

    /// <summary>
    ///     是否启用了至少一个匹配维度（全空规则在生成侧视为配置错误）
    /// </summary>
    [JsonIgnore]
    public bool HasMatcher =>
        NonEmpty(DomainSuffix) || NonEmpty(Domain) || NonEmpty(DomainKeyword) || NonEmpty(DomainRegex) ||
        NonEmpty(IpCidr) || NonEmpty(Port) || NonEmpty(Protocol) || NonEmpty(RuleSet);

    [JsonIgnore]
    public IEnumerable<(string Kind, string Value)> Matchers =>
        (DomainSuffix ?? Enumerable.Empty<string>()).Select(v => (Kind: "DomainSuffix", v))
        .Concat((Domain ?? Enumerable.Empty<string>()).Select(v => (Kind: "Domain", v)))
        .Concat((DomainKeyword ?? Enumerable.Empty<string>()).Select(v => (Kind: "DomainKeyword", v)))
        .Concat((DomainRegex ?? Enumerable.Empty<string>()).Select(v => (Kind: "DomainRegex", v)))
        .Concat((IpCidr ?? Enumerable.Empty<string>()).Select(v => (Kind: "IpCidr", v)))
        .Concat((Port ?? Enumerable.Empty<string>()).Select(v => (Kind: "Port", v)))
        .Concat((Protocol ?? Enumerable.Empty<string>()).Select(v => (Kind: "Protocol", v)))
        .Concat((RuleSet ?? Enumerable.Empty<string>()).Select(v => (Kind: "RuleSet", v)));

    /// <summary>规则列表的只读摘要，不进入模式 JSON（RE-3§4.3）</summary>
    [JsonIgnore]
    public string SummaryText
    {
        get
        {
            var enabled = this.Enabled().ToList();
            if (enabled.Count == 0)
                return RuleSet is { Count: > 0 } ? I18N.TF("modeEditor.summary.ref", RuleSet.Count) : I18N.T("modeEditor.summary.emptyRule");
            var parts = enabled.Take(2).Select(d => $"{I18N.T($"dim.{d}")}×{this.Get(d)!.Count}").ToList();
            if (enabled.Count > 2)
                parts.Add(I18N.TF("modeEditor.summary.more", enabled.Count - 2));
            return string.Join(" + ", parts);
        }
    }

    [JsonIgnore]
    public string RuleSetText => RuleSet is { Count: > 0 } ? string.Join(", ", RuleSet) : "—";

    [JsonIgnore]
    public string ActionText => I18N.T($"modeEditor.action.{Action}");

    private static bool NonEmpty(IEnumerable<string>? list) => list != null && list.Any();
}

public enum RouteRuleAction
{
    Proxy,
    Direct,
    Block
}
