using System.Net;
using Netch;
using System.Text.RegularExpressions;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;

namespace Netch.Utils;

/// <summary>编辑器与生成层共用的规则校验（RE-1§2.6）</summary>
public static class RouteRuleValidator
{
    public static readonly IReadOnlyList<string> SniffableProtocols =
        new[] { "http", "tls", "quic", "dns", "stun", "bittorrent", "dtls", "ssh", "rdp", "ntp" };

    /// <summary>单维度值的格式校验；返回 null=合法，否则为原有错误词条 key</summary>
    public static string? ValidateValue(RouteRuleDimension dim, string value)
    {
        switch (dim)
        {
            case RouteRuleDimension.DomainRegex:
                try
                {
                    _ = new Regex(value);
                }
                catch (ArgumentException)
                {
                    return "kernel.invalidDomainRegex0";
                }
                break;
            case RouteRuleDimension.IpCidr:
                if (!ValidCidr(value))
                    return "kernel.invalidIpCidr0";
                break;
            case RouteRuleDimension.Port:
                if (!ValidPort(value))
                    return "kernel.invalidPortRange0Expected";
                break;
            case RouteRuleDimension.Protocol:
                if (!SniffableProtocols.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return "kernel.unknownSniffedProtocol0Supported";
                break;
        }

        return null;
    }

    /// <summary>tag 是否已注册；内置禁用态仍合法，生成时按既有口径摘除</summary>
    public static bool TagReferencable(string tag)
    {
        return BuiltInRuleSets.IsRegistered(tag) || Global.Settings.RuleSets.Any(r => r.Tag == tag);
    }

    /// <summary>原始规则校验（互斥必须在 Local 展开前执行）</summary>
    public static void Validate(RouteRule rule)
    {
        if (rule.RuleSet is { Count: > 0 } && rule.Enabled().Any())
            throw new MessageException(I18N.T("kernel.ruleRefInlineExclusive"));

        if (!rule.HasMatcher)
            throw new MessageException(I18N.T("modeEditor.routeRuleRequiresAtLeast"));

        foreach (var dim in rule.Enabled())
            foreach (var value in rule.Get(dim)!)
            {
                var key = ValidateValue(dim, value);
                if (key == null)
                    continue;

                var args = dim == RouteRuleDimension.Protocol
                    ? new object[] { value, string.Join(", ", SniffableProtocols) }
                    : new object[] { value };
                throw new MessageException(I18N.TF(key, args));
            }

        foreach (var tag in rule.RuleSet ?? [])
            if (!TagReferencable(tag))
                throw new MessageException(I18N.TF("kernel.unknownRuleSet0", tag));
    }

    public static string NormalizeCidr(string cidr)
    {
        if (!ValidCidr(cidr))
            throw new MessageException(I18N.TF("kernel.invalidIpCidr0", cidr));
        return cidr;
    }

    public static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            throw new MessageException(I18N.TF("kernel.invalidPortRange0Expected", value));
        return port;
    }

    private static bool ValidCidr(string value)
    {
        var slash = value.IndexOf('/');
        if (slash != value.LastIndexOf('/'))
            return false;

        var address = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(address, out var ip))
            return false;

        if (slash < 0)
            return true;

        return int.TryParse(value[(slash + 1)..], out var prefix) &&
               prefix >= 0 && prefix <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    private static bool ValidPort(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 1 or > 2 || parts.Any(p => !int.TryParse(p, out var port) || port is < 1 or > 65535))
            return false;

        return parts.Length == 1 || int.Parse(parts[0]) <= int.Parse(parts[1]);
    }
}

