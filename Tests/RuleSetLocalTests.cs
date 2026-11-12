using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Servers;
using Netch.Services;
using NetchGlobal = Netch.Global;
using Global = Netch.Global;
using Netch.Utils;

namespace Tests;

[TestClass]
[DoNotParallelize]
public class RuleSetLocalTests
{
    private Setting _settings = null!;
    private readonly List<Mode> _modes = new();

    [TestInitialize]
    public void Setup()
    {
        _settings = Netch.Global.Settings;
        _modes.AddRange(Netch.Global.Modes);
        Netch.Global.Settings = new Setting();
        Netch.Global.Modes.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Netch.Global.Settings = _settings;
        Netch.Global.Modes.Clear();
        Netch.Global.Modes.AddRange(_modes);
    }

    private static RuleSetConfig Local(string tag, params RuleSetEntry[] entries)
    {
        return new RuleSetConfig { Tag = tag, Type = RuleSetKind.Local, Entries = entries.ToList() };
    }

    [TestMethod]
    public void JsonCompat_OldFieldDefaultsRemote()
    {
        var options = Netch.Global.NewCustomJsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var config = JsonSerializer.Deserialize<RuleSetConfig>("""{"Tag":"a","Url":"http://x"}""", options)!;
        Assert.AreEqual(RuleSetKind.Remote, config.Type);
        Assert.IsNull(config.Entries);
        Assert.IsFalse(JsonSerializer.Serialize(config, options).Contains("Entries"));
    }

    [TestMethod]
    public void JsonCompat_LocalRoundTrip()
    {
        var options = Netch.Global.NewCustomJsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var original = Local("steam", new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "a.com" },
            new RuleSetEntry { Kind = RouteRuleDimension.Port, Value = "443" },
            new RuleSetEntry { Kind = RouteRuleDimension.IpCidr, Value = "::1" });
        var copy = JsonSerializer.Deserialize<RuleSetConfig>(JsonSerializer.Serialize(original, options), options)!;
        Assert.AreEqual(RuleSetKind.Local, copy.Type);
        Assert.AreEqual(3, copy.Entries!.Count);
        CollectionAssert.AreEqual(original.Entries.Select(e => e.Value).ToList(), copy.Entries.Select(e => e.Value).ToList());
    }

    [TestMethod]
    public void Expand_SingleDimSingleRule()
    {
        Netch.Global.Settings.RuleSets.Add(Local("steam",
            new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "a.com" },
            new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "b.com" }));
        var result = LocalRuleSetExpand.Expand(new[] { new RouteRule { RuleSet = new List<string> { "steam" }, Action = RouteRuleAction.Direct } });
        Assert.AreEqual(1, result.Count);
        CollectionAssert.AreEqual(new[] { "a.com", "b.com" }, result[0].DomainSuffix);
        Assert.AreEqual(RouteRuleAction.Direct, result[0].Action);
    }

    [TestMethod]
    public void Expand_MultiDimMultiRules()
    {
        Netch.Global.Settings.RuleSets.Add(Local("steam",
            new RuleSetEntry { Kind = RouteRuleDimension.Port, Value = "443" },
            new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "a.com" }));
        var result = LocalRuleSetExpand.Expand(new[] { new RouteRule { RuleSet = new List<string> { "steam" } } });
        Assert.AreEqual(2, result.Count);
        Assert.IsNotNull(result[0].DomainSuffix);
        Assert.IsNotNull(result[1].Port);
        Assert.IsTrue(result.All(r => r.Action == RouteRuleAction.Proxy));
    }

    [TestMethod]
    public void Expand_MixedRefsOrderKept()
    {
        Netch.Global.Settings.RuleSets.Add(Local("a", new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "a.com" }));
        Netch.Global.Settings.RuleSets.Add(Local("b", new RuleSetEntry { Kind = RouteRuleDimension.DomainSuffix, Value = "b.com" }));
        var result = LocalRuleSetExpand.Expand(new[] { new RouteRule { RuleSet = new List<string> { "a", "geosite-cn", "b" } } });
        Assert.AreEqual(3, result.Count);
        CollectionAssert.AreEqual(new[] { "a.com" }, result[0].DomainSuffix);
        CollectionAssert.AreEqual(new[] { "geosite-cn" }, result[1].RuleSet);
        CollectionAssert.AreEqual(new[] { "b.com" }, result[2].DomainSuffix);
    }

    [TestMethod]
    public void Expand_PureRemoteUntouched()
    {
        var rule = new RouteRule { RuleSet = new List<string> { "geosite-cn" } };
        Assert.AreSame(rule, LocalRuleSetExpand.Expand(new[] { rule }).Single());
    }

    [TestMethod]
    public void Expand_UnknownTagPassthrough()
    {
        var rule = new RouteRule { RuleSet = new List<string> { "unknown" } };
        Assert.AreSame(rule, LocalRuleSetExpand.Expand(new[] { rule }).Single());
        Assert.ThrowsException<MessageException>(() => RouteRuleValidator.Validate(rule));
    }

    [TestMethod]
    public void Expand_LocalAndUnknownTagPreserved()
    {
        Netch.Global.Settings.RuleSets.Add(Local("a", new RuleSetEntry { Value = "a.com" }));
        var result = LocalRuleSetExpand.Expand(new[] { new RouteRule { RuleSet = new List<string> { "a", "unknown" } } });
        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "unknown" }, result[1].RuleSet);
        Assert.ThrowsException<MessageException>(() => RouteRuleValidator.Validate(result[1]));
    }

    [TestMethod]
    public void Expand_EmptyLocalThrows()
    {
        Netch.Global.Settings.RuleSets.Add(Local("empty"));
        Assert.ThrowsException<MessageException>(() => LocalRuleSetExpand.Expand(new[] { new RouteRule { RuleSet = new List<string> { "empty" } } }));
    }

    [TestMethod]
    public void Exclusive_RefWithInlineThrows()
    {
        var rule = new RouteRule { RuleSet = new List<string> { "geosite-cn" }, DomainSuffix = new List<string> { "a.com" } };
        var e = Assert.ThrowsException<MessageException>(() => RouteRuleValidator.Validate(rule));
        Assert.AreEqual(I18N.T("kernel.ruleRefInlineExclusive"), e.Message);
    }

    [TestMethod]
    public void ExternalResource_SkipsLocal()
    {
        Netch.Global.Settings.RuleSets.Add(Local("local", new RuleSetEntry { Value = "a.com" }));
        Netch.Global.Settings.RuleSets.Add(new RuleSetConfig { Tag = "remote", Url = "https://example.com/a.srs" });
        var keys = ExternalResourceService.All.Select(r => r.Key).ToList();
        Assert.IsFalse(keys.Contains("ruleset.custom.local"));
        Assert.IsTrue(keys.Contains("ruleset.custom.remote"));
    }

    [TestMethod]
    public void RenameReferences_SyncsAll()
    {
        var dir = Path.Combine(Netch.Global.NetchDir, "mode", "Custom");
        Directory.CreateDirectory(dir);
        var paths = new[] { Path.Combine(dir, $"re-test-{Guid.NewGuid():N}.json"), Path.Combine(dir, $"re-test-{Guid.NewGuid():N}.json") };
        try
        {
            foreach (var path in paths)
            {
                var mode = new Redirector { FullName = path, Rules = new List<RouteRule> { new() { RuleSet = new List<string> { "old" } } } };
                mode.WriteFile();
                Netch.Global.Modes.Add(mode);
            }
            Assert.AreEqual(2, RuleSetReferenceService.RenameReferences("old", "new"));
            Assert.IsTrue(Netch.Global.Modes.All(m => ((Redirector)m).Rules![0].RuleSet![0] == "new"));
            foreach (var path in paths)
                Assert.AreEqual("new", ModeHelper.LoadMode(path) is Redirector mode ? mode.Rules![0].RuleSet![0] : null);
        }
        finally
        {
            foreach (var path in paths)
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            }
        }
    }

    [TestMethod]
    public async Task DisabledBuiltInOnly_DoesNotMatchAll()
    {
        var resource = ExternalResourceService.Find("ruleset.geosite-cn")!;
        Netch.Global.Settings.ExternalResources.Items.Add(new ExternalResourceState { Key = resource.Key, Disabled = true });
        var server = new ShadowsocksServer { Hostname = "localhost", Port = 8388, EncryptMethod = "aes-256-gcm", Password = "test" };
        var mode = new Redirector { FilterDNS = false, Rules = new List<RouteRule> { new() { RuleSet = new List<string> { "geosite-cn" } } } };
        var (config, _) = await SingboxConfigUtils.GenerateClientConfigAsync(new[] { server }, server, 19090, mode);
        Assert.IsFalse(config.Route.Rules.OfType<SingboxRouteRule>().Any(r => r.Action == "route" && !r.GetType().GetProperties().Any(p => p.GetValue(r) is System.Collections.ICollection c && c.Count > 0)));
        Assert.IsTrue(config.Route.Rules.OfType<SingboxRouteRule>().All(r => r.Action == "sniff" || r.Action == "hijack-dns"));
    }
}

