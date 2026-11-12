using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Controls;
using Netch.UI.Hub;
using UserControl = System.Windows.Controls.UserControl;

namespace Netch.Views;

/// <summary>
///     日志页（07§D）：Redirector 日志行（UIEvents.RedirectorLog），环形 5000（批量不清 UI）。
///     页面常驻（VM 静态集合，导航切换不丢）。
/// </summary>
public partial class LogView : UserControl
{
    private const int Capacity = 5000;

    /// <summary>全应用共享的环形行集（页面实例化重建也不丢——静态）</summary>
    private static readonly ObservableCollection<string> Lines = new();

    static LogView()
    {
        UIEvents.RedirectorLog += line =>
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted)
                return;

            void Add()
            {
                Lines.Add(line);
                while (Lines.Count > Capacity)
                    Lines.RemoveAt(0);
            }

            if (app.Dispatcher.CheckAccess())
                Add();
            else
// 静态事件回调 fire-and-forget 投递（无可等待的异步上下文，JTF 切换不适用）
#pragma warning disable VSTHRD001
                _ = app.Dispatcher.InvokeAsync(Add, System.Windows.Threading.DispatcherPriority.Normal);
#pragma warning restore VSTHRD001
        };
    }

    public LogView()
    {
        InitializeComponent();
        logList.ItemsSource = Lines;
    }
}
