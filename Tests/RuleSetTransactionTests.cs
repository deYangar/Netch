using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Services;
using Netch.Utils;

namespace Tests;

[TestClass]
[DoNotParallelize]
public class RuleSetTransactionTests
{
    [TestMethod]
    public async Task Rename_SettingsWriteFailure_RestoresEveryArtifact()
    {
        var settings = Netch.Global.Settings;
        var modes = Netch.Global.Modes.ToArray();
        var suffix = Guid.NewGuid().ToString("N");
        var oldTag = "re-old-" + suffix;
        var newTag = "re-new-" + suffix;
        var failedTag = "re-fail-" + suffix;
        var directory = Path.Combine(Netch.Global.NetchDir, "mode", "Custom");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, suffix + ".json");
        var cacheDir = Path.Combine(Netch.Global.NetchDir, "bin", "rule-set");
        Directory.CreateDirectory(cacheDir);
        var oldCache = Path.Combine(cacheDir, oldTag + ".srs");
        var newCache = Path.Combine(cacheDir, newTag + ".srs");
        var failedCache = Path.Combine(cacheDir, failedTag + ".srs");
        var remote = new RuleSetConfig { Tag = oldTag, Url = "https://example.invalid/re.srs" };
        var mode = new Redirector { FullName = path, Rules = [new RouteRule { RuleSet = [oldTag] }] };
        try
        {
            Netch.Global.Settings = new Setting();
            Netch.Global.Settings.RuleSets.Add(remote);
            Netch.Global.Modes.Clear();
            Netch.Global.Modes.Add(mode);
            mode.WriteFile();
            File.WriteAllText(oldCache, "isolated-transaction-test");
            await Configuration.SaveStrictAsync();
            var settingsBefore = File.ReadAllBytes(Configuration.FileFullName);
            await RuleSetReferenceService.RenameRuleSetAsync(oldTag, new RuleSetConfig { Tag = newTag, Url = remote.Url });
            Assert.IsTrue(File.Exists(newCache));
            Assert.IsFalse(File.Exists(oldCache));
            settingsBefore = File.ReadAllBytes(Configuration.FileFullName);
            var modeBefore = File.ReadAllBytes(path);
            using (var locked = new FileStream(Configuration.FileFullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    _ = await RuleSetReferenceService.RenameRuleSetAsync(newTag,
                        new RuleSetConfig { Tag = failedTag, Url = remote.Url });
                    Assert.Fail("配置锁未阻止重命名");
                }
                catch (IOException)
                {
                }
            }
            var settingsAfter = File.ReadAllBytes(Configuration.FileFullName);
            var diagnostic = @"D:\Netch_RECOVERY_20261102\transaction_diagnostic";
            Directory.CreateDirectory(diagnostic);
            File.WriteAllBytes(Path.Combine(diagnostic, "before.json"), settingsBefore);
            File.WriteAllBytes(Path.Combine(diagnostic, "after.json"), settingsAfter);
            var first = settingsBefore.Zip(settingsAfter).TakeWhile(p => p.First == p.Second).Count();
            Console.WriteLine($"SETTINGS_DIFF lengths={settingsBefore.Length}/{settingsAfter.Length} first={first}");
            Console.WriteLine("BEFORE=" + System.Text.Encoding.UTF8.GetString(settingsBefore, Math.Max(0, first - 160), Math.Min(400, settingsBefore.Length - Math.Max(0, first - 160))));
            Console.WriteLine("AFTER=" + System.Text.Encoding.UTF8.GetString(settingsAfter, Math.Max(0, first - 160), Math.Min(400, settingsAfter.Length - Math.Max(0, first - 160))));
            Console.WriteLine($"EXCEPTION_RESOURCES={Netch.Global.Settings.RuleSets.Single().Tag}");
            CollectionAssert.AreEqual(settingsBefore, settingsAfter);
            CollectionAssert.AreEqual(modeBefore, File.ReadAllBytes(path));
            Assert.AreEqual(newTag, mode.Rules[0].RuleSet[0]);
            Assert.IsTrue(File.Exists(newCache));
            Assert.IsFalse(File.Exists(failedCache));
            Assert.IsFalse(Netch.Global.Settings.RuleSets.Any(r => r.Tag == failedTag));
        }
        finally
        {
            Netch.Global.Settings = settings;
            Netch.Global.Modes.Clear();
            Netch.Global.Modes.AddRange(modes);
            foreach (var file in new[] { path, path + ".bak", oldCache, newCache, failedCache })
                if (File.Exists(file))
                    File.Delete(file);
        }
    }
}
