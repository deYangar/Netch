using System.Windows;
using Netch.Models.Modes.ProcessMode;
using Netch.ViewModels;

namespace Netch.Views.ModeEditor;

/// <summary>进程模式编辑器（07§A）：纯绑定窗口，保存即关窗</summary>
public partial class ProcessModeEditorView
{
    private readonly ProcessModeEditorViewModel _vm;

    private ProcessModeEditorView(ProcessModeEditorViewModel vm)
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

    public static ProcessModeEditorView CreateNew() => new(new ProcessModeEditorViewModel(null));

    public static ProcessModeEditorView Edit(Redirector mode) => new(new ProcessModeEditorViewModel(mode));
}
