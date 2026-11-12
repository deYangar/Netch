using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Netch.Models;
using Netch.Models.GitHubRelease;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Controllers;

/// <summary>
///     内核更新器：从官方 GitHub release 下载内核压缩包，按 asset digest（sha256）校验后安全替换 bin\ 下的内核。
///     替换前自动备份旧文件，替换后以 version 输出冒烟，失败自动回滚。
///     双核 sing-box / Xray 均走官方 release 通道
/// </summary>
public static class CoreUpdater
{
    public sealed class KernelChannel
    {
        /// <summary>bin\ 下的内核文件名，如 sing-box.exe</summary>
        public string ExeName { get; init; } = "";

        /// <summary>外部资源注册键（R 线：kernel.singbox / kernel.xray）</summary>
        public string ResourceKey { get; init; } = "";

        /// <summary>官方仓库 owner/repo</summary>
        public string Owner { get; init; } = "";

        public string Repo { get; init; } = "";

        /// <summary>asset 文件名必须包含的关键词（平台标识）</summary>
        public string AssetKeyword { get; init; } = "";

        /// <summary>asset 文件名包含即排除的关键词（如给 Win7 的 legacy 包）</summary>
        public string[] AssetExcludes { get; init; } = Array.Empty<string>();

        public string DisplayName { get; init; } = "";

        /// <summary>
        ///     版本输出解析委托；null 时用默认逻辑（输出中找 "version" 后的版本号，sing-box 形如 "sing-box version 1.14.4"）。
        ///     Xray 输出无 "version" 单词（首行 "Xray 26.10.21 (...)"），需自定义
        /// </summary>
        public Func<string, string?>? ParseVersion { get; init; }

        /// <summary>从 release 资产里挑出本平台内核包（如 windows-amd64.zip，排除 legacy 包）</summary>
        public Asset? MatchAsset(Release release)
        {
            return release.assets.FirstOrDefault(asset =>
                asset.name.Contains(AssetKeyword, StringComparison.OrdinalIgnoreCase) &&
                !AssetExcludes.Any(exclude => asset.name.Contains(exclude, StringComparison.OrdinalIgnoreCase)));
        }

        public override string ToString() => DisplayName;
    }

    public static readonly KernelChannel SingBox = new()
    {
        ExeName = "sing-box.exe",
        ResourceKey = "kernel.singbox",
        Owner = "SagerNet",
        Repo = "sing-box",
        AssetKeyword = "windows-amd64.zip",
        AssetExcludes = new[] { "legacy" },
        DisplayName = "sing-box"
    };

    public static readonly KernelChannel Xray = new()
    {
        ExeName = "xray.exe",
        ResourceKey = "kernel.xray",
        Owner = "XTLS",
        Repo = "Xray-core",
        // windows-64.zip 同时命中 Xray-windows-arm64-v8a.zip、.dgst 校验文件，一并排除
        AssetKeyword = "windows-64.zip",
        AssetExcludes = new[] { "arm", "32", "dgst" },
        DisplayName = "Xray",
        ParseVersion = output =>
        {
            foreach (var line in output.GetLines(false))
            {
                // 首行形如 "Xray 26.10.21 (Xray, Penetrates Everything.) Custom (go1.24 windows/amd64)"
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("Xray", StringComparison.OrdinalIgnoreCase))
                    return parts[1];
            }

            return null;
        }
    };

    /// <summary>
    ///     查询 bin\ 下内核的已安装版本（<c>kernel version</c> 输出首行）；文件不存在返回 null
    /// </summary>
    public static async Task<string?> GetInstalledVersionAsync(KernelChannel channel)
    {
        var exePath = GetKernelPath(channel);
        if (!File.Exists(exePath))
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "version",
            WorkingDirectory = Path.Combine(Global.NetchDir, "bin"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var parsed = channel.ParseVersion?.Invoke(output) ?? ParseVersionAfterKeyword(output);
        if (parsed != null)
            return parsed;

        Log.Warning("Unrecognized {Name} version output: {Output}", channel.ExeName, output);
        return null;
    }

    private static string? ParseVersionAfterKeyword(string output)
    {
        // sing-box 输出形如 "sing-box version 1.14.4"；取 version 后的版本号
        foreach (var line in output.GetLines(false))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("version", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    ///     拉取官方最新 stable release（过滤 prerelease）；网络失败等异常上抛，无 stable release 返回 null
    /// </summary>
    public static async Task<Release?> GetLatestStableAsync(KernelChannel channel)
    {
        var url = $"https://api.github.com/repos/{channel.Owner}/{channel.Repo}/releases";
        var (_, json) = await WebUtil.DownloadStringAsync(WebUtil.CreateResourceRequest(url));

        var releases = JsonSerializer.Deserialize<List<Release>>(json)!.Where(release => !release.prerelease);
        // tag 形如 v1.14.4，剥掉 v 前缀后 VersionComparer 才能正确排序
        return releases.OrderByDescending(release => NormalizeVersion(release.tag_name), new VersionUtil.VersionComparer()).FirstOrDefault();
    }

    /// <summary>
    ///     检查更新：拉官方 release（过滤 prerelease），与本地版本比较。
    ///     返回有新版的 release；已最新返回 null。本地内核缺失时返回最新 release 以支持重新获取
    /// </summary>
    public static async Task<Release?> CheckUpdateAsync(KernelChannel channel)
    {
        var latest = await GetLatestStableAsync(channel);
        if (latest == null)
            throw new MessageException(I18N.T("update.fetchKernelReleaseFailed"));

        var installed = await GetInstalledVersionAsync(channel);
        if (installed == null)
        {
            // bin 下内核缺失（如被误删/杀软误报隔离），提供重新获取入口
            Log.Warning("{Name} not found in bin, offer a fresh download", channel.ExeName);
            return latest;
        }

        return VersionUtil.CompareVersion(NormalizeVersion(latest.tag_name), installed) > 0 ? latest : null;
    }

    /// <summary>
    ///     目标内核是否正在运行（停-换-启判定：仅当前 ServerController 就是目标内核才需要停；
    ///     另一个内核在跑不阻碍本次文件替换）
    /// </summary>
    internal static bool IsRunningChannel(KernelChannel channel)
    {
        return MainController.ServerController switch
        {
            Servers.SingboxController => channel == SingBox,
            Servers.XrayController => channel == Xray,
            _ => false
        };
    }

    /// <summary>替换成功/失败后是否拉起内核：曾在跑 且 用户开了内核自动重启（R 线 §4.3）</summary>
    internal static bool ShouldRestartAfterUpdate(bool wasRunning)
    {
        return wasRunning && Global.Settings.Network.KernelAutoRestart;
    }

    /// <summary>
    ///     停机编排注入点（F-4a，2026-11-04 审查）：默认直调 MainController（harness 沙箱无 VM）；
    ///     WPF 装配时由 MainViewModel 注入状态机路径——停机经 VM 的 StopCoreAsync，
    ///     State 与 UI 全程知情（此前内核更新绕过状态机，更新失败后状态栏仍显示「运行中」）
    /// </summary>
    internal static Func<KernelChannel, Task>? StopRunner;

    /// <summary>拉起编排注入点（同上）：默认 MainController.StartAsync；WPF 注入 StartStopAsync（附带任务全跑）</summary>
    internal static Func<KernelChannel, Task>? RestartRunner;

    private static Task StopDirectAsync(KernelChannel channel)
    {
        Log.Information("Stopping running kernel {Name} before update", channel.ExeName);
        return MainController.StopAsync();
    }

    private static Task RestartDirectAsync(KernelChannel channel)
    {
        Log.Information("Restarting kernel after update: {Server} {Mode}", MainController.Server!.Type, MainController.Mode!.i18NRemark);
        return MainController.StartAsync(MainController.Server, MainController.Mode);
    }

    /// <summary>
    ///     更新内核：先下载（内核未停，内置代理可用且服务不中断）→ 停机 → 备份替换 → version 冒烟 →
    ///     按 KernelRestart 设置拉起；替换失败回滚旧文件。停机窗口压到替换级秒数，
    ///     下载失败时内核从未停止（stopped=false 不触发拉起），服务全程无感。
    ///     停/启经 StopRunner/RestartRunner（VM 注入则走状态机，F-4a）；拉起失败不再吞主体异常（F-4b）
    /// </summary>
    public static async Task UpdateAsync(KernelChannel channel, Release release, Asset asset, IProgress<int>? progress = null)
    {
        var wasRunning = IsRunningChannel(channel);
        var exePath = GetKernelPath(channel);

        // 目标内核未被本程序运行（未运行或跑的是另一内核）时，下载前确认文件未被第三方占用，失败提前暴露不白下大包
        if (!wasRunning && File.Exists(exePath))
            EnsureKernelFileNotLocked(exePath, channel);

        var workDir = Path.Combine(Global.NetchDir, "data", "core-update");
        Directory.CreateDirectory(workDir);
        var stopped = false;
        Exception? mainError = null;
        try
        {
            var kernelFile = await FetchCoreAsync(channel, asset, workDir, progress);

            if (wasRunning)
            {
                await (StopRunner ?? StopDirectAsync)(channel);
                await WaitForKernelFileReleasedAsync(channel);
                stopped = true;
            }

            await ReplaceCoreAsync(channel, release, kernelFile);
        }
        catch (Exception e)
        {
            mainError = e;
            throw;
        }
        finally
        {
            if (stopped && ShouldRestartAfterUpdate(wasRunning) && MainController.Server != null && MainController.Mode != null)
            {
                try
                {
                    await (RestartRunner ?? RestartDirectAsync)(channel);
                }
                catch (Exception restartError)
                {
                    // F-4b（2026-11-04 审查）：finally 里抛新异常会替换掉主体异常，日志只见重启失败、
                    // 替换失败被吞。主体已失败时拉起失败只记录+提示，主体异常优先传播；
                    // 主体成功而拉起失败（新内核起不来）才把拉起异常上抛
                    Log.Error(restartError, "Restart kernel after update failed");
                    if (mainError == null)
                        throw;

                    Global.UIHub.NotifyTip(I18N.T("update.restartFailed0") + "\n" + restartError.Message, info: false);
                }
            }

            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, true);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Clean core-update workdir failed");
            }
        }
    }

    /// <summary>下载校验解压（不触碰 bin 下内核文件，无需停机）</summary>
    private static async Task<string> FetchCoreAsync(KernelChannel channel, Asset asset, string workDir, IProgress<int>? progress)
    {
        var zipPath = Path.Combine(workDir, asset.name);
        var extractDir = Path.Combine(workDir, "extracted");

        Log.Information("Downloading {Asset} from {Url}", asset.name, asset.browser_download_url);
        // 内核此时仍在服务：外部资源经内置代理下载（ExternalResources.ViaProxy），大包直连不再被强加于停机窗口
        await WebUtil.DownloadFileAsync(WebUtil.CreateResourceRequest(asset.browser_download_url, 120_000), zipPath, progress);

        VerifyChecksum(zipPath, asset);

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        var kernelFile = Directory.EnumerateFiles(extractDir, channel.ExeName, SearchOption.AllDirectories).FirstOrDefault();
        if (kernelFile == null)
            throw new MessageException(I18N.T("update.kernelFileNotFoundRelease"));

        return kernelFile;
    }

    /// <summary>备份替换（调用方已按需停机）：替换后 version 冒烟，失败回滚旧文件</summary>
    private static async Task ReplaceCoreAsync(KernelChannel channel, Release release, string kernelFile)
    {
        var exePath = GetKernelPath(channel);

        // 停机后仍可能有第三方短暂占用（Defender 扫描新映像等），替换前确认独占可开
        if (File.Exists(exePath))
            EnsureKernelFileNotLocked(exePath, channel);

        // 重新获取场景 bin 目录可能已被整体清空（杀软隔离/误删），先确保目标目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

        var backupPath = exePath + ".bak";

        // 备份旧内核（缺失场景为重新获取，无备份），替换后冒烟失败即回滚
        var hadBackup = File.Exists(exePath);
        if (hadBackup)
            File.Copy(exePath, backupPath, true);

        try
        {
            File.Copy(kernelFile, exePath, true);
            var installed = await GetInstalledVersionAsync(channel);
            if (installed == null || VersionUtil.CompareVersion(installed, NormalizeVersion(release.tag_name)) != 0)
                throw new MessageException(I18N.T("update.kernelSmokeCheckFailedRolled"));

            if (hadBackup)
                File.Delete(backupPath);

            Log.Information("{Name} updated to {Version}", channel.ExeName, installed);
        }
        catch
        {
            if (hadBackup)
            {
                File.Copy(backupPath, exePath, true);
                File.Delete(backupPath);
            }
            else if (File.Exists(exePath))
            {
                File.Delete(exePath);
            }

            throw;
        }
    }

    private static string GetKernelPath(KernelChannel channel)
    {
        return Path.Combine(Global.NetchDir, "bin", channel.ExeName);
    }

    /// <summary>sing-box 等 tag 带 v 前缀（v1.14.4），与纯版本号比较前剥掉</summary>
    private static string NormalizeVersion(string tag)
    {
        return tag.TrimStart('v');
    }

    private static void EnsureKernelFileNotLocked(string exePath, KernelChannel channel)
    {
        try
        {
            using (new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070020) || e.HResult == unchecked((int)0x80070021))
        {
            // 0x20 sharing violation / 0x21 lock violation 才是真正的文件占用；其余 IOException 原样上抛避免误报
            throw new MessageException(I18N.TF("update.0FileUse", channel.ExeName));
        }
    }

    /// <summary>
    ///     等待内核文件锁释放：进程终止到句柄释放存在秒级窗口（VM 实测 ~2.6s，含 Defender 对新映像的扫描），
    ///     StopAsync 返回后立即替换会确定性撞上 sharing violation。最多等 10s，超时按文件占用失败；
    ///     其余 IOException 原样上抛，与 EnsureKernelFileNotLocked 同口径
    /// </summary>
    private static async Task WaitForKernelFileReleasedAsync(KernelChannel channel)
    {
        var exePath = GetKernelPath(channel);
        if (!File.Exists(exePath))
            return;

        for (var i = 0; i < 40; i++)
        {
            try
            {
                using (new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return;
            }
            catch (IOException e) when (e.HResult == unchecked((int)0x80070020) || e.HResult == unchecked((int)0x80070021))
            {
                await Task.Delay(250);
            }
        }

        throw new MessageException(I18N.TF("update.0FileUse", channel.ExeName));
    }

    private static void VerifyChecksum(string filePath, Asset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.digest) || !asset.digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            // 无校验和的下载源不做静默安装，宁可不更新
            throw new MessageException(I18N.T("update.downloadSourceDidNotProvide"));

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
        var expected = asset.digest["sha256:".Length..].Trim().ToLowerInvariant();

        if (actual != expected)
        {
            Log.Error("Kernel checksum mismatch: expected {Expected}, actual {Actual}", expected, actual);
            throw new MessageException(I18N.T("update.kernelChecksumMismatchUpdateCancelled"));
        }
    }
}
