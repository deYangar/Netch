using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch;
using Netch.Models;
using Netch.Models.Modes;
using NetchGlobal = Netch.Global;
using Global = Netch.Global;
using Netch.Utils;

namespace Tests;

[TestClass]
[DoNotParallelize]
public class RouteRuleValidatorTests
{
    private Setting _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = Netch.Global.Settings;
        Netch.Global.Settings = new Setting();
    }

    [TestCleanup]
    public void Cleanup() => Netch.Global.Settings = _settings;

    [TestMethod]
    public void Cidr_ValidAndInvalid()
    {
        foreach (var valid in new[] { "1.2.3.0/24", "1.2.3.4", "::/64", "::1", "0.0.0.0/0", "1.2.3.4/32" })
            Assert.IsNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.IpCidr, valid), valid);
        foreach (var invalid in new[] { "1.2.3.4/33", "::/129", "1.2.3.4/-1", "1.2.3.4/24/1", "a/24" })
            Assert.IsNotNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.IpCidr, invalid), invalid);
    }

    [TestMethod]
    public void Port_ValidAndInvalid()
    {
        foreach (var valid in new[] { "443", "1000:2000", "1", "65535" })
            Assert.IsNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.Port, valid), valid);
        foreach (var invalid in new[] { ":200", "abc", "0", "65536", "200:100", "999999999999" })
            Assert.IsNotNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.Port, invalid), invalid);
    }

    [TestMethod]
    public void Protocol_ValidAndInvalid()
    {
        Assert.IsNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.Protocol, "tls"));
        Assert.IsNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.Protocol, "TLS"));
        Assert.IsNotNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.Protocol, "http2"));
    }

    [TestMethod]
    public void TagReferencable_IncludesDisabledBuiltInAndBothCustomKinds()
    {
        Netch.Global.Settings.UseBuiltInRuleSets = false;
        Assert.IsTrue(RouteRuleValidator.TagReferencable("geosite-cn"));
        Netch.Global.Settings.RuleSets.Add(new RuleSetConfig { Tag = "remote" });
        Netch.Global.Settings.RuleSets.Add(new RuleSetConfig { Tag = "local", Type = RuleSetKind.Local });
        Assert.IsTrue(RouteRuleValidator.TagReferencable("remote"));
        Assert.IsTrue(RouteRuleValidator.TagReferencable("local"));
        Assert.IsFalse(RouteRuleValidator.TagReferencable("unknown"));
    }

    [TestMethod]
    public void Regex_InvalidIsRejected()
    {
        Assert.IsNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.DomainRegex, "^a.+$"));
        Assert.IsNotNull(RouteRuleValidator.ValidateValue(RouteRuleDimension.DomainRegex, "["));
    }
}
