using System;
using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Services;

namespace Tests;

/// <summary>
///     Updater.ApplyUpdate 进程内覆盖测试（2026-11-20 发版形态改 zip 补齐：此前零覆盖）。
///     验证三组语义：① zip 顶层 Netch/ 契约下程序文件整目录覆盖更新；② KeepDirectories
///     用户区（data / mode\Custom / logging / bin\rule-set 缓存）保命；③ 旧程序文件标 .old
///     后由 CleanOld 启动清扫。真实下载链路由 release.yml 抽查 + VM 实测覆盖。
/// </summary>
[TestClass]
public class UpdaterApplyTests
{
    private string _installDir = null!;
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "netch-updater-test-" + Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");

        // 模拟旧安装：程序文件 + 组件 + 用户区
        Write("Netch.exe", "OLD-EXE");
        Write("bin\\Redirector.bin", "OLD-DLL");
        Write("bin\\sing-box.exe", "OLD-KERNEL"); // 更新包不带的组件：应走 .old → CleanOld 清扫
        Write("bin\\rule-set\\user-cache.srs", "USER-CACHE"); // 用户规则集缓存：保命
        Write("data\\servers.json", "SERVERS");
        Write("mode\\Custom\\my.json", "CUSTOM-MODE");
        Write("logging\\app.log", "LOG");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_installDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_installDir, relative));

    /// <summary>构造更新 zip：顶层 Netch/ 目录（Updater 硬契约），带新主程序/组件/内置规则集，不带内核</summary>
    private string BuildUpdateZip()
    {
        var zipPath = Path.Combine(_root, "update.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (entry, content) in new[]
                 {
                     ("Netch/Netch.exe", "NEW-EXE"),
                     ("Netch/bin/Redirector.bin", "NEW-DLL"),
                     ("Netch/bin/rule-set/builtin.srs", "NEW-SRS")
                 })
        {
            var e = archive.CreateEntry(entry);
            using var ws = new StreamWriter(e.Open());
            ws.Write(content);
        }

        return zipPath;
    }

    [TestMethod]
    public void ZipUpdate_CoversProgram_KeepsUserZone_CleansOld()
    {
        var updater = new Updater(BuildUpdateZip(), _installDir);
        updater.ApplyUpdate();

        // ① 程序文件被更新包内容覆盖
        Assert.AreEqual("NEW-EXE", Read("Netch.exe"));
        Assert.AreEqual("NEW-DLL", Read("bin\\Redirector.bin"));
        Assert.AreEqual("NEW-SRS", Read("bin\\rule-set\\builtin.srs"));

        // ② 用户区保命（含 bin\rule-set 用户缓存；内置同名快照仍可被包内容更新——本例无同名）
        Assert.AreEqual("SERVERS", Read("data\\servers.json"));
        Assert.AreEqual("CUSTOM-MODE", Read("mode\\Custom\\my.json"));
        Assert.AreEqual("LOG", Read("logging\\app.log"));
        Assert.AreEqual("USER-CACHE", Read("bin\\rule-set\\user-cache.srs"));

        // ③ 更新包未携带的旧程序文件被标 .old，CleanOld 启动清扫后消失
        Assert.IsTrue(File.Exists(Path.Combine(_installDir, "bin", "sing-box.exe.old")), "旧组件应被标记 .old 等待清扫");
        Updater.CleanOld(_installDir);
        Assert.IsFalse(File.Exists(Path.Combine(_installDir, "bin", "sing-box.exe.old")), "CleanOld 应清扫 .old 残件");
        Assert.IsFalse(File.Exists(Path.Combine(_installDir, "bin", "sing-box.exe")), "包未携带的旧组件不应复活");
    }

    [TestMethod]
    public void ZipUpdate_MissingTopLevelNetchDir_Rejected()
    {
        var zipPath = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("wrong-root/Netch.exe");
            using var ws = new StreamWriter(e.Open());
            ws.Write("X");
        }

        var updater = new Updater(zipPath, _installDir);
        Assert.ThrowsException<MessageException>(() => updater.ApplyUpdate());
    }
}
