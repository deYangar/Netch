#nullable enable
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Controllers;

namespace Tests;

/// <summary>
///     断网自动暂停/恢复的连通性判定单测：探活注入驱动状态机——
///     单轮探活失败不误杀、连续达阈值判死（NetworkLost）、探活成功恢复（NetworkRestored）。
///     前提：运行机有活动网络接口（无接口环境 Inconclusive）。
/// </summary>
[TestClass]
public class NetworkWatcherTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NetworkWatcher.ProbeOverride = null;
    }

    [TestMethod]
    public async Task ProbeFailures_BelowThreshold_DoNotRaiseLost()
    {
        if (!NetworkWatcher.IsNetworkAvailable())
            Assert.Inconclusive("运行机无活动网络接口");

        NetworkWatcher.ProbeOverride = () => Task.FromResult(false);
        using var watcher = new NetworkWatcher();
        var lost = 0;
        watcher.NetworkLost += _ => lost++;

        watcher.Start();
        await watcher.EvaluateForTestAsync();

        // 第一轮失败（阈值 2 未达）：保持可用，不误杀
        Assert.AreEqual(0, lost, "单轮探活失败不应判死");
    }

    [TestMethod]
    public async Task ProbeFailures_AtThreshold_RaiseLostThenRecover()
    {
        if (!NetworkWatcher.IsNetworkAvailable())
            Assert.Inconclusive("运行机无活动网络接口");

        NetworkWatcher.ProbeOverride = () => Task.FromResult(false);
        using var watcher = new NetworkWatcher();
        var lost = 0;
        var restored = 0;
        watcher.NetworkLost += _ => lost++;
        watcher.NetworkRestored += () => restored++;

        watcher.Start();
        await watcher.EvaluateForTestAsync();
        await watcher.EvaluateForTestAsync();
        Assert.AreEqual(1, lost, "连续两轮失败应判死一次");
        Assert.AreEqual(0, restored);

        // 持续断网：不重复发事件
        await watcher.EvaluateForTestAsync();
        Assert.AreEqual(1, lost);

        // 探活成功：恢复
        NetworkWatcher.ProbeOverride = () => Task.FromResult(true);
        await watcher.EvaluateForTestAsync();
        Assert.AreEqual(1, restored, "探活成功应触发恢复");

        // 恢复后再跑：状态稳定，不重复恢复
        await watcher.EvaluateForTestAsync();
        Assert.AreEqual(1, restored);
    }

    [TestMethod]
    public async Task IsConnectivityOk_FollowsProbeResult()
    {
        if (!NetworkWatcher.IsNetworkAvailable())
            Assert.Inconclusive("运行机无活动网络接口");

        NetworkWatcher.ProbeOverride = () => Task.FromResult(true);
        Assert.IsTrue(await NetworkWatcher.IsConnectivityOkAsync());

        NetworkWatcher.ProbeOverride = () => Task.FromResult(false);
        Assert.IsFalse(await NetworkWatcher.IsConnectivityOkAsync());
    }
}
