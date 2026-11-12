#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Services;
using NetchGlobal = Netch.Global;
using static Netch.Services.ExternalResourceService;

namespace Tests;

/// <summary>
///     外部资源管线单测（R1 验收判据）：
///     坏包（截断/错魔数/404 体）全部拒绝且旧文件逐字节不变；.trash 隔离可恢复；状态缺省不落盘。
/// </summary>
[TestClass]
public class ExternalResourceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static ExternalResource Res(ExternalResourceKind kind, string key = "test.res", long minBytes = 4,
        string? url = null, string? fallbackUrl = null)
    {
        return new ExternalResource
        {
            Key = key,
            DisplayNameKey = "external.title",
            Kind = kind,
            Provider = url == null ? ExternalResourceProviderKind.None : ExternalResourceProviderKind.HttpFile,
            LocalPath = $@"bin\rule-set\{key}.bin",
            Url = url,
            FallbackUrl = fallbackUrl,
            Caps = ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanDelete,
            MinBytes = minBytes
        };
    }

    #region 校验表（魔数/大小/可解析）

    [TestMethod]
    public void Validate_KernelMagic()
    {
        var res = Res(ExternalResourceKind.Kernel, minBytes: 4);
        var path = Path.GetTempFileName();

        // 合法：MZ 头 + 足量体
        File.WriteAllBytes(path, "MZ"u8.ToArray().Concat(new byte[10]).ToArray());
        ValidateDownloaded(res, path);

        // 错魔数（HTML 错误体）
        File.WriteAllBytes(path, "<html>404 not found</html>"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));

        // 过小（1 字节 < 阈值 4）
        File.WriteAllBytes(path, "M"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));
        File.Delete(path);
    }

    [TestMethod]
    public void Validate_SrsMagic()
    {
        var res = Res(ExternalResourceKind.RuleSet, minBytes: 4);
        var path = Path.GetTempFileName();

        // 合法：SRS + 版本字节 1/2 + 任意体
        File.WriteAllBytes(path, new byte[] { 0x53, 0x52, 0x53, 0x01, 0x78, 0xDA });
        ValidateDownloaded(res, path);
        File.WriteAllBytes(path, new byte[] { 0x53, 0x52, 0x53, 0x02, 0x28, 0xB5 });
        ValidateDownloaded(res, path);

        // 错魔数（HTML 错误体）
        File.WriteAllBytes(path, "<html>502 bad gateway"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));
        // 截断体（不足 4 字节）
        File.WriteAllBytes(path, "SRS"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));
        // 非法版本字节
        File.WriteAllBytes(path, new byte[] { 0x53, 0x52, 0x53, 0x09 });
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));
        File.Delete(path);
    }

    [TestMethod]
    public void Validate_SrsSnapshotFromRepo_IsAccepted()
    {
        // 仓库内置快照（随包分发物）必须过校验——防校验器与真实资产格式漂移
        var res = Res(ExternalResourceKind.RuleSet, minBytes: 1024);
        foreach (var name in new[] { "geosite-cn.srs", "geoip-cn.srs" })
        {
            var path = Path.Combine(RepoRoot(), "Other", "rule-sets", name);
            ValidateDownloaded(res, path);
        }
    }

    [TestMethod]
    public void Validate_MmdbRejectsGarbage()
    {
        var res = Res(ExternalResourceKind.DataFile, minBytes: 4);

        // Reader 会 memory-map 文件：每个坏例用独立临时文件，避免句柄释放时序干扰覆写
        var path1 = Path.GetTempFileName();
        File.WriteAllBytes(path1, "not a database at all"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path1));
        File.Delete(path1);

        var path2 = Path.GetTempFileName();
        File.WriteAllBytes(path2, "<html>maintenance page</html>"u8.ToArray());
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path2));
        File.Delete(path2);
    }

    [TestMethod]
    public void Validate_SizeThreshold()
    {
        var res = Res(ExternalResourceKind.RuleSet, minBytes: 1024);
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 0x53, 0x52, 0x53, 0x01 }); // 格式合法但 < 1KB 阈值
        Assert.ThrowsException<MessageException>(() => ValidateDownloaded(res, path));
        File.Delete(path);
    }

    #endregion

    #region 管线端到端（坏包拒绝保旧文件 / 备胎切换 / 404 体）

    /// <summary>TcpListener 迷你 HTTP 服务：零权限依赖，按脚本回响应</summary>
    private sealed class FakeHttpServer : IDisposable
    {
        private readonly TcpListener _listener;

        public int Port { get; }

        public Func<byte[]> Body { get; set; } = () => "SRS1"u8.ToArray();

        public Func<int> StatusCode { get; set; } = () => 200;

        public FakeHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url => $"http://127.0.0.1:{Port}/file";

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    using (client)
                    await using (var stream = client.GetStream())
                    {
                        var buffer = new byte[4096];
                        await stream.ReadAsync(buffer);

                        var body = Body();
                        var status = StatusCode();
                        var reason = status == 200 ? "OK" : "Not Found";
                        var head =
                            $"HTTP/1.1 {status} {reason}\r\n" +
                            "Content-Type: application/octet-stream\r\n" +
                            $"Content-Length: {body.Length}\r\n" +
                            "ETag: \"test-etag-1\"\r\n" +
                            "Connection: close\r\n\r\n";
                        var headBytes = Encoding.ASCII.GetBytes(head);
                        await stream.WriteAsync(headBytes);
                        if (status == 200)
                            await stream.WriteAsync(body);
                        await stream.FlushAsync();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    /// <summary>把测试资源落进 NetchDir 相对路径并注册旧内容（管线替换目标）</summary>
    private static string SeedTarget(ExternalResource res, byte[] content)
    {
        var target = GetFullPath(res);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
        return target;
    }

    [TestMethod]
    public async Task Update_BadBody_RejectedOldFileUntouched()
    {
        using var server = new FakeHttpServer { StatusCode = () => 200, Body = () => "<html>blocked</html>"u8.ToArray() };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-badbody", minBytes: 4, url: server.Url);
        var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0xAA, 0xBB };
        var target = SeedTarget(res, old);

        await Assert.ThrowsExceptionAsync<MessageException>(() => UpdateHttpFileAsync(res));

        // 旧文件逐字节不变
        CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
        // 状态未被污染为「已更新」
        Assert.IsNull(GetState(res).LastUpdateAt);
    }

    [TestMethod]
    public async Task Update_Http404_RejectedOldFileUntouched()
    {
        using var server = new FakeHttpServer { StatusCode = () => 404, Body = () => "not found"u8.ToArray() };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-404", minBytes: 4, url: server.Url);
        var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0xCC, 0xDD };
        var target = SeedTarget(res, old);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => UpdateHttpFileAsync(res));
        CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task Update_TruncatedBody_Rejected()
    {
        // 截断体：服务器声明 Content-Length 大于实发（客户端读取截断）→ 由大小/魔数任一拒绝
        using var server = new FakeHttpServer { Body = () => new byte[] { 0x53, 0x52, 0x53 } };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-trunc", minBytes: 4, url: server.Url);
        var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0xEE, 0xFF };
        var target = SeedTarget(res, old);

        await Assert.ThrowsExceptionAsync<MessageException>(() => UpdateHttpFileAsync(res));
        CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task Update_GoodBody_ReplacesAtomicallyAndRecordsState()
    {
        using var server = new FakeHttpServer { Body = () => new byte[] { 0x53, 0x52, 0x53, 0x01, 0x11, 0x22 } };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-good", minBytes: 4, url: server.Url);
        var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0x99 };
        var target = SeedTarget(res, old);

        var (outcome, usedUrl, viaMirror) = await UpdateHttpFileAsync(res);

        Assert.AreEqual(ResourceUpdateOutcome.Updated, outcome);
        Assert.AreEqual(server.Url, usedUrl);
        Assert.IsFalse(viaMirror);
        CollectionAssert.AreEqual(new byte[] { 0x53, 0x52, 0x53, 0x01, 0x11, 0x22 }, File.ReadAllBytes(target));
        Assert.IsNotNull(GetState(res).LastUpdateAt);
        Assert.IsNotNull(GetState(res).LastETag);
    }

    [TestMethod]
    public async Task Update_PrimaryBroken_FallsBackToMirror()
    {
        using var bad = new FakeHttpServer { StatusCode = () => 404 };
        using var mirror = new FakeHttpServer { Body = () => new byte[] { 0x53, 0x52, 0x53, 0x01, 0x77 } };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-mirror", minBytes: 4,
            url: bad.Url, fallbackUrl: mirror.Url);
        var target = SeedTarget(res, new byte[] { 0x53, 0x52, 0x53, 0x01, 0x99 });

        var (outcome, usedUrl, viaMirror) = await UpdateHttpFileAsync(res);

        Assert.AreEqual(ResourceUpdateOutcome.Updated, outcome);
        Assert.AreEqual(mirror.Url, usedUrl);
        Assert.IsTrue(viaMirror);
        CollectionAssert.AreEqual(new byte[] { 0x53, 0x52, 0x53, 0x01, 0x77 }, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task Update_MirrorDisabled_PrimaryFailurePropagates()
    {
        using var bad = new FakeHttpServer { StatusCode = () => 404 };
        using var mirror = new FakeHttpServer { Body = () => new byte[] { 0x53, 0x52, 0x53, 0x01 } };
        var res = Res(ExternalResourceKind.RuleSet, key: "test-nomirror", minBytes: 4,
            url: bad.Url, fallbackUrl: mirror.Url);
        NetchGlobal.Settings.ExternalResources.RuleSetFallbackMirror = false;
        try
        {
            var old = new byte[] { 0x53, 0x52, 0x53, 0x01, 0x99 };
            var target = SeedTarget(res, old);

            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => UpdateHttpFileAsync(res));
            CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
        }
        finally
        {
            NetchGlobal.Settings.ExternalResources.RuleSetFallbackMirror = true;
        }
    }

    #endregion

    #region .trash 隔离删除

    [TestMethod]
    public void MoveToTrash_IsolatesAndRecoverable()
    {
        var res = Res(ExternalResourceKind.RuleSet, key: "test-trash", minBytes: 4);
        var content = new byte[] { 0x53, 0x52, 0x53, 0x01, 0xAB };
        var target = SeedTarget(res, content);

        var trashName = MoveToTrash(res);

        // 原位置已移除；隔离区文件内容一致（可恢复）
        Assert.IsFalse(File.Exists(target));
        var trashFile = Path.Combine(NetchGlobal.NetchDir, TrashDirName, trashName);
        Assert.IsTrue(File.Exists(trashFile));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(trashFile));

        // 恢复：从隔离区取回原位置
        File.Copy(trashFile, target);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));

        // 隔次启动清理：清空隔离区
        CleanTrashOnStartup();
        Assert.IsFalse(Directory.Exists(Path.Combine(NetchGlobal.NetchDir, TrashDirName)));

        // 测试残留清理
        if (File.Exists(target))
            File.Delete(target);
    }

    [TestMethod]
    public void MoveToTrash_SameNameCollisionGetsSuffix()
    {
        var res = Res(ExternalResourceKind.RuleSet, key: "test-collide", minBytes: 4);
        var first = SeedTarget(res, new byte[] { 0x53, 0x52, 0x53, 0x01 });
        var n1 = MoveToTrash(res);
        var second = SeedTarget(res, new byte[] { 0x53, 0x52, 0x53, 0x02 });
        var n2 = MoveToTrash(res);

        Assert.AreNotEqual(n1, n2);

        CleanTrashOnStartup();
        if (File.Exists(first))
            File.Delete(first);
        if (File.Exists(second))
            File.Delete(second);
    }

    [TestMethod]
    public void MoveToTrash_NotInstalled_Throws()
    {
        var res = Res(ExternalResourceKind.RuleSet, key: "test-absent", minBytes: 4);
        Assert.ThrowsException<MessageException>(() => MoveToTrash(res));
    }

    #endregion

    #region 状态持久化（缺省不落盘，首次改动才写入）

    [TestMethod]
    public async Task State_DefaultNotPersisted_UntilMutated()
    {
        var res = Res(ExternalResourceKind.RuleSet, key: "test-state", minBytes: 4);
        var store = NetchGlobal.Settings.ExternalResources;

        var before = store.Items.Count(i => i.Key == res.Key);
        Assert.AreEqual(0, before, "未改动的资源不应有落盘条目");

        // 缺省态读取：禁用=false、自动更新=注册表默认
        Assert.IsFalse(IsDisabled(res));
        Assert.IsFalse(IsAutoUpdateOn(res), "测试资源未挂 CanAutoUpdate 能力");

        await MutateStateAsync(res, s => s.Disabled = true);
        Assert.AreEqual(1, store.Items.Count(i => i.Key == res.Key));
        Assert.IsTrue(IsDisabled(res));
    }

    [TestMethod]
    public void AutoUpdate_UserOverrideWinsOverDefault()
    {
        var res = new ExternalResource
        {
            Key = "test-autoupdate",
            DisplayNameKey = "external.title",
            Kind = ExternalResourceKind.RuleSet,
            Provider = ExternalResourceProviderKind.None,
            LocalPath = @"bin\rule-set\test-autoupdate.bin",
            Caps = ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = true,
            MinBytes = 4
        };

        Assert.IsTrue(IsAutoUpdateOn(res), "默认开");
        NetchGlobal.Settings.ExternalResources.Items.Add(new ExternalResourceState { Key = res.Key, AutoUpdate = false });
        Assert.IsFalse(IsAutoUpdateOn(res), "用户显式关优先于默认");
        NetchGlobal.Settings.ExternalResources.Items.RemoveAll(i => i.Key == res.Key);
    }

    #endregion
}
