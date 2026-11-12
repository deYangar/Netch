using Netch.Controllers;
using Netch.Enums;

namespace Netch.UI.Hub;

/// <summary>
///     UI 感兴趣的业务事件。业务层只发事件，不持 UI 引用；订阅方（WPF VM / 过渡期旧窗体）自行调度到 UI 线程。
/// </summary>
public static class UIEvents
{
    /// <summary>内核意外退出重启（MainController.RestartCoreAsync 触发），参数 (原因, 次数/上限)</summary>
    public static event Action<string, int, int>? KernelRestarting;

    /// <summary>Pcap/Redirector 日志行输出（PcapController 现直接写 LogForm）</summary>
    public static event Action<string>? RedirectorLog;

    /// <summary>发现新版本（UpdateChecker），参数 (版本号, 下载页)</summary>
    public static event Action<string, string>? NewVersionFound;

    /// <summary>主窗状态变化（Waiting/Starting/Started/Stopping）。hub 落地时同时触发本事件</summary>
    public static event Action<State>? StateChanged;

    /// <summary>服务器集合变更（订阅更新/导入/增删后），主页与订阅页刷新列表用</summary>
    public static event Action? ServersChanged;

    /// <summary>模式集合变更（ModeService Load/Add/Sort/Delete 后），主页模式列表刷新用</summary>
    public static event Action? ModesChanged;

    /// <summary>内核进程意外退出（Guard.Exited，任意线程），订阅方按退避策略编排重启</summary>
    public static event Action<Guard>? KernelExited;

    /// <summary>语言切换（设置页即时生效）：主窗标题/动态文本重取</summary>
    public static event Action? LanguageChanged;

    /// <summary>Profile 槽位数变化（设置页改 ProfileCount）：主页 Profile 卡片区重建</summary>
    public static event Action? ProfileCountChanged;

    #region 过渡期流量推送（旧 WinForms 状态栏依赖；WPF 状态栏改轮询静态字段后由 S8 删除）

    /// <summary>Clash API 按出站分账的流量读数推送（参数同旧 MainForm.OnClashTrafficUpdated）</summary>
    public static event Action<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>? TrafficUpdated;

    /// <summary>ETW 回退路径的总流量读数推送（参数同旧 MainForm.OnBandwidthUpdated）</summary>
    public static event Action<ulong>? EtwTrafficUpdated;

    /// <summary>流量统计源启动。etwFallback=true 时旧主窗隐藏直连流速列（ETW 不分上下行）</summary>
    public static event Action<bool>? TrafficSourceStarted;

    #endregion

    public static void RaiseKernelRestarting(string reason, int count, int limit) => KernelRestarting?.Invoke(reason, count, limit);

    public static void RaiseRedirectorLog(string line) => RedirectorLog?.Invoke(line);

    public static void RaiseNewVersionFound(string version, string url) => NewVersionFound?.Invoke(version, url);

    public static void RaiseStateChanged(State state) => StateChanged?.Invoke(state);

    public static void RaiseServersChanged() => ServersChanged?.Invoke();

    public static void RaiseModesChanged() => ModesChanged?.Invoke();

    public static void RaiseKernelExited(Guard guard) => KernelExited?.Invoke(guard);

    public static void RaiseLanguageChanged() => LanguageChanged?.Invoke();

    public static void RaiseProfileCountChanged() => ProfileCountChanged?.Invoke();

    public static void RaiseTrafficUpdated(ulong pUp, ulong pDown, ulong dUp, ulong dDown,
        ulong pUpTotal, ulong pDownTotal, ulong dUpTotal, ulong dDownTotal) =>
        TrafficUpdated?.Invoke(pUp, pDown, dUp, dDown, pUpTotal, pDownTotal, dUpTotal, dDownTotal);

    public static void RaiseEtwTrafficUpdated(ulong total) => EtwTrafficUpdated?.Invoke(total);

    public static void RaiseTrafficSourceStarted(bool etwFallback) => TrafficSourceStarted?.Invoke(etwFallback);
}
