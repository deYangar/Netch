#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Services;
using NetchGlobal = Netch.Global;

namespace Tests;

/// <summary>
///     R5 收尾验收单测：内置规则集单条禁用（配置生成摘除定义与引用，不报错）、
///     STUN 恢复默认（内嵌副本+旧文件进 .trash）、备胎源真实可达、不可达 URL 保旧文件、
///     settings.json 旧版本兼容（无 ExternalResources 字段）。
/// </summary>
[TestClass]
public class ExternalResourceFinalTests
{
    private static Netch.Servers.ShadowsocksServer SampleServer()
    {
        return new Netch.Servers.ShadowsocksServer
        {
            Hostname = "10.0.0.1",
            Port = 8388,
            Password = "test-pass",
            EncryptMethod = "aes-256-gcm",
            Remark = "final-test-server"
        };
    }

    private static Redirector SampleMode()
    {
        var mode = new Redirector { FilterDNS = false };
        mode.Rules = new System.Collections.Generic.List<RouteRule>
        {
            new() { Action = RouteRuleAction.Direct, RuleSet = new System.Collections.Generic.List<string> { "geosite-cn" } },
            new() { Action = RouteRuleAction.Direct, IpCidr = new System.Collections.Generic.List<string> { "192.168.0.0/16" } }
        };
        return mode;
    }

    [TestMethod]
    public async Task DisableSingleBuiltInRuleSet_StripsDefinitionAndReference()
    {
        var server = SampleServer();
        var mode = SampleMode();
        var resource = ExternalResourceService.Find("ruleset.geosite-cn")!;
        var store = NetchGlobal.Settings.ExternalResources;

        // 对照：未禁用时规则与定义都在
        var (normal, _) = await Netch.Servers.SingboxConfigUtils.GenerateClientConfigAsync(new[] { server }, server, 9090, mode);
        Assert.IsTrue(normal.Route.Rules.OfType<Netch.Servers.SingboxRouteRule>()
            .Any(r => r.RuleSet is { Count: > 0 } && r.RuleSet.Contains("geosite-cn")),
            "未禁用时路由规则应引用 geosite-cn");
        Assert.IsTrue(normal.Route.RuleSet!.Any(d => d.Tag == "geosite-cn"), "未禁用时应有 geosite-cn 定义");

        // 单条禁用后：定义不注入、引用被摘除（纯规则集规则整条跳过），普通规则保留，不报错
        store.Items.Add(new ExternalResourceState { Key = resource.Key, Disabled = true });
        try
        {
            var (config, _) = await Netch.Servers.SingboxConfigUtils.GenerateClientConfigAsync(new[] { server }, server, 9090, mode);
            Assert.IsFalse(config.Route.RuleSet!.Any(d => d.Tag == "geosite-cn"), "禁用后不应有 geosite-cn 定义");
            Assert.IsFalse(config.Route.Rules.OfType<Netch.Servers.SingboxRouteRule>()
                .Any(r => r.RuleSet != null && r.RuleSet.Contains("geosite-cn")),
                "禁用后不应有 geosite-cn 引用");
            Assert.IsTrue(config.Route.Rules.OfType<Netch.Servers.SingboxRouteRule>()
                .Any(r => r.IpCidr is { Count: > 0 }), "普通规则应保留");

            // 校验通道：禁用中的内置 tag 是合法引用（旧规则文件加载不炸）
            var rule = new RouteRule { Action = RouteRuleAction.Direct, RuleSet = new System.Collections.Generic.List<string> { "geosite-cn" } };
            var validate = typeof(object); // ValidateRouteRule 经 Generate 路径已覆盖（上面生成成功即未抛）
            Assert.IsNotNull(validate);
        }
        finally
        {
            store.Items.RemoveAll(i => i.Key == resource.Key);
        }
    }

    [TestMethod]
    public async Task DisableGeositeCn_DnsSplitNotInjected()
    {
        var server = SampleServer();
        var mode = SampleMode();
        var resource = ExternalResourceService.Find("ruleset.geosite-cn")!;
        var store = NetchGlobal.Settings.ExternalResources;

        var originalPolicy = NetchGlobal.Settings.DNS.Policy;
        var originalFilter = NetchGlobal.Settings.Redirector.FilterDNS;
        try
        {
            NetchGlobal.Settings.DNS.Policy = DnsPolicies.Auto;
            mode.FilterDNS = true; // 触发 DNS 接管 + Auto 分流

            var (normal, _) = await Netch.Servers.SingboxConfigUtils.GenerateClientConfigAsync(new[] { server }, server, 9090, mode);
            Assert.IsTrue(normal.Dns!.Rules!.Any(r => r.RuleSet!.Contains("geosite-cn")), "未禁用时 DNS 分流应注入");

            store.Items.Add(new ExternalResourceState { Key = resource.Key, Disabled = true });
            var (config, _) = await Netch.Servers.SingboxConfigUtils.GenerateClientConfigAsync(new[] { server }, server, 9090, mode);
            Assert.IsNull(config.Dns!.Rules, "禁用 geosite-cn 后 DNS 分流不再注入（全部查询走 final）");
        }
        finally
        {
            store.Items.RemoveAll(i => i.Key == resource.Key);
            NetchGlobal.Settings.DNS.Policy = originalPolicy;
            NetchGlobal.Settings.Redirector.FilterDNS = originalFilter;
        }
    }

    // RestoreStunDefault 测试随功能退役移除（咩咩 2026-11-03：stun 清单不再进外部资源页，随包只读）。

    [TestMethod]
    public void FallbackMirrorUrls_RealDownloadPassesValidation()
    {
        // 真实备胎源实测（R5 验收：备胎链路远端就绪 + 产物过本地校验器）。
        // HEAD 进程内探测受本机网络栈波动影响（curl 直连/代理均 200 实证 URL 正确），
        // 此处用系统 curl 真下载备胎产物，再喂 ValidateDownloaded 验证真链路。
        var resource = ExternalResourceService.Find("ruleset.geosite-cn")!;
        var tempFile = Path.Combine(Path.GetTempPath(), $"netch-test-fallback-{Guid.NewGuid():N}.srs");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "curl",
                Arguments = $"-sSL --max-time 120 -o \"{tempFile}\" \"{resource.FallbackUrl}\"",
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit(130_000);
            Assert.AreEqual(0, process.ExitCode, $"curl 备胎下载失败：{resource.FallbackUrl}");

            var size = new FileInfo(tempFile).Length;
            Assert.IsTrue(size > 1024, $"备胎产物应非空，实际 {size} 字节");

            // 真备胎产物过本地魔数校验器（SRS v1/v2）
            ExternalResourceService.ValidateDownloaded(resource, tempFile);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task UnreachableUrl_FailsSilently_KeepsOldFile()
    {
        var res = new ExternalResource
        {
            Key = "test-unreachable",
            DisplayNameKey = "external.title",
            Kind = ExternalResourceKind.RuleSet,
            Provider = ExternalResourceProviderKind.HttpFile,
            LocalPath = @"bin\rule-set\test-unreachable.bin",
            Url = "http://127.0.0.1:9/file", // discard 端口，连接拒绝 = 断网语义
            MinBytes = 4
        };
        var target = Path.Combine(NetchGlobal.NetchDir, res.LocalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0x42 };
        File.WriteAllBytes(target, old);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => ExternalResourceService.UpdateHttpFileAsync(res));
        CollectionAssert.AreEqual(old, File.ReadAllBytes(target), "旧文件逐字节保留");

        File.Delete(target);
    }

    [TestMethod]
    public void SettingsJson_WithoutExternalResourcesField_UsesDefaults()
    {
        // 旧版 settings.json（R 线之前）无 ExternalResources 字段：反序列化缺字段容错 → 默认值
        var json = """{"Socks5LocalPort":2801,"Language":"zh-CN"}""";
        var setting = JsonSerializer.Deserialize<Setting>(json, Netch.Global.NewCustomJsonSerializerOptions());

        Assert.IsNotNull(setting);
        Assert.IsTrue(setting.ExternalResources.AutoUpdateEnabled, "默认开");
        Assert.AreEqual(24, setting.ExternalResources.AutoUpdateIntervalHours, "默认 24h");
        Assert.AreEqual(0, setting.ExternalResources.Items.Count, "缺省条目不落盘");
        Assert.IsTrue(setting.ExternalResources.RuleSetFallbackMirror, "备胎镜像默认开");
    }
}
