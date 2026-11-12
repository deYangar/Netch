using Netch.Enums;

namespace Netch.UI.Hub;

/// <summary>
///     业务层访问主界面/托盘/对话框的唯一通道。实现必须线程安全：任意线程可调，内部调度到 UI 线程。
/// </summary>
public interface IMainUIHub
{
    /// <summary>状态栏主文本。null = 按 State 枚举恢复默认文案（对应旧 MainForm.StatusText()）</summary>
    void StatusText(string? text);

    /// <summary>托盘气泡通知（对应旧 MainForm.NotifyTip）</summary>
    void NotifyTip(string text, int timeout = 0, bool info = true);

    /// <summary>在 UI 线程同步执行</summary>
    void Invoke(Action action);

    /// <summary>在 UI 线程异步执行</summary>
    void BeginInvoke(Action action);

    /// <summary>显示并激活主窗口（对应 Constants.Parameter.Show 二次启动唤起）</summary>
    void ShowMainForm();

    /// <summary>模态询问，返回 true=确认</summary>
    Task<bool> ConfirmAsync(string text, string? title = null);

    /// <summary>信息/警告/错误提示（错误级别图标必须是 Error，修正旧版配错）</summary>
    void Alert(string text, DialogLevel level = DialogLevel.Info, string? title = null);

    /// <summary>主窗当前状态（Waiting/Starting/Started/Stopping），供后台组件判断</summary>
    State State { get; }
}

public enum DialogLevel
{
    Info,
    Warning,
    Error
}
