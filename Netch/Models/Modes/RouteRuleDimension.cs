namespace Netch.Models.Modes;

/// <summary>RouteRule 的匹配维度（RE-1§2.1）；与 RouteRule 属性一一对应</summary>
public enum RouteRuleDimension
{
    DomainSuffix,
    Domain,
    DomainKeyword,
    DomainRegex,
    IpCidr,
    Port,
    Protocol
}

public static class RouteRuleDimensionExtensions
{
    /// <summary>维度 → RouteRule 对应维度值列表的读写（展开/聚合共用）</summary>
    public static List<string>? Get(this RouteRule rule, RouteRuleDimension d) => d switch
    {
        RouteRuleDimension.DomainSuffix => rule.DomainSuffix,
        RouteRuleDimension.Domain => rule.Domain,
        RouteRuleDimension.DomainKeyword => rule.DomainKeyword,
        RouteRuleDimension.DomainRegex => rule.DomainRegex,
        RouteRuleDimension.IpCidr => rule.IpCidr,
        RouteRuleDimension.Port => rule.Port,
        RouteRuleDimension.Protocol => rule.Protocol,
        _ => null
    };

    public static void Set(this RouteRule rule, RouteRuleDimension d, List<string>? v)
    {
        switch (d)
        {
            case RouteRuleDimension.DomainSuffix: rule.DomainSuffix = v; break;
            case RouteRuleDimension.Domain: rule.Domain = v; break;
            case RouteRuleDimension.DomainKeyword: rule.DomainKeyword = v; break;
            case RouteRuleDimension.DomainRegex: rule.DomainRegex = v; break;
            case RouteRuleDimension.IpCidr: rule.IpCidr = v; break;
            case RouteRuleDimension.Port: rule.Port = v; break;
            case RouteRuleDimension.Protocol: rule.Protocol = v; break;
        }
    }

    /// <summary>RouteRule 上已启用的维度（非空即启用；RuleSet 维度不在枚举内，另算）</summary>
    public static IEnumerable<RouteRuleDimension> Enabled(this RouteRule rule) => Enum.GetValues<RouteRuleDimension>()
        .Where(d => rule.Get(d) is { Count: > 0 });
}
