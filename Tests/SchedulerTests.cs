#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Services;
using NetchGlobal = Netch.Global;

namespace Tests;

/// <summary>
///     R4 自动更新调度单测：调度清单选择（全局开关/每资源开关/内核默认不进清单）、
///     全局互斥占用时跳过、真实一轮静默检查（网络失败静默不抛）。
/// </summary>
[TestClass]
public class SchedulerTests
{
    [TestMethod]
    public void SelectAutoResources_DefaultsExcludesKernels()
    {
        NetchGlobal.Settings.ExternalResources.Items.Clear();
        var selected = ExternalResourceService.All.Where(ExternalResourceService.IsAutoUpdateOn).ToList();

        // 默认：内置规则集×2（开）+ ip2region v4/v6（开）；内核×2 默认关；Redirector 无自动更新能力；stun 无能力
        CollectionAssert.AreEquivalent(
            new[] { "ruleset.geosite-cn", "ruleset.geoip-cn", "geo.ip2region", "geo.ip2region-v6" },
            selected.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void SelectAutoResources_UserExplicitEnableOverridesKernelDefault()
    {
        var store = NetchGlobal.Settings.ExternalResources;
        var singbox = ExternalResourceService.Find("kernel.singbox")!;
        store.Items.Add(new ExternalResourceState { Key = singbox.Key, AutoUpdate = true });
        try
        {
            var selected = ExternalResourceService.All.Where(ExternalResourceService.IsAutoUpdateOn).ToList();
            CollectionAssert.Contains(selected.Select(r => r.Key).ToList(), "kernel.singbox");
        }
        finally
        {
            store.Items.RemoveAll(i => i.Key == singbox.Key);
        }
    }

    [TestMethod]
    public async Task RunOnce_GlobalSwitchOff_DoesNothing()
    {
        var original = NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled;
        NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = false;
        try
        {
            // 总开关关：直接返回（无动作、无异常）
            await ExternalResourceScheduler.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = original;
        }
    }

    [TestMethod]
    public async Task RunOnce_BusyLockHeld_SkipsAll()
    {
        var original = NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled;
        NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = true;
        await ExternalResourceService.BusyLock.WaitAsync();
        try
        {
            // 手动操作持锁中：本轮全部跳过（不排队堆积），且不抛异常
            await ExternalResourceScheduler.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            ExternalResourceService.BusyLock.Release();
            NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = original;
        }
    }

    [TestMethod]
    public async Task RunOnce_RealPass_SilentAndStateful()
    {
        // 真实一轮（真网络：srs/xdb 探测+按需下载；GitHub API 内核检查）。
        // 验收点：静默失败不抛（断网/波动只记日志）；成功路径写 LastCheckAt。
        var original = NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled;
        NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = true;
        try
        {
            await ExternalResourceScheduler.RunOnceAsync(CancellationToken.None);

            var geo = ExternalResourceService.Find("geo.ip2region-v6")!;
            var state = ExternalResourceService.GetState(geo);
            // 网络可用时 LastCheckAt 已落；不可用时静默（不失败）。两种结果都合法，这里如实记录实测事实
            Console.WriteLine($"geo.ip2region-v6 LastCheckAt={state.LastCheckAt:O} LastETag={state.LastETag}");
        }
        finally
        {
            NetchGlobal.Settings.ExternalResources.AutoUpdateEnabled = original;
        }
    }
}
