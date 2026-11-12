using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using Netch.Enums;
using Netch.Localization;
using Netch.Models;
using Netch.Services;
using Netch.UI.Hub;

namespace Netch.Utils;

/// <summary>
///     设置页备份/恢复（2026-11-11）。统一 zip 载体，manifest 标类型：
///     Settings（仅设置）＝ settings.json 剥离版，Server / Subscription / RuleSets / Profiles 四集合清空
///     （节点不备、订阅不留、分流规则集不备；Profiles 引用节点 Remark，节点不备则它是死数据，一并剥离）；
///     Full（完整备份）＝ settings.json 全量 + mode\Custom\ 用户自建模式（内置模式随程序分发，不备份）。
///     恢复语义：同名覆盖、新增补齐，不删除目标处多余文件——恢复操作绝不删用户数据。
/// </summary>
[Fody.ConfigureAwait(true)] // 程序集级 false 会把 continuation 丢线程池；恢复链里的 ModeService.Load 改 UI 绑定的 Global.Modes，必须留在 UI 线程
public static class BackupService
{
    private const int ManifestVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const string SettingsEntryName = "settings.json";
    private const string ModeEntryPrefix = "mode/";

    /// <summary>用户自建模式目录（Updater.KeepDirectories / RuleSetReferenceService.IsWritable 同口径）</summary>
    private static string ModeCustomDirectory => Path.Combine(ModeService.Instance.ModeDirectoryFullName, "Custom");

    public enum BackupType
    {
        Settings,
        Full
    }

    public sealed class BackupManifest
    {
        public string Type { get; set; } = "";
        public int Version { get; set; }
        public string CreatedAt { get; set; } = "";
        public string App { get; set; } = "";
    }

    #region 备份

    public static async Task BackupAsync(BackupType type)
    {
        var defaultName = $"Netch-{(type == BackupType.Settings ? "Settings" : "Full")}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var dialog = new SaveFileDialog
        {
            Title = I18N.T("settings.tools.backupTitle"),
            Filter = "ZIP (*.zip)|*.zip",
            FileName = defaultName,
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return; // 用户取消，静默返回

        try
        {
            // 设置页保存是 500ms 防抖，先强制落盘再备份，保证备到最新状态
            await Configuration.SaveAsync();

            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
            var createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (type == BackupType.Settings)
                WriteSettingsBackup(dialog.FileName, Global.Settings, version, createdAt);
            else
                WriteFullBackup(dialog.FileName, Configuration.FileFullName, ModeCustomDirectory, version, createdAt);

            Log.Information("Backup ({Type}) saved to {Path}", type, dialog.FileName);
            Global.UIHub.NotifyTip(I18N.TF("settings.tools.backupDone", dialog.FileName));
        }
        catch (Exception e)
        {
            Log.Error(e, "Backup ({Type}) failed", type);
            Global.UIHub.Alert(I18N.TF("settings.tools.backupFailed", e.Message), DialogLevel.Error);
        }
    }

    /// <summary>仅设置备份：序列化剥离版（四集合清空，其余字段全量）</summary>
    internal static void WriteSettingsBackup(string zipPath, Setting source, string appVersion, string createdAt)
    {
        var stripped = source.ShallowCopy();
        stripped.Server = new();
        stripped.Subscription = new();
        stripped.RuleSets = new();
        stripped.Profiles = new();

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteManifest(archive, BackupType.Settings, appVersion, createdAt);

        var entry = archive.CreateEntry(SettingsEntryName);
        using (var stream = entry.Open())
        {
            JsonSerializer.Serialize(stream, stripped, Configuration.JsonSerializerOptions);
        }
    }

    /// <summary>完整备份：settings.json 原文件字节 + mode\Custom\ 递归（目录不存在则模式部分为空）</summary>
    internal static void WriteFullBackup(string zipPath, string settingsJsonPath, string modeCustomDirectory, string appVersion, string createdAt)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteManifest(archive, BackupType.Full, appVersion, createdAt);
        archive.CreateEntryFromFile(settingsJsonPath, SettingsEntryName);

        if (!Directory.Exists(modeCustomDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(modeCustomDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(modeCustomDirectory, file);
            archive.CreateEntryFromFile(file, ModeEntryPrefix + relative.Replace('\\', '/'));
        }
    }

    private static void WriteManifest(ZipArchive archive, BackupType type, string appVersion, string createdAt)
    {
        var manifest = new BackupManifest
        {
            Type = type.ToString(),
            Version = ManifestVersion,
            CreatedAt = createdAt,
            App = appVersion
        };

        var entry = archive.CreateEntry(ManifestEntryName);
        using (var stream = entry.Open())
        {
            JsonSerializer.Serialize(stream, manifest);
        }
    }

    #endregion

    #region 恢复

    public static async Task RestoreAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = I18N.T("settings.tools.restoreTitle"),
            Filter = "ZIP (*.zip)|*.zip"
        };
        if (dialog.ShowDialog() != true)
            return; // 用户取消，静默返回

        BackupManifest manifest;
        try
        {
            manifest = ReadManifest(dialog.FileName);
        }
        catch (Exception e)
        {
            Log.Error(e, "Read backup manifest failed: {Path}", dialog.FileName);
            Global.UIHub.Alert(I18N.TF("settings.tools.restoreInvalidFile", e.Message), DialogLevel.Error);
            return;
        }

        var isFull = manifest.Type == nameof(BackupType.Full);
        if (!await Global.UIHub.ConfirmAsync(I18N.TF(
                isFull ? "settings.tools.restoreConfirmFull" : "settings.tools.restoreConfirmSettings",
                manifest.CreatedAt)))
            return;

        Global.UIHub.StatusText(I18N.T("settings.tools.restoring"));
        try
        {
            // 阶段一：反序列化 + 校验 + 解包全部在临时态完成，任何失败零副作用
            Setting restored;
            using (var archive = ZipFile.OpenRead(dialog.FileName))
            {
                var settingsEntry = archive.GetEntry(SettingsEntryName) ?? throw new MessageException("missing settings.json");
                using var stream = settingsEntry.Open();
                restored = await JsonSerializer.DeserializeAsync<Setting>(stream, Configuration.JsonSerializerOptions)
                           ?? throw new MessageException("settings.json is empty");
            }

            var merged = isFull ? restored : MergeSettingsRestore(restored, Global.Settings);
            Configuration.CheckSetting(merged);

            var modeTempDir = isFull
                ? ExtractModeEntries(dialog.FileName, Path.Combine(Path.GetTempPath(), "NetchRestore_" + Guid.NewGuid().ToString("N")))
                : null;

            try
            {
                // 阶段二：运行中先停——恢复的端口 / DNS 等设置不能与运行态冲突
                if (RequiresStop(Global.UIHub.State))
                {
                    if (App.CurrentMain == null)
                        throw new MessageException("main window not ready");

                    await App.CurrentMain.StopAsync();
                }

                // 阶段三：提交（Global.Settings 实例不变，UI 绑定不断）
                Global.Settings.Set(merged);
                await Configuration.SaveAsync();

                if (modeTempDir != null)
                {
                    CopyDirectoryOverwrite(modeTempDir, ModeCustomDirectory);
                    ModeService.Instance.Load();
                }

                Log.Information("Restored {Type} backup from {Path} (created {CreatedAt})", manifest.Type, dialog.FileName, manifest.CreatedAt);
                Global.UIHub.Alert(I18N.T("settings.tools.restoreDone"));
            }
            finally
            {
                if (modeTempDir != null && Directory.Exists(modeTempDir))
                    Directory.Delete(modeTempDir, recursive: true);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Restore backup failed: {Path}", dialog.FileName);
            Global.UIHub.Alert(I18N.TF("settings.tools.restoreFailed", e.Message), DialogLevel.Error);
        }
        finally
        {
            Global.UIHub.StatusText(null);
        }
    }

    /// <summary>校验 zip 是合法的本服务备份（坏 zip / 缺 manifest / 版本超前 / 类型未知均抛 MessageException）</summary>
    internal static BackupManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(ManifestEntryName) ?? throw new MessageException("missing manifest.json");

        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream) ?? throw new MessageException("manifest.json is empty");

        if (manifest.Version > ManifestVersion)
            throw new MessageException($"unsupported backup version {manifest.Version}");

        if (manifest.Type != nameof(BackupType.Settings) && manifest.Type != nameof(BackupType.Full))
            throw new MessageException($"unknown backup type \"{manifest.Type}\"");

        return manifest;
    }

    /// <summary>Settings 型备份的四集合在备份时已剥离；恢复时保留当前运行态的这四项（节点/订阅/分流规则集/快捷配置不动）。
    ///     防御手工编辑过的 zip：即使剥离版里带了数据，也一律以当前态为准。</summary>
    internal static Setting MergeSettingsRestore(Setting restored, Setting current)
    {
        restored.Server = current.Server;
        restored.Subscription = current.Subscription;
        restored.RuleSets = current.RuleSets;
        restored.Profiles = current.Profiles;
        return restored;
    }

    /// <summary>把 zip 内 mode/ 前缀条目解到临时目录（正斜杠还原平台分隔符）。
    ///     恶意 entry 名（..\、盘符、绝对路径）一律拒绝：先 GetFullPath 规范化，再验证必须落在 tempRoot 之内。</summary>
    internal static string ExtractModeEntries(string zipPath, string tempRoot)
    {
        tempRoot = Path.GetFullPath(tempRoot);
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(ModeEntryPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = entry.FullName.Substring(ModeEntryPrefix.Length);
            if (relative.Length == 0 || entry.FullName.EndsWith('/'))
                continue; // mode/ 目录条目本身

            var target = Path.GetFullPath(Path.Combine(tempRoot, relative.Replace('/', '\\')));
            if (!target.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new MessageException($"illegal entry path \"{entry.FullName}\"");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var dest = File.Create(target);
            source.CopyTo(dest);
        }

        return tempRoot;
    }

    /// <summary>目录合并拷贝：同名覆盖、新增补齐、不删除目标处多余文件（恢复不删用户数据）</summary>
    internal static void CopyDirectoryOverwrite(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool RequiresStop(State state)
    {
        // Waiting/Stopped = 未运行；Terminating = 程序退出中，无需干预
        return state is State.Starting or State.Started or State.Stopping or State.Degraded;
    }

    #endregion
}
