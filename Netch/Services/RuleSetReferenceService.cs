using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Models.Modes.TunMode;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Services;

/// <summary>规则集 tag → 引用它的模式清单；重命名同步/删除保护/引用计数共用（RE-1§2.7）</summary>
public static class RuleSetReferenceService
{
    public static List<Mode> ReferencingModes(string tag)
    {
        return Global.Modes.Where(m => IsWritable(m) && Rules(m)?.Any(r => r.RuleSet?.Contains(tag) == true) == true).ToList();
    }

    /// <summary>同步全部可写 JSON 自定义模式引用，写失败时恢复内存中的原 tag</summary>
    public static int RenameReferences(string oldTag, string newTag)
    {
        if (oldTag == newTag)
            return 0;

        var affected = ReferencingModes(oldTag);
        var backups = affected.ToDictionary(m => m, m => File.ReadAllBytes(m.FullName));
        var count = 0;
        try
        {
            foreach (var mode in affected)
            {
                foreach (var rule in Rules(mode)!)
                    for (var i = 0; i < (rule.RuleSet?.Count ?? 0); i++)
                        if (rule.RuleSet![i] == oldTag)
                        {
                            rule.RuleSet[i] = newTag;
                            count++;
                        }
                mode.WriteFile();
            }
        }
        catch
        {
            RestoreModes(backups, newTag, oldTag);
            throw;
        }

        return count;
    }

    /// <summary>统一持久化重命名：任一写入失败，模式、设置和 Remote 缓存均恢复原状。</summary>
    public static async Task<int> RenameRuleSetAsync(string oldTag, RuleSetConfig newConfig)
    {
        var oldConfig = Global.Settings.RuleSets.FirstOrDefault(r => r.Tag == oldTag)
                        ?? throw new MessageException(I18N.TF("kernel.unknownRuleSet0", oldTag));
        if (oldTag != newConfig.Tag && (Servers.BuiltInRuleSets.IsRegistered(newConfig.Tag) ||
                                       Global.Settings.RuleSets.Any(r => r.Tag == newConfig.Tag)))
            throw new MessageException(I18N.T("rulesets.duplicateTag"));

        var resource = ExternalResourceService.Find(ExternalResourceService.CustomRuleSetKey(oldTag));
        var oldCache = resource != null && ExternalResourceService.Exists(resource)
            ? ExternalResourceService.GetFullPath(resource)
            : null;
        var oldState = Global.Settings.ExternalResources.Items.FirstOrDefault(i => i.Key == ExternalResourceService.CustomRuleSetKey(oldTag));
        var settingsPath = Configuration.FileFullName;
        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var oldIndex = Global.Settings.RuleSets.IndexOf(oldConfig);
        var affected = ReferencingModes(oldTag);
        var modeBackups = affected.ToDictionary(m => m, m => File.ReadAllBytes(m.FullName));
        var cacheBytes = oldCache == null ? null : File.ReadAllBytes(oldCache);
        var newCache = oldCache == null ? null : Path.Combine(Global.NetchDir, ExternalResourceService.CustomRuleSetLocalPath(newConfig.Tag));
        var oldNewCacheBytes = newCache != null && File.Exists(newCache) ? File.ReadAllBytes(newCache) : null;
        var hadNewCache = oldNewCacheBytes != null;
        var count = RenameReferences(oldTag, newConfig.Tag);
        try
        {
            if (oldCache != null && newCache != null && oldTag != newConfig.Tag)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newCache)!);
                var temp = newCache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(oldCache, temp);
                if (hadNewCache)
                    File.Replace(temp, newCache, null);
                else
                    File.Move(temp, newCache);
                if (oldCache != newCache)
                    File.Delete(oldCache);
            }

            if (oldState != null)
                oldState.Key = ExternalResourceService.CustomRuleSetKey(newConfig.Tag);
            Global.Settings.RuleSets[oldIndex] = newConfig;
            await Configuration.SaveStrictAsync();
            return count;
        }
        catch
        {
            // 不再走第二次 RenameReferences：那会产生额外 .bak，并可能掩盖最初的写入异常。
            RestoreModes(modeBackups, newConfig.Tag, oldTag);
            Global.Settings.RuleSets[oldIndex] = oldConfig;
            if (oldState != null)
                oldState.Key = ExternalResourceService.CustomRuleSetKey(oldTag);
            if (settingsBefore != null)
                // 低危⑫（2026-11-04 审查）：直写主文件绕过 Configuration 的锁与原子写，与并发保存存在竞态窗口；
                // 改走同模式原子恢复（持锁 + tmp 同目录 + File.Replace）
                await Configuration.RestoreBytesAsync(settingsBefore);
            if (oldCache != null && cacheBytes != null)
                File.WriteAllBytes(oldCache, cacheBytes);
            if (newCache != null && oldCache != newCache)
            {
                if (hadNewCache)
                    File.WriteAllBytes(newCache, oldNewCacheBytes!);
                else if (File.Exists(newCache))
                    File.Delete(newCache);
            }
            throw;
        }
    }

    private static void RestoreModes(Dictionary<Mode, byte[]> backups, string changedTag, string originalTag)
    {
        foreach (var (mode, bytes) in backups)
        {
            foreach (var rule in Rules(mode)!)
                for (var i = 0; i < (rule.RuleSet?.Count ?? 0); i++)
                    if (rule.RuleSet![i] == changedTag)
                        rule.RuleSet[i] = originalTag;
            File.WriteAllBytes(mode.FullName, bytes);
        }
    }

    private static List<RouteRule>? Rules(Mode mode) => mode switch
    {
        TunMode t => t.Rules,
        Redirector p => p.Rules,
        _ => null
    };

    private static bool IsWritable(Mode mode)
    {
        if (!mode.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = Path.GetRelativePath(ModeService.Instance.ModeDirectoryFullName, mode.FullName);
        return !relative.StartsWith("..", StringComparison.Ordinal) && relative.StartsWith("Custom" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
