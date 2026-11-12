using System.Windows;
using System.Windows.Controls;

namespace Netch.Controls;

/// <summary>
///     三态复选框：鼠标点击循环 勾选 → 不勾选 → 不确定（跟随全局）。
///     WPF 原生 CheckBox 即便 IsThreeState=true，点击循环也不进入 Indeterminate
///     （第三态只能由数据/代码进入）——进程模式 Handle 组需要鼠标可达的第三态。
/// </summary>
public class TriStateCheckBox : CheckBox
{
    static TriStateCheckBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TriStateCheckBox), new FrameworkPropertyMetadata(typeof(CheckBox)));
    }

    protected override void OnClick()
    {
        if (!IsThreeState)
        {
            base.OnClick();
            return;
        }

        // 手动三态循环并补发基类 OnClick 的连带（Toggle 事件/命令），绑定走 IsChecked DP 变化
        IsChecked = IsChecked switch
        {
            true => false,
            false => null,
            null => true
        };

        switch (IsChecked)
        {
            case true:
                RaiseEvent(new RoutedEventArgs(CheckedEvent));
                break;
            case false:
                RaiseEvent(new RoutedEventArgs(UncheckedEvent));
                break;
            default:
                RaiseEvent(new RoutedEventArgs(IndeterminateEvent));
                break;
        }

        RaiseEvent(new RoutedEventArgs(ClickEvent));
        if (Command is System.Windows.Input.ICommand cmd && cmd.CanExecute(CommandParameter))
            cmd.Execute(CommandParameter);
    }
}
