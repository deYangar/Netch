using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Tests;

/// <summary>
///     真实测速改造（2026-11-19）：设置迁移（ICMP→TCPing 一次性、测速地址非法回落）、
///     生效测速地址解析、临时实例配置后处理（去入站/去 urltest/修 selector.Default）
/// </summary>
[TestClass]
[DoNotParallelize]
public class SpeedtestRealDelayTests
{
    private Setting _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = Netch.Global.Settings;
        Netch.Global.Settings = new Setting();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Netch.Global.Settings = _settings;
    }

    #region CheckSetting 迁移

    [TestMethod]
    public void CheckSetting_MigratesLegacyIcmpToTcping_Once()
    {
        var settings = new Setting { ServerTCPing = false, ServerTCPingMigrated = false };
        Configuration.CheckSetting(settings);
        Assert.IsTrue(settings.ServerTCPing, "存量 false（旧版默认值）一次性翻转为 TCPing");
        Assert.IsTrue(settings.ServerTCPingMigrated, "迁移标记落盘");
    }

    [TestMethod]
    public void CheckSetting_RespectsExplicitIcmpAfterMigration()
    {
        var settings = new Setting { ServerTCPing = false, ServerTCPingMigrated = true };
        Configuration.CheckSetting(settings);
        Assert.IsFalse(settings.ServerTCPing, "已迁移后用户显式选 ICMP 不再动");
    }

    [TestMethod]
    public void CheckSetting_FallsBackInvalidDelayTestUrl()
    {
        foreach (var invalid in new[] { "", "   ", "gstatic.com/generate_204", "ftp://example.com/204" })
        {
            var settings = new Setting { DelayTestUrl = invalid };
            Configuration.CheckSetting(settings);
            Assert.AreEqual(Setting.DefaultDelayTestUrl, settings.DelayTestUrl, $"非法值 [{invalid}] 回落默认");
        }
    }

    [TestMethod]
    public void CheckSetting_KeepsValidDelayTestUrl()
    {
        const string custom = "https://cp.cloudflare.com/generate_204";
        var settings = new Setting { DelayTestUrl = custom };
        Configuration.CheckSetting(settings);
        Assert.AreEqual(custom, settings.DelayTestUrl);

        // 大写协议头合法（OrdinalIgnoreCase）
        settings = new Setting { DelayTestUrl = "HTTPS://example.com/204" };
        Configuration.CheckSetting(settings);
        Assert.AreEqual("HTTPS://example.com/204", settings.DelayTestUrl);
    }

    #endregion

    #region 生效测速地址

    [TestMethod]
    public void EffectiveDelayTestUrl_EmptyFallsBackToDefault()
    {
        Netch.Global.Settings.DelayTestUrl = "";
        Assert.AreEqual(Setting.DefaultDelayTestUrl, ClashApiClient.EffectiveDelayTestUrl());

        Netch.Global.Settings.DelayTestUrl = "  ";
        Assert.AreEqual(Setting.DefaultDelayTestUrl, ClashApiClient.EffectiveDelayTestUrl(), "空白串回落");
    }

    [TestMethod]
    public void EffectiveDelayTestUrl_CustomValueTrimmed()
    {
        Netch.Global.Settings.DelayTestUrl = " https://test.example/204 ";
        Assert.AreEqual("https://test.example/204", ClashApiClient.EffectiveDelayTestUrl());
    }

    #endregion

    #region 临时实例配置后处理

    [TestMethod]
    public void StripForSpeedtest_RemovesInboundsAndUrltest()
    {
        var config = new SingboxConfig();
        config.Inbounds.Add(new { type = "mixed", tag = "mixed-in" });
        var selector = new SingboxSelectorOutbound { Tag = "proxy", Default = "node-0" };
        selector.Outbounds.AddRange(new[] { "node-0", "node-1", "auto" });
        config.Outbounds = new List<object>
        {
            selector,
            new SingboxUrltestOutbound { Tag = "auto" },
            new { type = "shadowsocks", tag = "node-0" },
            new { type = "shadowsocks", tag = "node-1" }
        };

        SpeedtestSingboxController.StripForSpeedtest(config);

        Assert.AreEqual(0, config.Inbounds.Count, "入站全清（不占全局 Socks5 端口）");
        Assert.AreEqual(3, config.Outbounds!.Count, "urltest 出站被摘除");
        Assert.IsFalse(config.Outbounds.Any(o => o is SingboxUrltestOutbound), "无 urltest 残留");
        CollectionAssert.AreEqual(new[] { "node-0", "node-1" }, selector.Outbounds.ToList(), "selector 引用同步摘除 auto");
        Assert.AreEqual("node-0", selector.Default, "Default 本就不指 auto，不受影响");
    }

    [TestMethod]
    public void StripForSpeedtest_FixesAutoDefaultSelector()
    {
        var selector = new SingboxSelectorOutbound { Tag = "proxy", Default = "auto" };
        selector.Outbounds.AddRange(new[] { "node-0", "node-1", "auto" });
        var config = new SingboxConfig();
        config.Outbounds = new List<object>
        {
            selector,
            new SingboxUrltestOutbound { Tag = "auto" },
            new { type = "trojan", tag = "node-0" }
        };

        SpeedtestSingboxController.StripForSpeedtest(config);

        Assert.AreEqual("node-0", selector.Default, "Default 指向已摘除的 auto 会炸 sing-box 校验，改指首个节点");
    }

    #endregion
}
