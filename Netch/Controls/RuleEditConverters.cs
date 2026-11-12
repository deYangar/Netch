using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Netch.ViewModels;

namespace Netch.Controls;

/// <summary>规则编辑方式的单选按钮与面板显隐（RE-3§4.1）</summary>
public sealed class RuleEditModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var matches = value is RuleEditMode mode && mode.ToString() == parameter as string;
        return targetType == typeof(Visibility) ? (matches ? Visibility.Visible : Visibility.Collapsed) : matches;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse<RuleEditMode>((string)parameter) : Binding.DoNothing;
}

public sealed class DisabledOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 0.55 : 1.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
