using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Netch.Enums;

namespace Netch.Controls;

/// <summary>
///     State → Visibility：给定的状态集合内可见（托盘菜单启动/停止项按 State 显隐）。
/// </summary>
public class StateVisibilityConverter : IValueConverter
{
    private readonly State[] _visibleStates;

    public StateVisibilityConverter(params State[] visibleStates)
    {
        _visibleStates = visibleStates;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is State state && _visibleStates.Contains(state) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
