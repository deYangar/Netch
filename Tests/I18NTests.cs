using System.Globalization;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Enums;
using Netch.Localization;

namespace Tests;

[TestClass]
public class I18NTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>两份 resx key 集合一致且值非空（05§B5）</summary>
    [TestMethod]
    public void ResxKeySetsAreConsistent()
    {
        var root = Path.Combine(RepoRoot(), "Netch", "Localization");
        var neutral = LoadKeys(Path.Combine(root, "Strings.resx"));
        var zh = LoadKeys(Path.Combine(root, "Strings.zh-CN.resx"));

        Assert.IsTrue(neutral.Count > 300, $"neutral keys = {neutral.Count}");

        var missingInZh = neutral.Keys.Except(zh.Keys).ToList();
        var extraInZh = zh.Keys.Except(neutral.Keys).ToList();
        Assert.AreEqual(0, missingInZh.Count, "zh-CN 缺 key: " + string.Join(", ", missingInZh.Take(5)));
        Assert.AreEqual(0, extraInZh.Count, "zh-CN 多 key: " + string.Join(", ", extraInZh.Take(5)));

        foreach (var (key, value) in zh)
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"zh 空 value: {key}");
        foreach (var (key, value) in neutral)
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"en 空 value: {key}");
    }

    /// <summary>代码里全部 I18N.T/TF 引用的 key 在 resx 有值（防替换缺串；05§B5）</summary>
    [TestMethod]
    public void AllCodeKeysExistInResx()
    {
        var neutral = LoadKeys(Path.Combine(RepoRoot(), "Netch", "Localization", "Strings.resx"));
        var srcRoot = Path.Combine(RepoRoot(), "Netch");
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(srcRoot, file).Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            var text = File.ReadAllText(file);
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(text, """I18N\.T(?:F)?\("([^"]+)"\)"""))
            {
                var g = ((System.Text.RegularExpressions.Match)m).Groups[1].Value;
                used.Add(g);
            }
        }

        Assert.IsTrue(used.Count > 60, $"used keys = {used.Count}");
        var missing = used.Where(k => !neutral.ContainsKey(k)).ToList();
        Assert.AreEqual(0, missing.Count, "代码引用但 resx 缺失: " + string.Join(", ", missing.Take(10)));
    }

    /// <summary>
    ///     全量点分小写串字面量必须可解析到 resx（B1 终验抓到 main.state.starting 缺失后立此网）：
    ///     直查命中、或按 DataForm 规则补 serverForm. 前缀命中、或列白名单（非 i18n 域名等）。
    ///     罩住 GetStatusKey/校验器错误 key 等不经过 I18N.T 字面量调用点的动态 key。
    /// </summary>
    [TestMethod]
    public void AllDottedLiteralsResolvableToResx()
    {
        var resx = LoadKeys(Path.Combine(RepoRoot(), "Netch", "Localization", "Strings.resx"));
        var srcRoot = Path.Combine(RepoRoot(), "Netch");
        var pattern = new System.Text.RegularExpressions.Regex("\"([a-z0-9]+(?:\\.[a-z0-9]+)+)\"");
        string[] ignoreExtensions =
        {
            ".json", ".txt", ".log", ".exe", ".dll", ".sys", ".png", ".ico", ".bat", ".ps1",
            ".yaml", ".yml", ".md", ".xml", ".cs", ".xaml", ".resx", ".bak", ".zip", ".7z",
            ".csproj", ".sln", ".config", ".ini"
        };
        // 非 i18n 的点分小写串（外部服务地址等），新增时在此登记
        var notI18n = new HashSet<string> { "stun.syncthing.net" };

        var unresolved = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(srcRoot, file).Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (ignoreExtensions.Any(key.EndsWith) || notI18n.Contains(key))
                    continue;
                if (key.All(c => c == '.' || char.IsDigit(c)))
                    continue;
                    continue;
                if (resx.ContainsKey(key) || resx.ContainsKey($"serverForm.{key}"))
                    continue;
                unresolved.TryAdd(key, file);
            }
        }

        Assert.AreEqual(0, unresolved.Count,
            "点分 key 无法解析到 resx（直查与 serverForm. 前缀均未命中）: "
            + string.Join("; ", unresolved.Select(kv => $"{kv.Key} ({Path.GetFileName(kv.Value)})")));
    }

    /// <summary>XAML {I18N key} 引用全部存在于 resx（38 词条事故防回归，迁移自 harness 的常驻等价断言）</summary>
    [TestMethod]
    public void AllXamlI18nReferencesExistInResx()
    {
        var resx = LoadKeys(Path.Combine(RepoRoot(), "Netch", "Localization", "Strings.resx"));
        var srcRoot = Path.Combine(RepoRoot(), "Netch");
        var pattern = new System.Text.RegularExpressions.Regex("\\{I18N\\s+([A-Za-z0-9_.]+)");
        var missing = new Dictionary<string, string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(srcRoot, file).Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (!resx.ContainsKey(key))
                    missing.TryAdd(key, file);
            }
        }

        Assert.IsTrue(missing.Count == 0,
            "XAML 引用但 resx 缺失: " + string.Join("; ", missing.Select(kv => $"{kv.Key} ({Path.GetFileName(kv.Value)})")));
    }

    /// <summary>状态枚举全量 → GetStatusKey 的 key 都在 resx（枚举驱动，动态 key 源强制覆盖）</summary>
    [TestMethod]
    public void StateStatusKeysCoverAllEnumValues()
    {
        var resx = LoadKeys(Path.Combine(RepoRoot(), "Netch", "Localization", "Strings.resx"));
        foreach (State state in Enum.GetValues<State>())
        {
            var key = StateExtension.GetStatusKey(state);
            Assert.IsTrue(resx.ContainsKey(key), $"State.{state} 的状态文案 key 缺失: {key}");
        }
    }

    /// <summary>T/TF 取值与语言切换（zh/en 各取一次）</summary>
    [TestMethod]
    public void TranslateReturnsLocalizedValues()
    {
        I18N.SetLanguage("en-US");
        Assert.AreEqual("Start", I18N.T("main.start"));
        Assert.AreEqual("Waiting for command", I18N.T("main.state.waiting"));
        Assert.AreEqual("Starting", I18N.T("main.state.starting"));
        Assert.AreEqual("Stopping", I18N.T("main.state.stopping"));

        I18N.SetLanguage("zh-CN");
        Assert.AreEqual("启动", I18N.T("main.start"));
        Assert.AreEqual("等待命令中", I18N.T("main.state.waiting"));
        Assert.AreEqual("正在启动中", I18N.T("main.state.starting"));
        Assert.AreEqual("正在停止中", I18N.T("main.state.stopping"));
        Assert.AreEqual("已从剪贴板导入 3 个服务器", I18N.TF("home.importDone", 3));

        I18N.SetLanguage("System");
    }

    /// <summary>缺失 key 抛异常（开发期暴露，05§B1）</summary>
    [TestMethod]
    public void MissingKeyThrows()
    {
        Assert.ThrowsException<InvalidOperationException>(() => I18N.T("no.such.key.exists"));
    }

    private static Dictionary<string, string> LoadKeys(string path)
    {
        var result = new Dictionary<string, string>();
        var doc = new System.Xml.XmlDocument();
        doc.Load(path);
        foreach (System.Xml.XmlNode data in doc.SelectNodes("/root/data")!)
            result[data.Attributes!["name"].Value] = data.SelectSingleNode("value")!.InnerText;
        return result;
    }
}
