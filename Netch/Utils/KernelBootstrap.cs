using System.IO.Compression;
using System.Reflection;

namespace Netch.Utils;

/// <summary>
///     内核随包自释放（咩咩 2026-10-31 拍板）：发版 exe 内嵌 bin/{sing-box,xray}.zip
///     （build.ps1 发布时从用户区内核压缩生成，KernelEmbed/*.zip；开发构建无此资源=静默跳过），
///     启动最早处（App.OnStartup）对「缺失或残缺」的内核自动释放。
///     覆盖语义（咩咩点名保护）：bin 里内核存在且 ≥MinBytes 就一个字节都不碰——用户自行升级/
///     替换的内核永不回写打包版；仅「不存在」或「残缺文件」（&lt;MinBytes，发版中断/杀毒截断）才自愈释放。
///     释放失败不拦启动：缺内核由 Guard 报「内核文件缺失」，用户走外部资源页手动下载。
/// </summary>
public static class KernelBootstrap
{
    /// <summary>内核残缺判定阈值：与外部资源页 MinBytes 同口径（&lt;1MB 的内核必是残件）</summary>
    public const long MinBytes = 1024 * 1024;

    /// <summary>(嵌入资源名, 内核文件名)。资源名 = RootNamespace + KernelEmbed 相对路径</summary>
    public static readonly (string ResourceName, string ExeName)[] Kernels =
    {
        ("Netch.KernelEmbed.sing-box.zip", "sing-box.exe"),
        ("Netch.KernelEmbed.xray.zip", "xray.exe")
    };

    /// <summary>启动早期调用（App.OnStartup，CWD 切到 NetchDir 之后）。全程静默不抛</summary>
    public static void EnsureKernels(string netchDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var (resourceName, exeName) in Kernels)
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                EnsureKernel(Path.Combine(netchDir, "bin"), exeName, stream);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Kernel release failed: {Exe}", exeName);
            }
        }
    }

    /// <summary>
    ///     单内核释放（harness 可测纯函数）。zipStream=null 视为「本构建未嵌入内核」：no-op。
    ///     bin 下 exeName 存在且 ≥MinBytes：不碰（用户自行升级的保护语义）。
    ///     否则从 zip 中解出 exeName 条目释放（残缺文件覆盖自愈）。
    /// </summary>
    public static void EnsureKernel(string binDir, string exeName, Stream? zipStream)
    {
        var target = Path.Combine(binDir, exeName);
        if (File.Exists(target) && new FileInfo(target).Length >= MinBytes)
            return; // 用户区有效内核：一个字节都不碰（含用户自行升级的版本）

        if (zipStream == null)
            return; // 开发构建未嵌入内核（KernelEmbed 无 zip），静默跳过

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            Log.Warning("Kernel zip missing entry {Entry}", exeName);
            return;
        }

        Directory.CreateDirectory(binDir);
        using (var entryStream = entry.Open())
        using (var fs = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write))
        {
            entryStream.CopyTo(fs);
        }

        File.Move(target + ".tmp", target, overwrite: true);
        Log.Information("Kernel released from package: {Exe} ({Bytes} bytes)", exeName, entry.Length);
    }
}
