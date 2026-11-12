using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;
using Netch.Localization;

namespace Netch.Utils;

/// <summary>
///     Local 规则集展开（RE-1§2.3，D1）：引用 Local 集的 RouteRule 在原位置替换为
///     相邻多条同 Action 内联规则。按引用顺序逐集展开，集内按维度枚举序聚合。
/// </summary>
public static class LocalRuleSetExpand
{
    public static List<RouteRule> Expand(IEnumerable<RouteRule> rules)
    {
        var result = new List<RouteRule>();
        foreach (var rule in rules)
        {
            if (rule.RuleSet is not { Count: > 0 } || !rule.RuleSet.Any(IsLocal))
            {
                result.Add(rule);
                continue;
            }

            foreach (var tag in rule.RuleSet)
            {
                var local = Global.Settings.RuleSets.FirstOrDefault(r => r.Tag == tag && r.Type == RuleSetKind.Local);
                if (local == null)
                {
                    // Remote、内置和未知 tag 均原样保留，由既有校验决定其可用性
                    result.Add(new RouteRule { RuleSet = new List<string> { tag }, Action = rule.Action });
                    continue;
                }

                if (local.Entries is not { Count: > 0 })
                    throw new MessageException(I18N.TF("rulesets.local.entryEmpty", 1));

                var grouped = new Dictionary<RouteRuleDimension, List<string>>();
                foreach (var entry in local.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Value))
                        throw new MessageException(I18N.TF("rulesets.local.entryEmpty", local.Entries.IndexOf(entry) + 1));

                    if (!grouped.TryGetValue(entry.Kind, out var values))
                        grouped[entry.Kind] = values = new List<string>();
                    if (!values.Contains(entry.Value))
                        values.Add(entry.Value);
                }

                foreach (var dim in Enum.GetValues<RouteRuleDimension>())
                {
                    if (!grouped.TryGetValue(dim, out var values))
                        continue;

                    var expanded = new RouteRule { Action = rule.Action };
                    expanded.Set(dim, values);
                    result.Add(expanded);
                }
            }
        }

        return result;
    }

    private static bool IsLocal(string tag)
    {
        return !BuiltInRuleSets.IsRegistered(tag) && Global.Settings.RuleSets.Any(r => r.Tag == tag && r.Type == RuleSetKind.Local);
    }
}

