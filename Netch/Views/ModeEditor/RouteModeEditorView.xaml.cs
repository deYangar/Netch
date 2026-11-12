using System.Windows;
using Netch.Models.Modes;
using Netch.Models.Modes.TunMode;
using Netch.ViewModels;

namespace Netch.Views.ModeEditor;

/// <summary>路由模式编辑器（07§B）：纯绑定窗口，保存即关窗</summary>
public partial class RouteModeEditorView
{
    private readonly RouteModeEditorViewModel _vm;

    /// <summary>出站选项（平移旧 RouteRuleForm 的 Proxy/Direct 选择）</summary>
    public object[] ActionTypes { get; } = { RouteRuleAction.Proxy, RouteRuleAction.Direct };

    private RouteModeEditorView(RouteModeEditorViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        saveButton.Click += (_, _) =>
        {
            _vm.SaveCommand.Execute(null);
            if (_vm.Saved)
                Close();
        };
        cancelButton.Click += (_, _) => Close();
    }

    public static RouteModeEditorView CreateNew() => new(new RouteModeEditorViewModel(null));

    public static RouteModeEditorView Edit(TunMode mode) => new(new RouteModeEditorViewModel(mode));
}
