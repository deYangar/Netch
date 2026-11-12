using System.Net;
using Netch.Models;
using Netch.Models.Modes;

namespace Netch.Utils;

/// <summary>规则集批量导入：mihomo、通配域名、裸域名及裸 IP/CIDR（RE-2§3.5，D5）</summary>
public static class BulkRuleParser
{
    public static (List<RuleSetEntry> entries, int skipped) Parse(string text)
    {
        var entries = new List<RuleSetEntry>();
        var skipped = 0;
        foreach (var source in text.Split('\n'))
        {
            var line = source.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            RuleSetEntry? entry = null;
            var comma = line.IndexOf(',');
            if (comma > 0)
            {
                var kind = line[..comma].Trim().ToUpperInvariant() switch
                {
                    "DOMAIN-SUFFIX" => RouteRuleDimension.DomainSuffix,
                    "DOMAIN" => RouteRuleDimension.Domain,
                    "DOMAIN-KEYWORD" => RouteRuleDimension.DomainKeyword,
                    "DOMAIN-REGEX" => RouteRuleDimension.DomainRegex,
                    "IP-CIDR" or "IP-CIDR6" => RouteRuleDimension.IpCidr,
                    "PORT" => RouteRuleDimension.Port,
                    "PROTOCOL" => RouteRuleDimension.Protocol,
                    _ => (RouteRuleDimension?)null
                };
                var value = line[(comma + 1)..].Trim();
                if (kind != null && value.Length > 0 && RouteRuleValidator.ValidateValue(kind.Value, value) == null)
                    entry = new RuleSetEntry { Kind = kind.Value, Value = value };
            }
            else if (line.StartsWith("*.") || line.EndsWith(".*"))
            {
                var value = line.StartsWith("*.") ? line[2..] : line[..^2];
                if (ValidWildcardSuffix(value))
                    entry = new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = value };
            }
            else if (RouteRuleValidator.ValidateValue(RouteRuleDimension.IpCidr, line) == null &&
                     IPAddress.TryParse(line.Split('/')[0], out _))
            {
                entry = new RuleSetEntry { Kind = RouteRuleDimension.IpCidr, Value = line };
            }
            else if (ValidDomain(line))
            {
                entry = new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = line };
            }

            if (entry == null)
                skipped++;
            else
                entries.Add(entry);
        }

        return (entries, skipped);
    }

    private static bool ValidWildcardSuffix(string value)
    {
        return value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
               !value.StartsWith('.') && !value.EndsWith('.') &&
               (Uri.CheckHostName(value) == UriHostNameType.Dns || !value.Contains('.'));
    }

    private static bool ValidDomain(string value)
    {
        return value.Contains('.') && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '*') &&
               !value.EndsWith('/') && !value.EndsWith(':') &&
               (Uri.CheckHostName(value) == UriHostNameType.Dns || value.Contains('*'));
    }
}

