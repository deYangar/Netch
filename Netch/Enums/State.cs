namespace Netch.Enums;

/// <summary>
///     状态
/// </summary>
public enum State
{
    /// <summary>
    ///     等待命令中
    /// </summary>
    Waiting,

    /// <summary>
    ///     正在启动中
    /// </summary>
    Starting,

    /// <summary>
    ///     已启动
    /// </summary>
    Started,

    /// <summary>
    ///     正在停止中
    /// </summary>
    Stopping,

    /// <summary>
    ///     已停止
    /// </summary>
    Stopped,

    /// <summary>
    ///     等待网络（断网有序暂停，恢复后自动重建；不是「已停止」）
    /// </summary>
    Degraded,

    /// <summary>
    ///     退出中
    /// </summary>
    Terminating
}

public static class StateExtension
{
    /// <summary>状态文案 i18n key（05 键制：原运行时英文查表机制退役）</summary>
    public static string GetStatusKey(State state)
    {
        return state switch
        {
            State.Waiting => "main.state.waiting",
            State.Starting => "main.state.starting",
            State.Started => "main.state.started",
            State.Stopping => "main.state.stopping",
            State.Stopped => "main.state.stopped",
            State.Degraded => "main.state.degraded",
            _ => "main.state.waiting"
        };
    }
}
