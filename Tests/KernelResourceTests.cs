#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Controllers;
using Netch.Interfaces;
using Netch.Models;
using Netch.Services;
using NetchGlobal = Netch.Global;

namespace Tests;

/// <summary>
///     内核资源单测：禁用闭环（外部资源页禁用的内核任何路由路径都不再实例化）、
///     存量内核档位迁移（V2raySN/SingBox → Auto）、报废协议路由（SSR/旧 XTLS 明示不支持）。
///     网络层（真实下载）由 coreupdater_test/CoreUpdaterHarness 沙箱实测覆盖，此处不重复。
///     真控制器类型经 Guard 构造检查 bin 内核文件存在：EnsureFakeKernels 补假文件（MZ 头）。
/// </summary>
[TestClass]
public class KernelResourceTests
{
    /// <summary>补测试用假内核文件（Guard 只做 File.Exists 检查），返回清理委托</summary>
    private static Action EnsureFakeKernels(params string[] exeNames)
    {
        var created = new List<string>();
        foreach (var name in exeNames)
        {
            var path = Path.Combine(NetchGlobal.NetchDir, "bin", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, "MZ"u8.ToArray());
                created.Add(path);
            }
        }

        return () =>
        {
            foreach (var path in created)
                if (File.Exists(path))
                    File.Delete(path);
        };
    }

    [TestMethod]
    public void ResourceKeys_AreWired()
    {
        Assert.AreEqual("kernel.singbox", CoreUpdater.SingBox.ResourceKey);
        Assert.AreEqual("kernel.xray", CoreUpdater.Xray.ResourceKey);
        Assert.IsNotNull(ExternalResourceService.Find("kernel.singbox"));
        Assert.IsNotNull(ExternalResourceService.Find("kernel.xray"));
        // v2ray-sn 槽位已退役（2026-10-31）：注册行必须清零，防复活
        Assert.IsNull(ExternalResourceService.Find("kernel.v2ray-sn"));
        // redirector/stun 随主程序发行只读行已退役（咩咩 2026-11-03）：同槽位防复活口径
        Assert.IsNull(ExternalResourceService.Find("kernel.redirector"));
        Assert.IsNull(ExternalResourceService.Find("stun.list"));
    }

    [TestMethod]
    public void IsRunningChannel_MapsByControllerType()
    {
        var cleanup = EnsureFakeKernels("sing-box.exe", "xray.exe");
        try
        {
            SetServerController(new Netch.Servers.SingboxController());
            Assert.IsTrue(CoreUpdater.IsRunningChannel(CoreUpdater.SingBox));
            Assert.IsFalse(CoreUpdater.IsRunningChannel(CoreUpdater.Xray));

            SetServerController(new Netch.Servers.XrayController());
            Assert.IsTrue(CoreUpdater.IsRunningChannel(CoreUpdater.Xray));
            Assert.IsFalse(CoreUpdater.IsRunningChannel(CoreUpdater.SingBox));

            SetServerController(null);
            Assert.IsFalse(CoreUpdater.IsRunningChannel(CoreUpdater.SingBox), "未运行时不应触发停机");
        }
        finally
        {
            SetServerController(null);
            cleanup();
        }
    }

    [TestMethod]
    public void ShouldRestartAfterUpdate_FollowsKernelRestartSetting()
    {
        var original = NetchGlobal.Settings.Network.KernelAutoRestart;
        try
        {
            NetchGlobal.Settings.Network.KernelAutoRestart = true;
            Assert.IsFalse(CoreUpdater.ShouldRestartAfterUpdate(wasRunning: false), "没在跑就不存在拉起");
            Assert.IsTrue(CoreUpdater.ShouldRestartAfterUpdate(wasRunning: true));

            NetchGlobal.Settings.Network.KernelAutoRestart = false;
            Assert.IsFalse(CoreUpdater.ShouldRestartAfterUpdate(wasRunning: true), "关闭自动重启时不拉起");
        }
        finally
        {
            NetchGlobal.Settings.Network.KernelAutoRestart = original;
        }
    }

    [TestMethod]
    public void DisabledKernel_BlocksControllerCreation()
    {
        var singbox = ExternalResourceService.Find("kernel.singbox")!;
        var xray = ExternalResourceService.Find("kernel.xray")!;
        var cleanup = EnsureFakeKernels("xray.exe");

        try
        {
            // sing-box 禁用：SS 节点（默认路由 sing-box）启动被拦（全自动路由，禁用即拦）
            ExternalResourceService.MutateStateAsync(singbox, s => s.Disabled = true).GetAwaiter().GetResult();
            var ex = Assert.ThrowsException<MessageException>(
                () => MainController.CreateServerController(new Netch.Servers.Socks5Server()));
            StringAssert.Contains(ex.Message, "sing-box");

            // sing-box 禁用不影响 Xray 兜底：kcp VMess 仍路由 XrayController
            var kcp = new Netch.Servers.VMessServer { TransferProtocol = "kcp" };
            Assert.IsInstanceOfType(MainController.CreateServerController(kcp), typeof(Netch.Servers.XrayController));

            ExternalResourceService.MutateStateAsync(singbox, s => s.Disabled = false).GetAwaiter().GetResult();

            // xhttp 节点在 Xray 禁用时被拦
            ExternalResourceService.MutateStateAsync(xray, s => s.Disabled = true).GetAwaiter().GetResult();
            var vless = new Netch.Servers.VLESSServer { TransferProtocol = "xhttp" };
            Assert.ThrowsException<MessageException>(() => MainController.CreateServerController(vless));

            // 恢复后同一节点正常路由到 Xray
            ExternalResourceService.MutateStateAsync(xray, s => s.Disabled = false).GetAwaiter().GetResult();
            Assert.IsInstanceOfType(MainController.CreateServerController(vless), typeof(Netch.Servers.XrayController));
        }
        finally
        {
            ExternalResourceService.MutateStateAsync(singbox, s => s.Disabled = false).GetAwaiter().GetResult();
            ExternalResourceService.MutateStateAsync(xray, s => s.Disabled = false).GetAwaiter().GetResult();
            cleanup();
        }
    }

    [TestMethod]
    public void KernelCore_MigratesToAuto()
    {
        // 内核选择全自动化（2026-10-31 咩咩拍板）：存量 V2raySN（槽位引擎从未存在）/
        // SingBox（手选已移除）经 CheckSetting（加载必经）统一迁移 Auto
        foreach (var legacy in new[] { KernelGlobal.V2raySN, KernelGlobal.SingBox })
        {
            var settings = new Netch.Models.Setting();
            settings.Kernel.Core = legacy;
            Netch.Utils.Configuration.CheckSetting(settings);
            Assert.AreEqual(KernelGlobal.Auto, settings.Kernel.Core, $"存量 {legacy} 应迁移 Auto");
        }
    }

    [TestMethod]
    public void RetiredProtocols_RoutePerRetirementPlan()
    {
        // 拍板 A：SSR / 旧 XTLS flow / 裸 QUIC 传输（随包 Xray 26.10.21 已删，双死）明确报「不再支持」；
        // 明文 Trojan / mKCP 自动兜底 Xray
        var cleanup = EnsureFakeKernels("xray.exe");
        try
        {
            Assert.ThrowsException<MessageException>(
                () => MainController.CreateServerController(new Netch.Servers.ShadowsocksRServer()));
            Assert.ThrowsException<MessageException>(
                () => MainController.CreateServerController(new Netch.Servers.VLESSServer { TLSSecureType = "xtls" }));
            Assert.ThrowsException<MessageException>(
                () => MainController.CreateServerController(new Netch.Servers.TrojanServer { TLSSecureType = "xtls" }));
            Assert.ThrowsException<MessageException>(
                () => MainController.CreateServerController(new Netch.Servers.VMessServer { TransferProtocol = "quic" }));
            Assert.IsInstanceOfType(
                MainController.CreateServerController(new Netch.Servers.TrojanServer { TLSSecureType = "none" }),
                typeof(Netch.Servers.XrayController));
        }
        finally
        {
            cleanup();
        }
    }

    [TestMethod]
    public async Task KernelCheckUpdate_RecordsCheckState()
    {
        // VM 状态写回通道：LastCheckAt 由 CheckUpdateAsync 落（不真连 GitHub：检查在无网环境抛错，
        // 但 MutateState 在成功路径才写——此处只验证资源查找链路 + 缺省态干净）
        var resource = ExternalResourceService.Find("kernel.singbox")!;
        var state = ExternalResourceService.GetState(resource);
        Assert.IsFalse(state.Disabled);

        await ExternalResourceService.MutateStateAsync(resource, s => s.LastCheckAt = new DateTime(2026, 10, 29, 12, 0, 0));
        Assert.AreEqual(new DateTime(2026, 10, 29, 12, 0, 0), ExternalResourceService.GetState(resource).LastCheckAt);
        ExternalResourceService.GetState(resource).LastCheckAt = null;
        NetchGlobal.Settings.ExternalResources.Items.RemoveAll(i => i.Key == resource.Key && i.LastCheckAt == null && !i.Disabled && i.AutoUpdate == null && i.LastUpdateAt == null && i.LastETag == null);
    }

    private static void SetServerController(IServerController? controller)
    {
        var prop = typeof(MainController).GetProperty(nameof(MainController.ServerController))!;
        prop.GetSetMethod(true)!.Invoke(null, new[] { controller });
    }
}
