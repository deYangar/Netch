namespace Netch.Models;

/// <summary>资源类别（R 线 §4.1）</summary>
public enum ExternalResourceKind
{
    /// <summary>内核可执行文件（bin\*.exe，GitHub Release 通道）</summary>
    Kernel,

    /// <summary>sing-box 规则集快照（bin\rule-set\*.srs）</summary>
    RuleSet,

    /// <summary>数据文件（IP 归属地库等）</summary>
    DataFile,

    /// <summary>文本清单（STUN 服务器列表，仅恢复默认，无网络通道）</summary>
    TextList
}

/// <summary>更新源类型（R 线 §4.2）</summary>
public enum ExternalResourceProviderKind
{
    /// <summary>无更新通道（自建改造版内核 / 随主程序发行 / 仅恢复默认的清单）</summary>
    None,

    /// <summary>GitHub Release 通道（内核 zip 包，复用 CoreUpdater 校验替换流程）</summary>
    GitHubRelease,

    /// <summary>静态文件直链（ip2region xdb / .srs 快照；HEAD 指纹探测 + 魔数校验 + 原子替换）</summary>
    HttpFile
}

/// <summary>资源能力位（R 线 §4.1，具体矩阵 §4.4 定死）</summary>
[Flags]
public enum ExternalResourceCaps
{
    None = 0,

    /// <summary>可禁用（内核=模式选择器置灰；规则集=配置生成跳过该 tag）</summary>
    CanDisable = 1,

    /// <summary>可删除（移入安装目录 .trash\，隔次启动清理，可恢复）</summary>
    CanDelete = 2,

    /// <summary>可手动立即更新</summary>
    CanUpdate = 4,

    /// <summary>可挂自动更新调度</summary>
    CanAutoUpdate = 8
}

/// <summary>
///     外部资源定义（R 线 §4.1）。内置项静态注册在 ExternalResourceService，用户自定义规则集动态合成。
/// </summary>
public sealed class ExternalResource
{
    /// <summary>全局唯一键："kernel.singbox" / "ruleset.geosite-cn" / "geo.ip2region" / "stun.list" / "ruleset.custom.&lt;tag&gt;"</summary>
    public required string Key { get; init; }

    /// <summary>显示名 i18n 键（external.* 词条）</summary>
    public required string DisplayNameKey { get; init; }

    public ExternalResourceKind Kind { get; init; }

    public ExternalResourceProviderKind Provider { get; init; }

    /// <summary>相对安装目录的本地文件路径（如 bin\sing-box.exe）；只读展示项同样提供</summary>
    public required string LocalPath { get; init; }

    /// <summary>HttpFileProvider 下载直链</summary>
    public string? Url { get; init; }

    /// <summary>备胎镜像直链（主源超时/404/校验失败自动切换，徽章如实显示「经镜像更新」）</summary>
    public string? FallbackUrl { get; init; }

    public ExternalResourceCaps Caps { get; init; }

    /// <summary>自动更新默认态（AutoUpdateDefault 关的多为内核：1.x 偶有 breaking change，默认只提醒不自动换）</summary>
    public bool AutoUpdateDefault { get; init; }

    /// <summary>HttpFileProvider 下载校验的最小合法大小（字节）；截断/HTML 错误体在此拒收</summary>
    public long MinBytes { get; init; }
}
