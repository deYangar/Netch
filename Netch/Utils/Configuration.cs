using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;
using Netch.JsonConverter;
using Netch.Models;

namespace Netch.Utils;

public static class Configuration
{
    /// <summary>
    ///     数据目录
    /// </summary>
    public static string DataDirectoryFullName => Path.Combine(Global.NetchDir, "data");

    public static string FileFullName => Path.Combine(DataDirectoryFullName, FileName);

    private static string BackupFileFullName => Path.Combine(DataDirectoryFullName, BackupFileName);

    private const string FileName = "settings.json";

    private const string BackupFileName = "settings.json.bak";

    private static readonly AsyncReaderWriterLock _lock = new(null);

    /// <summary>
    ///     配置序列化统一 options（Server 多态 converter + 枚举字符串）。
    ///     internal 暴露给 BackupService：备份/恢复的 Setting 序列化必须与主配置同一套 converter
    /// </summary>
    internal static readonly JsonSerializerOptions JsonSerializerOptions = Global.NewCustomJsonSerializerOptions();

    static Configuration()
    {
        JsonSerializerOptions.Converters.Add(new ServerConverterWithTypeDiscriminator());
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(FileFullName))
            {
                await SaveAsync();
                return;
            }

            await using var _ = await _lock.ReadLockAsync();

            if (await LoadCoreAsync(FileFullName))
                return;

            Log.Information("Load backup configuration \"{FileName}\"", BackupFileFullName);
            // C-1 补全（2026-11-04 实测）：主备双败时 LoadCoreAsync 吞异常返回 false，
            // 此前静默落到默认空配置继续启动——用户配置全丢却毫无提示。显式抛出走 catch 弹窗
            if (!await LoadCoreAsync(BackupFileFullName))
                throw new MessageException("settings.json and backup both damaged");
        }
        catch (Exception e)
        {
            Log.Error(e, "Load configuration failed");
            // C-1（2026-11-04 审查）：双损坏时静默 Exit 无提示、无窗口，排障无从下手。
            // logger 已在 App 启动前段装配（日志已留），此处再弹窗明示后退出（bin 检查同款先例）
            Localization.I18N.SetLanguage("System");
            System.Windows.MessageBox.Show(Localization.I18N.T("configuration.loadFailedExit"));
            Environment.Exit(-1);
        }
    }

    private static async ValueTask<bool> LoadCoreAsync(string filename)
    {
        try
        {
            Setting settings;

            await using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                settings = (await JsonSerializer.DeserializeAsync<Setting>(fs, JsonSerializerOptions))!;
            }

            CheckSetting(settings);
            Global.Settings = settings;
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e, "Load configuration file \"{FileName}\" error ", filename);
            return false;
        }
    }

    internal static void CheckSetting(Setting settings)
    {
        settings.Profiles.RemoveAll(p => p.ServerRemark == string.Empty || p.ModeRemark == string.Empty);

        if (settings.Profiles.Any(p => settings.Profiles.Any(p1 => p1 != p && p1.Index == p.Index)))
            for (var i = 0; i < settings.Profiles.Count; i++)
                settings.Profiles[i].Index = i;

        // 内核选择全自动化（2026-10-31 咩咩拍板）：存量 V2raySN（槽位引擎从未存在）/ SingBox（手选已移除）→ Auto
        if (settings.Kernel.Core != KernelGlobal.Auto)
            settings.Kernel.Core = KernelGlobal.Auto;
    }

    /// <summary>
    ///     保存配置
    /// </summary>
    public static Task SaveAsync() => SaveCoreAsync(throwOnError: false);

    /// <summary>规则集重命名等事务式操作必须知道持久化是否成功（RE-1§2.7）</summary>
    public static Task SaveStrictAsync() => SaveCoreAsync(throwOnError: true);

    private static async Task SaveCoreAsync(bool throwOnError)
    {
        if (_lock.IsWriteLockHeld)
        {
            if (throwOnError)
                throw new InvalidOperationException("Configuration write lock is already held");
            return;
        }

        try
        {
            await using var _ = await _lock.WriteLockAsync();
            Log.Verbose("Save Configuration");

            if (!Directory.Exists(DataDirectoryFullName))
                Directory.CreateDirectory(DataDirectoryFullName);

            var tempFile = Path.Combine(DataDirectoryFullName, FileFullName + ".tmp");
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(fileStream, Global.Settings, JsonSerializerOptions);
            }

            if (File.Exists(FileFullName))
            {
                // 目标文件被占用时先复制旧内容：File.Replace 可能在覆盖目标后才因备份失败抛错。
                // 失败发生在真正替换之前，事务调用方才仍持有完整旧配置。
                File.Copy(FileFullName, BackupFileFullName, overwrite: true);
                File.Replace(tempFile, FileFullName, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempFile, FileFullName);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Save Configuration error");
            if (throwOnError)
                throw;
        }
    }

    /// <summary>
    ///     按字节恢复 settings.json（事务回滚用，低危⑫ 2026-11-04 审查）：调用方持有恢复前快照字节，
    ///     不能走 SaveAsync（内存态可能已被并发改动，序列化产物≠快照）；
    ///     与 SaveCoreAsync 同模式：持写锁 + tmp 同目录 + File.Replace 原子替换，并发保存无竞态窗口
    /// </summary>
    public static async Task RestoreBytesAsync(byte[] content)
    {
        await using var _ = await _lock.WriteLockAsync();

        var tempFile = Path.Combine(DataDirectoryFullName, FileFullName + ".restore.tmp");
        await File.WriteAllBytesAsync(tempFile, content);

        if (File.Exists(FileFullName))
            File.Replace(tempFile, FileFullName, destinationBackupFileName: null);
        else
            File.Move(tempFile, FileFullName);
    }
}