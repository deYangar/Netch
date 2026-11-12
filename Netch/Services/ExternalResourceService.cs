using Netch.Controllers;
using Netch.Localization;
using Netch.Models;
using Netch.Utils;

namespace Netch.Services;

/// <summary>
///     外部资源统一管理（R 线 §4-5）：资源注册表 + 状态持久化 + 下载校验管线 + .trash 隔离删除。
///     治本原则：校验 → 原子替换 → 失败保旧文件，状态如实回显，不做静默降级。
/// </summary>
public static class ExternalResourceService
{
    /// <summary>删除隔离区（相对安装目录）：隔次启动清理，删除可人工恢复</summary>
    public const string TrashDirName = ".trash";

    /// <summary>全局资源操作互斥（手动更新 / 自动更新 / 删除串行，避免并发写 bin；R 线 §4.5）</summary>
    public static readonly SemaphoreSlim BusyLock = new(1, 1);

    #region 注册表（内置资源；BuiltInRuleSets 同款模式）

    private static readonly List<ExternalResource> BuiltInResources =
    [
        // 内核（GitHub Release 通道）
        new()
        {
            Key = "kernel.singbox", DisplayNameKey = "external.kernel.singbox",
            Kind = ExternalResourceKind.Kernel, Provider = ExternalResourceProviderKind.GitHubRelease,
            LocalPath = @"bin\sing-box.exe",
            Caps = ExternalResourceCaps.CanDisable | ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = false, MinBytes = 1024 * 1024
        },
        new()
        {
            Key = "kernel.xray", DisplayNameKey = "external.kernel.xray",
            Kind = ExternalResourceKind.Kernel, Provider = ExternalResourceProviderKind.GitHubRelease,
            LocalPath = @"bin\xray.exe",
            Caps = ExternalResourceCaps.CanDisable | ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = false, MinBytes = 1024 * 1024
        },

        // 内置规则集快照（SagerNet 主源 + MetaCubeX sing 备胎；R 线 §1.3/§4.2）
        // 全部走 jsDelivr CDN（咩咩 2026-10-29 拍板）：raw.githubusercontent 直连不稳，CDN 国内可达；
        // 备胎路径实测修正：方案模板 rule-set/{geosite,geoip}/xxx.srs 已 404，
        // 实际仓库结构 sing 分支 geo/{geosite,geoip}/cn.srs（HTTP 200 验证）
        new()
        {
            Key = "ruleset.geosite-cn", DisplayNameKey = "external.ruleset.geositeCn",
            Kind = ExternalResourceKind.RuleSet, Provider = ExternalResourceProviderKind.HttpFile,
            LocalPath = @"bin\rule-set\geosite-cn.srs",
            Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geosite@rule-set/geosite-cn.srs",
            FallbackUrl = "https://cdn.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/geosite/cn.srs",
            Caps = ExternalResourceCaps.CanDisable | ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = true, MinBytes = 1024
        },
        new()
        {
            Key = "ruleset.geoip-cn", DisplayNameKey = "external.ruleset.geoipCn",
            Kind = ExternalResourceKind.RuleSet, Provider = ExternalResourceProviderKind.HttpFile,
            LocalPath = @"bin\rule-set\geoip-cn.srs",
            Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geoip@rule-set/geoip-cn.srs",
            FallbackUrl = "https://cdn.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/geoip/cn.srs",
            Caps = ExternalResourceCaps.CanDisable | ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = true, MinBytes = 1024
        },

        // IP 归属地库（连接页归属地 + NAT 检测国家码消费；官方数据 v3 五段格式，
        // 探针实测 88.221.123.186 → Germany|Hamburg|Hamburg|Akamai|DE。
        // v6 库 37MB 超 jsDelivr 20MB 限制（咩咩 2026-10-30 拍板）：不走 CDN，
        // GitHub raw 直链（用户网络能下就下），下不动在设置页勾选「外部资源经内置代理下载」）
        new()
        {
            Key = "geo.ip2region", DisplayNameKey = "external.geo.ip2region",
            Kind = ExternalResourceKind.DataFile, Provider = ExternalResourceProviderKind.HttpFile,
            LocalPath = @"bin\ip2region.xdb",
            Url = "https://cdn.jsdelivr.net/gh/lionsoul2014/ip2region@master/data/ip2region_v4.xdb",
            FallbackUrl = "https://fastly.jsdelivr.net/gh/lionsoul2014/ip2region@master/data/ip2region_v4.xdb",
            // 不提供禁用（归属地功能本体内建）；不提供 CanDisable
            Caps = ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = true, MinBytes = 1024 * 1024
        },
        new()
        {
            Key = "geo.ip2region-v6", DisplayNameKey = "external.geo.ip2regionV6",
            Kind = ExternalResourceKind.DataFile, Provider = ExternalResourceProviderKind.HttpFile,
            LocalPath = @"bin\ip2region_v6.xdb",
            // 不设备胎：jsDelivr 系超限出不了 37MB，直连失败时由「外部资源经内置代理下载」接管
            Url = "https://raw.githubusercontent.com/lionsoul2014/ip2region/master/data/ip2region_v6.xdb",
            // 不提供禁用（归属地功能本体内建）；不提供 CanDisable
            Caps = ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
            AutoUpdateDefault = true, MinBytes = 20 * 1024 * 1024
        }
    ];

    /// <summary>内置注册项</summary>
    public static IReadOnlyList<ExternalResource> BuiltIn => BuiltInResources;

    /// <summary>全部资源 = 内置项 + 用户自定义规则集动态项（ruleset.custom.&lt;tag&gt;）</summary>
    public static IEnumerable<ExternalResource> All
    {
        get
        {
            foreach (var r in BuiltInResources)
                yield return r;

            foreach (var rs in Global.Settings.RuleSets.Where(rs => rs.Type == RuleSetKind.Remote)) // RE-1§2.5：Local 集无下载语义，不进外部资源
            {
                if (BuiltInResources.Any(b => b.LocalPath == CustomRuleSetLocalPath(rs.Tag)))
                    continue;

                yield return new ExternalResource
                {
                    Key = CustomRuleSetKey(rs.Tag),
                    DisplayNameKey = "external.ruleset.custom",
                    Kind = ExternalResourceKind.RuleSet,
                    Provider = ExternalResourceProviderKind.HttpFile,
                    LocalPath = CustomRuleSetLocalPath(rs.Tag),
                    Url = rs.Url,
                    Caps = ExternalResourceCaps.CanDisable | ExternalResourceCaps.CanDelete | ExternalResourceCaps.CanUpdate | ExternalResourceCaps.CanAutoUpdate,
                    // 自定义规则集默认关（跟随用户）
                    AutoUpdateDefault = false,
                    MinBytes = 1024
                };
            }
        }
    }

    public static ExternalResource? Find(string key)
    {
        return All.FirstOrDefault(r => r.Key == key);
    }

    public static string CustomRuleSetKey(string tag)
    {
        return $"ruleset.custom.{tag}";
    }

    /// <summary>用户自定义规则集的本地缓存快照（initial_path 兜底，下载成功后写）</summary>
    public static string CustomRuleSetLocalPath(string tag)
    {
        return $@"bin\rule-set\{tag}.srs";
    }

    #endregion

    #region 状态（settings.json ExternalResources.Items；缺省不落盘，首次改动才写入）

    private static ExternalResourceSettings Store => Global.Settings.ExternalResources;

    /// <summary>资源当前状态：无落盘条目时返回临时默认态（不写入，保持 settings.json 干净）</summary>
    public static ExternalResourceState GetState(ExternalResource resource)
    {
        var item = Store.Items.FirstOrDefault(i => i.Key == resource.Key);
        if (item != null)
            return item;

        return new ExternalResourceState { Key = resource.Key };
    }

    /// <summary>变更并持久化资源状态（条目不存在则创建，即「首次改动才写入」）</summary>
    public static async Task MutateStateAsync(ExternalResource resource, Action<ExternalResourceState> mutate)
    {
        var item = Store.Items.FirstOrDefault(i => i.Key == resource.Key);
        if (item == null)
        {
            item = new ExternalResourceState { Key = resource.Key };
            Store.Items.Add(item);
        }

        mutate(item);
        await Configuration.SaveAsync();
    }

    /// <summary>资源的有效禁用态（未设置 = false）</summary>
    public static bool IsDisabled(ExternalResource resource)
    {
        return GetState(resource).Disabled;
    }

    /// <summary>资源的有效自动更新态：用户显式设置优先，否则注册表默认</summary>
    public static bool IsAutoUpdateOn(ExternalResource resource)
    {
        if (!resource.Caps.HasFlag(ExternalResourceCaps.CanAutoUpdate))
            return false;

        return GetState(resource).AutoUpdate ?? resource.AutoUpdateDefault;
    }

    #endregion

    #region 校验（魔数/可解析表，R 线 §4.3 定稿）

    /// <summary>
    ///     下载产物校验：大小阈值 + 格式魔数 + 可解析性。任一不过抛 MessageException（失败原因如实上抛）。
    /// </summary>
    public static void ValidateDownloaded(ExternalResource resource, string filePath)
    {
        var size = new FileInfo(filePath).Length;
        if (size < resource.MinBytes)
            throw new MessageException(I18N.TF("external.error.tooSmall", size, resource.MinBytes));

        switch (resource.Kind)
        {
            case ExternalResourceKind.Kernel:
                ValidateMagic(filePath, "MZ", "external.error.badKernel");
                break;

            case ExternalResourceKind.RuleSet:
                // .srs：头三字节 "SRS" + 版本字节（v1 raw / v2 zstd）；库内快照实测头 = 53 52 53 01
                ValidateSrs(filePath);
                break;

            case ExternalResourceKind.DataFile:
                // 归属地库只有 xdb 一族（mmdb 已退役）；v6 槽位强校验 ipVersion=6，防两库互相错装
                ValidateXdb(filePath, isV6: filePath.EndsWith("_v6.xdb", StringComparison.OrdinalIgnoreCase));
                break;

            case ExternalResourceKind.TextList:
                // 清单类不做网络管线（恢复默认走内嵌副本）；防御性 UTF-8 可读校验
                try
                {
                    File.ReadAllText(filePath);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Text list validation failed");
                    throw new MessageException(I18N.T("external.error.badTextList"));
                }

                break;
        }
    }

    private static void ValidateMagic(string filePath, string magic, string errorKey)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[2];
        if (fs.Read(header) != 2 || System.Text.Encoding.ASCII.GetString(header) != magic)
            throw new MessageException(I18N.T(errorKey));
    }

    private static void ValidateSrs(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[4];
        if (fs.Read(header) != 4)
            throw new MessageException(I18N.T("external.error.badSrs"));

        if (header[0] != 0x53 || header[1] != 0x52 || header[2] != 0x53 || header[3] is not (1 or 2))
            throw new MessageException(I18N.T("external.error.badSrs"));
    }

    /// <summary>xdb 结构校验：版本 + vIndex 边界自洽（Ip2Region.IsHeaderValid 同款判定，加载时同检不绕过；v6 槽位加验 ipVersion）</summary>
    private static void ValidateXdb(string filePath, bool isV6)
    {
        try
        {
            if (!Ip2Region.IsHeaderValid(File.ReadAllBytes(filePath), isV6))
                throw new InvalidOperationException("xdb header invalid");
        }
        catch (Exception e)
        {
            Log.Error(e, "Xdb validation failed");
            throw new MessageException(I18N.T("external.error.badXdb"));
        }
    }

    #endregion

    #region HttpFileProvider 下载管线（R 线 §4.2/§4.3）

    /// <summary>HEAD 指纹：ETag / Last-Modified / Content-Length 合成串（三者任一变化即「可更新」）</summary>
    public static string ComposeFingerprint(string? etag, string? lastModified, long? contentLength)
    {
        return $"{etag ?? "-"}|{lastModified ?? "-"}|{contentLength?.ToString() ?? "-"}";
    }

    /// <summary>
    ///     HEAD 探测远端指纹（网络失败上抛；GitHub release 静态资产 302 后落到 CDN 的 ETag 为对象指纹，稳定可比）。
    ///     全部头缺失返回 null（无法判定，按可更新处理由调用方决定）。
    /// </summary>
    public static async Task<string?> ProbeFingerprintAsync(string url)
    {
        var req = WebUtil.CreateResourceRequest(url);
        var (etag, lastModified, length) = await WebUtil.ProbeHeadersAsync(req);
        var hasAny = etag != null || lastModified != null || length != null;
        return hasAny ? ComposeFingerprint(etag, lastModified, length) : null;
    }

    /// <summary>文件资源一次更新的结果</summary>
    public enum ResourceUpdateOutcome
    {
        /// <summary>远端指纹与本地一致（且本地文件在），未下载</summary>
        UpToDate,

        /// <summary>已下载并替换</summary>
        Updated
    }

    /// <summary>
    ///     文件类资源一次完整更新：探测 → 指纹未变短路 → 下载到 %TEMP%（主源失败自动切备胎）→ 校验 →
    ///     原子替换 → 状态回写。任一步失败旧文件原样保留；备胎命中时 viaMirror=true（徽章如实显示「经镜像更新」）。
    ///     调用方持 BusyLock。
    /// </summary>
    public static async Task<(ResourceUpdateOutcome outcome, string usedUrl, bool viaMirror)> UpdateHttpFileAsync(
        ExternalResource resource, IProgress<int>? progress = null)
    {
        // 指纹短路：远端未变且本地文件在 → 不重复下载（立即全部更新对 8+ 资源逐个 HEAD 的成本可接受）
        string? probe = null;
        try
        {
            probe = await ProbeFingerprintAsync(resource.Url!);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Probe fingerprint failed for {Url}", resource.Url);
        }

        if (probe != null && Exists(resource) && GetState(resource).LastETag == probe)
        {
            await MutateStateAsync(resource, s => s.LastCheckAt = DateTime.Now);
            return (ResourceUpdateOutcome.UpToDate, "", false);
        }

        // tmp 落目标同目录（F-3，2026-11-04 审查）：%TEMP% 与安装目录跨卷时 File.Replace 必抛 IOException，
        // 非系统盘安装用户点更新 100% 失败——白下几十 MB 最后一步替换不了；与 Configuration 的 tmp 同目录做法对齐
        var targetPath = Path.Combine(Global.NetchDir, resource.LocalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempFile = targetPath + ".tmp";
        try
        {
            string usedUrl;
            string fingerprint;
            try
            {
                fingerprint = await DownloadValidatedAsync(resource, resource.Url!, tempFile, progress, probe);
                usedUrl = resource.Url!;
            }
            catch (Exception) when (!string.IsNullOrEmpty(resource.FallbackUrl) && Global.Settings.ExternalResources.RuleSetFallbackMirror)
            {
                // 备胎切换仅覆盖网络类失败与校验失败（主源给出的产物不可用）；切换结果由返回值如实暴露
                progress?.Report(0);
                fingerprint = await DownloadValidatedAsync(resource, resource.FallbackUrl!, tempFile, progress, null);
                usedUrl = resource.FallbackUrl!;
            }

            var viaMirror = usedUrl != resource.Url;
            InstallFile(resource, tempFile);
            await MutateStateAsync(resource, s =>
            {
                s.LastUpdateAt = DateTime.Now;
                s.LastCheckAt = DateTime.Now;
                s.LastETag = fingerprint;
            });

            return (ResourceUpdateOutcome.Updated, usedUrl, viaMirror);
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    /// <returns>下载前探测到的远端指纹（probeOverride 非 null 时复用免二次探测；探测失败为 null，不阻塞下载）</returns>
    private static async Task<string> DownloadValidatedAsync(ExternalResource resource, string url, string tempFile,
        IProgress<int>? progress, string? probeOverride)
    {
        string fingerprint;
        if (probeOverride != null)
        {
            fingerprint = probeOverride;
        }
        else
        {
            try
            {
                fingerprint = await ProbeFingerprintAsync(url) ?? "";
            }
            catch (Exception e)
            {
                Log.Warning(e, "Probe fingerprint failed for {Url}", url);
                fingerprint = "";
            }
        }

        var req = WebUtil.CreateResourceRequest(url, timeout: 120_000);
        await WebUtil.DownloadFileAsync(req, tempFile, progress);
        ValidateDownloaded(resource, tempFile);
        return fingerprint;
    }

    /// <summary>原子替换：tmp 与 backup 都在目标同目录（File.Replace 同卷约束）；目标缺失时退化为 Move（首次安装）</summary>
    private static void InstallFile(ExternalResource resource, string tempFile)
    {
        var target = Path.Combine(Global.NetchDir, resource.LocalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (!File.Exists(target))
        {
            File.Move(tempFile, target);
            return;
        }

        var backup = target + ".old";
        File.Replace(tempFile, target, backup);
        TryDelete(backup);
    }

    #endregion

    #region 删除（.trash 隔离；R 线 §4.4：删除一律先确认（UI 层），应用内移入 .trash 可恢复）

    /// <summary>资源本地文件全路径</summary>
    public static string GetFullPath(ExternalResource resource)
    {
        return Path.Combine(Global.NetchDir, resource.LocalPath);
    }

    public static bool Exists(ExternalResource resource)
    {
        return File.Exists(GetFullPath(resource));
    }

    /// <summary>删除：移入安装目录 .trash\（同名冲突加序号），返回隔离区内的落点文件名</summary>
    public static string MoveToTrash(ExternalResource resource)
    {
        var source = GetFullPath(resource);
        if (!File.Exists(source))
            throw new MessageException(I18N.T("external.error.notInstalled"));

        var trashDir = Path.Combine(Global.NetchDir, TrashDirName);
        Directory.CreateDirectory(trashDir);

        var fileName = Path.GetFileName(source);
        var target = Path.Combine(trashDir, fileName);
        for (var i = 1; File.Exists(target); i++)
            target = Path.Combine(trashDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{i}{Path.GetExtension(fileName)}");

        File.Move(source, target);

        // 快照缺失语义如实同步：禁用状态与更新时间不再有意义，清掉避免误导（未设置过的资源不落盘）
        var state = Store.Items.FirstOrDefault(i => i.Key == resource.Key);
        if (state != null)
            state.LastUpdateAt = null;

        return Path.GetFileName(target);
    }

    /// <summary>隔次启动清理：上次会话移入 .trash 的文件本次启动删除（撤销窗口 = 一个会话生命周期）</summary>
    public static void CleanTrashOnStartup()
    {
        try
        {
            var trashDir = Path.Combine(Global.NetchDir, TrashDirName);
            if (!Directory.Exists(trashDir))
                return;

            foreach (var file in Directory.EnumerateFiles(trashDir))
                TryDelete(file);

            if (!Directory.EnumerateFileSystemEntries(trashDir).Any())
                Directory.Delete(trashDir);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Clean external resource trash failed");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Delete temp file failed: {File}", path);
        }
    }

    #endregion
}
