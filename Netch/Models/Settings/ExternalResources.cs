namespace Netch.Models;

/// <summary>
///     外部资源统一管理（R 线 §4.1）：自动更新调度设置 + 每资源独立状态。
///     缺省条目不落盘（Items 空列表 = 全部资源处于注册表默认态），首次改动才写入，保持 settings.json 干净。
/// </summary>
public class ExternalResourceSettings
{
    /// <summary>
    ///     外部资源统一经内置代理下载（内核 mixed 入站运行时生效；未运行直连）。
    ///     覆盖全部下载路径：规则集/数据文件管线、内核 release 查询与包下载、主程序更新检查、内核运行期规则集下载出站
    /// </summary>
    public bool ViaProxy { get; set; } = true;

    /// <summary>全局自动更新总开关</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>自动更新检查间隔（小时）</summary>
    public int AutoUpdateIntervalHours { get; set; } = 24;

    /// <summary>每资源状态（Key → 条目；仅显式改过/更新过的资源有条目）</summary>
    public List<ExternalResourceState> Items { get; set; } = new();

    /// <summary>内置规则集 MetaCubeX 备胎镜像开关（主源超时/404/校验失败时自动切换）</summary>
    public bool RuleSetFallbackMirror { get; set; } = true;
}

/// <summary>
///     单资源状态。AutoUpdate 为三态：null=跟随注册表默认（条目缺省即此态，不落盘），
///     非 null=用户显式开关（全局 JsonIgnoreCondition.WhenWritingNull 使 null 不落盘）
/// </summary>
public class ExternalResourceState
{
    public string Key { get; set; } = "";

    /// <summary>禁用（语义按资源能力矩阵 §4.4：内核=模式选择器置灰，规则集=配置生成跳过该 tag）</summary>
    public bool Disabled { get; set; }

    /// <summary>自动更新独立开关；null = 跟随注册表默认</summary>
    public bool? AutoUpdate { get; set; }

    public DateTime? LastUpdateAt { get; set; }

    public DateTime? LastCheckAt { get; set; }

    /// <summary>HttpFileProvider 指纹（ETag / Last-Modified / Content-Length 合成串）；内核通道不使用</summary>
    public string? LastETag { get; set; }
}
