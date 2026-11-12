namespace Netch.Models;

public class KernelConfig
{
    /// <summary>
    ///     内核路由已全自动（2026-10-31 咩咩拍板去手选）：按节点协议自动路由 sing-box / Xray 兜底。
    ///     字段保留仅供存量 settings.json 迁移识别（见 Configuration.CheckSetting），运行期恒为 Auto。
    /// </summary>
    public string Core { get; set; } = KernelGlobal.Auto;

    /// <summary>
    ///     分组（同 Group 多节点）启动时由 urltest 自动选择延迟最低的节点；
    ///     关闭时 selector 固定选中启动时选择的节点（primary）
    /// </summary>
    public bool GroupAutoSelect { get; set; }
}

public static class KernelGlobal
{
    public const string Auto = "Auto";
    public const string SingBox = "SingBox"; // 仅存量迁移识别用
    public const string V2raySN = "V2raySN"; // 仅存量迁移识别用
}
