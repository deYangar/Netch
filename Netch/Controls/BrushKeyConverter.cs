using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Netch.Controls;

/// <summary>编辑模型声明 Token 键，视图统一解析为主题 Brush。</summary>
public sealed class BrushKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && Application.Current.TryFindResource(key) is { } brush ? brush : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
