using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace Netch.Views;

/// <summary>设置页（05§A1）</summary>
public partial class SettingsView : UserControl
{
    private bool _stunWiring;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += SettingsView_DataContextChanged;
    }

    /// <summary>STUN 可编辑下拉的显示与写回走显式控制：模板补 PART_EditableTextBox（2026-10-29）之前
    /// Text 无处渲染、绑定写进 Text DP 后又被内部清空且不反向重拉（b1o-b1r 三次实机复现），
    /// 根因虽除，绑定回显时序仍不如显式回写直观，故保留此方案。</summary>
    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not ViewModels.SettingsViewModel vm)
            return;

        // ApplicationIdle 是刻意时序：等 ComboBox 模板填充完成后回显初值（Loaded 更早会再被清一次）
#pragma warning disable VSTHRD001 // 需要指定优先级的 Dispatcher 派发，非线程切换场景
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (DataContext != vm || stunCombo == null)
                return;

            if (!_stunWiring)
            {
                _stunWiring = true;
                stunCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                    new TextChangedEventHandler((_, _) => vm.STUNServer = stunCombo.Text));
                // 写回优先取 SelectedItem：选中语义不依赖内部 Text 同步时序，手输场景无 SelectedItem 落回 Text
                stunCombo.SelectionChanged += (_, _) =>
                    vm.STUNServer = stunCombo.SelectedItem as string ?? stunCombo.Text;
            }

            stunCombo.Text = vm.STUNServer;
        });
#pragma warning restore VSTHRD001
    }
}
