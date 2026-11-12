using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Tests;

/// <summary>
///     BackupService 纯逻辑单测（2026-11-11 设置页备份/恢复）：
///     剥离字段、全量打包往返、manifest 校验、Settings 型合并保留四集合、zip 路径穿越拒绝、目录合并不删多余
/// </summary>
[TestClass]
[DoNotParallelize]
public class BackupServiceTests
{
    private string _tempRoot = "";

    [TestInitialize]
    public void Init()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "BackupServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static Setting MakeSetting()
    {
        return new Setting
        {
            Language = "zh-CN",
            HTTPLocalPort = 12345,
            RequestTimeout = 23456,
            Server = { new ShadowsocksServer { Hostname = "localhost", Port = 8388, EncryptMethod = "aes-256-gcm", Password = "test", Remark = "srv1" } },
            Subscription = { new Subscription { Enable = true, Link = "https://example.com/sub", Remark = "sub1" } },
            RuleSets = { new RuleSetConfig { Tag = "rs1", Url = "https://example.com/rs.srs" } },
            Profiles = { new Profile { Index = 0, ServerRemark = "srv1", ModeRemark = "模式1", ProfileName = "p1" } }
        };
    }

    private static BackupService.BackupManifest ReadManifestFromZip(string zipPath)
    {
        return BackupService.ReadManifest(zipPath);
    }

    private static T EntryJson<T>(string zipPath, string entryName, JsonSerializerOptions options)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, options)!;
    }

    [TestMethod]
    public void SettingsBackup_StripsFourCollections_KeepsOtherFields()
    {
        var zip = Path.Combine(_tempRoot, "settings.zip");
        var setting = MakeSetting();

        BackupService.WriteSettingsBackup(zip, setting, "1.0.0", "2026-11-11 12:00:00");

        var manifest = ReadManifestFromZip(zip);
        Assert.AreEqual("Settings", manifest.Type);
        Assert.AreEqual(1, manifest.Version);
        Assert.AreEqual("1.0.0", manifest.App);
        Assert.AreEqual("2026-11-11 12:00:00", manifest.CreatedAt);

        var restored = EntryJson<Setting>(zip, "settings.json", Configuration.JsonSerializerOptions);
        Assert.AreEqual(0, restored.Server.Count, "节点必须剥离");
        Assert.AreEqual(0, restored.Subscription.Count, "订阅必须剥离");
        Assert.AreEqual(0, restored.RuleSets.Count, "分流规则集必须剥离");
        Assert.AreEqual(0, restored.Profiles.Count, "快捷配置必须剥离（依赖节点，节点不备则它是死数据）");

        Assert.AreEqual("zh-CN", restored.Language);
        Assert.AreEqual((ushort)12345, restored.HTTPLocalPort);
        Assert.AreEqual(23456, restored.RequestTimeout);

        // 源对象不被剥离动作修改
        Assert.AreEqual(1, setting.Server.Count);
        Assert.AreEqual(1, setting.Subscription.Count);
    }

    [TestMethod]
    public void FullBackup_RoundTrips_SettingsBytes_And_CustomModes()
    {
        var zip = Path.Combine(_tempRoot, "full.zip");

        // 临时 data 目录 + settings.json
        var dataDir = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(dataDir);
        var settingsPath = Path.Combine(dataDir, "settings.json");
        var settingJson = JsonSerializer.Serialize(MakeSetting(), Configuration.JsonSerializerOptions);
        File.WriteAllText(settingsPath, settingJson);

        // 临时 mode\Custom（含子目录）
        var customDir = Path.Combine(_tempRoot, "mode", "Custom");
        Directory.CreateDirectory(Path.Combine(customDir, "sub"));
        File.WriteAllText(Path.Combine(customDir, "a.json"), "{}");
        File.WriteAllText(Path.Combine(customDir, "sub", "b.json"), "{ \"nested\": true }");
        // 内置模式位置（Custom 外）——不应被打包
        File.WriteAllText(Path.Combine(_tempRoot, "mode", "builtin.json"), "{}");

        BackupService.WriteFullBackup(zip, settingsPath, customDir, "1.0.0", "2026-11-11 12:00:00");

        var manifest = ReadManifestFromZip(zip);
        Assert.AreEqual("Full", manifest.Type);

        using (var archive = ZipFile.OpenRead(zip))
        {
            CollectionAssert.AreEquivalent(
                new[] { "manifest.json", "settings.json", "mode/a.json", "mode/sub/b.json" },
                archive.Entries.Select(e => e.FullName).ToList());

            // settings.json 原文件字节进 zip
            var entry = archive.GetEntry("settings.json")!;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            Assert.AreEqual(settingJson, reader.ReadToEnd());
        }

        // 全量 settings.json 反序列化走 Configuration 同一套 converter，多态 Server 可还原
        var restored = EntryJson<Setting>(zip, "settings.json", Configuration.JsonSerializerOptions);
        Assert.AreEqual(1, restored.Server.Count);
        Assert.AreEqual("srv1", restored.Server[0].Remark);
        Assert.AreEqual(1, restored.Subscription.Count);
        Assert.AreEqual(1, restored.RuleSets.Count);
        Assert.AreEqual(1, restored.Profiles.Count);
    }

    [TestMethod]
    public void FullBackup_WithoutCustomModeDirectory_StillValid()
    {
        var zip = Path.Combine(_tempRoot, "full-nomode.zip");
        var settingsPath = Path.Combine(_tempRoot, "settings.json");
        File.WriteAllText(settingsPath, "{}");

        var absentDir = Path.Combine(_tempRoot, "mode", "Custom"); // 不创建
        BackupService.WriteFullBackup(zip, settingsPath, absentDir, "1.0.0", "t");

        Assert.AreEqual("Full", ReadManifestFromZip(zip).Type);
    }

    [TestMethod]
    public void ReadManifest_Rejects_BadZip_MissingManifest_UnknownType_FutureVersion()
    {
        // 坏 zip（文本文件）
        var notZip = Path.Combine(_tempRoot, "fake.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.ThrowsException<InvalidDataException>(() => ReadManifestFromZip(notZip));

        // 空 zip（缺 manifest）
        var emptyZip = Path.Combine(_tempRoot, "empty.zip");
        using (ZipFile.Open(emptyZip, ZipArchiveMode.Create))
        {
        }
        Assert.ThrowsException<MessageException>(() => ReadManifestFromZip(emptyZip));

        // 类型未知 / 版本超前
        var evilZip = Path.Combine(_tempRoot, "evil.zip");
        using (var archive = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, new BackupService.BackupManifest { Type = "Evil", Version = 1 });
        }
        Assert.ThrowsException<MessageException>(() => ReadManifestFromZip(evilZip));

        var futureZip = Path.Combine(_tempRoot, "future.zip");
        using (var archive = ZipFile.Open(futureZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, new BackupService.BackupManifest { Type = "Full", Version = 99 });
        }
        Assert.ThrowsException<MessageException>(() => ReadManifestFromZip(futureZip));
    }

    [TestMethod]
    public void MergeSettingsRestore_KeepsCurrentCollections_OverwritesNothingElse()
    {
        var restored = new Setting { Language = "zh-CN", HTTPLocalPort = 1111 };
        var current = MakeSetting();
        current.Language = "en";

        var merged = BackupService.MergeSettingsRestore(restored, current);

        Assert.AreSame(current.Server, merged.Server, "节点保持当前态");
        Assert.AreSame(current.Subscription, merged.Subscription, "订阅保持当前态");
        Assert.AreSame(current.RuleSets, merged.RuleSets, "分流规则集保持当前态");
        Assert.AreSame(current.Profiles, merged.Profiles, "快捷配置保持当前态");
        Assert.AreEqual("zh-CN", merged.Language, "其余设置以备份为准");

        // Set 到现有实例：实例不变、字段被备份覆盖、四集合仍指向 current 的对象
        merged.Server = current.Server;
        current.Set(merged);
        Assert.AreSame(current, current);
        Assert.AreEqual("zh-CN", current.Language);
        Assert.AreEqual((ushort)1111, current.HTTPLocalPort);
        Assert.AreEqual(1, current.Server.Count);
    }

    [TestMethod]
    public void ExtractModeEntries_Rejects_PathTraversal()
    {
        var zip = Path.Combine(_tempRoot, "traversal.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mode/../../evil.txt", "pwned");
            WriteEntry(archive, "mode/ok.txt", "fine");
        }

        var tempExtract = Path.Combine(_tempRoot, "extract");
        Assert.ThrowsException<MessageException>(() => BackupService.ExtractModeEntries(zip, tempExtract));
        Assert.IsFalse(File.Exists(Path.Combine(_tempRoot, "evil.txt")), "穿越文件绝不能落地");

        // 根相对路径（\evil → C:\evil）同样拒绝
        var zip2 = Path.Combine(_tempRoot, "traversal2.zip");
        using (var archive = ZipFile.Open(zip2, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mode/\\Windows\\evil.txt", "pwned");
        }
        Assert.ThrowsException<MessageException>(() => BackupService.ExtractModeEntries(zip2, Path.Combine(_tempRoot, "extract2")));
    }

    [TestMethod]
    public void ExtractModeEntries_Restores_RelativeStructure()
    {
        var zip = Path.Combine(_tempRoot, "modes.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mode/Custom/a.json", "{}");
            WriteEntry(archive, "mode/Custom/sub/b.json", "{}");
            WriteEntry(archive, "mode/Custom/", ""); // 目录条目，跳过
        }

        var extracted = BackupService.ExtractModeEntries(zip, Path.Combine(_tempRoot, "ex"));
        Assert.IsTrue(File.Exists(Path.Combine(extracted, "Custom", "a.json")));
        Assert.IsTrue(File.Exists(Path.Combine(extracted, "Custom", "sub", "b.json")));
    }

    [TestMethod]
    public void CopyDirectoryOverwrite_OverwritesSameName_KeepsExtraFiles()
    {
        var src = Path.Combine(_tempRoot, "src");
        var dst = Path.Combine(_tempRoot, "dst");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(dst);

        File.WriteAllText(Path.Combine(src, "a.json"), "new");
        File.WriteAllText(Path.Combine(src, "sub", "b.json"), "nested");
        File.WriteAllText(Path.Combine(dst, "a.json"), "old");
        File.WriteAllText(Path.Combine(dst, "keep.json"), "must-survive");

        BackupService.CopyDirectoryOverwrite(src, dst);

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(dst, "a.json")), "同名覆盖");
        Assert.AreEqual("nested", File.ReadAllText(Path.Combine(dst, "sub", "b.json")), "新增补齐（含子目录）");
        Assert.AreEqual("must-survive", File.ReadAllText(Path.Combine(dst, "keep.json")), "恢复不删用户数据");
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
