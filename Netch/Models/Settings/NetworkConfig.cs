namespace Netch.Models;

/// <summary>
///     可靠性设置（M3-2）：断网暂停/恢复重建、内核进程守护
/// </summary>
public class NetworkConfig
{
    /// <summary>
    ///     断网时有序暂停，恢复后自动重建（默认开）
    /// </summary>
    public bool AutoRestore { get; set; } = true;

    /// <summary>
    ///     内核进程意外退出时自动重启（默认开）
    /// </summary>
    public bool KernelAutoRestart { get; set; } = true;

    /// <summary>
    ///     网络事件防抖窗口（秒），窗口内的连续事件合并为一次判定
    /// </summary>
    public int DebounceSeconds { get; set; } = 3;
}
