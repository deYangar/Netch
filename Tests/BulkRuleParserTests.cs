using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models.Modes;
using Netch.Utils;

namespace Tests;

[TestClass]
public class BulkRuleParserTests
{
    [TestMethod]
    public void BulkParser_MihomoStyle()
    {
        var (entries, skipped) = BulkRuleParser.Parse("DOMAIN-SUFFIX,x.com\nDOMAIN,a.x.com\ndomain-keyword,key\nDOMAIN-REGEX,^x.+$\nIP-CIDR,1.2.3.0/24\nIP-CIDR6,::/64\nPORT,443\nPROTOCOL,tls");
        Assert.AreEqual(8, entries.Count);
        Assert.AreEqual(0, skipped);
        Assert.AreEqual(RouteRuleDimension.Domain, entries[1].Kind);
        Assert.AreEqual(RouteRuleDimension.IpCidr, entries[5].Kind);
        Assert.AreEqual("::/64", entries[5].Value);
    }

    [TestMethod]
    public void BulkParser_WildcardAndBareDomain()
    {
        var (entries, skipped) = BulkRuleParser.Parse("*.x.com\nx.*\nbare.example.com");
        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(0, skipped);
        CollectionAssert.AreEqual(new[] { "x.com", "x", "bare.example.com" }, entries.Select(e => e.Value).ToList());
        Assert.IsTrue(entries.All(e => e.Kind == RouteRuleDimension.DomainSuffix));
    }

    [TestMethod]
    public void BulkParser_BareCidrAndV6()
    {
        var (entries, skipped) = BulkRuleParser.Parse("1.2.3.4\n1.2.3.0/24\n::1\n2001:db8::/32");
        Assert.AreEqual(4, entries.Count);
        Assert.AreEqual(0, skipped);
        Assert.IsTrue(entries.All(e => e.Kind == RouteRuleDimension.IpCidr));
    }

    [TestMethod]
    public void BulkParser_CommentBlankNotCounted_BadLineSkipped()
    {
        var (entries, skipped) = BulkRuleParser.Parse("\n# comment\nnot-a-domain\nIP-CIDR,999.1.1.1\nDOMAIN-SUFFIX,\n*.valid.example");
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(3, skipped);
    }
}

