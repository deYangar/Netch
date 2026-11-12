using System.Windows;
using System.Windows.Controls;

namespace Netch.Controls.DataForm;

/// <summary>
///     PasswordBox.Password 非 DP，经附加属性双向桥接到 FieldRow.Value（模板内绑定用）。
/// </summary>
public static class PasswordBinder
{
    /// <summary>
    ///     默认值哨兵：PasswordChanged 的挂接依赖 OnBoundValueChanged 回调，而 DP 初值与绑定初值相等时回调不触发。
    ///     新建表单绑定初值恰为空串（与旧默认值相等），事件将永不挂接、打字永不写回——
    ///     哨兵保证绑定激活（含初值为空）时回调必然执行一次。
    /// </summary>
    private const string UnboundSentinel = "\0unbound";

    public static readonly DependencyProperty BoundValueProperty = DependencyProperty.RegisterAttached(
        "BoundValue", typeof(string), typeof(PasswordBinder),
        new FrameworkPropertyMetadata(UnboundSentinel, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundValueChanged));

    private static readonly HashSet<PasswordBox> Updating = new();

    public static string GetBoundValue(DependencyObject obj) => (string)obj.GetValue(BoundValueProperty);

    public static void SetBoundValue(DependencyObject obj, string value) => obj.SetValue(BoundValueProperty, value);

    private static void OnBoundValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;

        box.PasswordChanged -= OnPasswordChanged;
        var newValue = (string?)e.NewValue ?? "";
        if (!Updating.Contains(box) && box.Password != newValue)
        {
            Updating.Add(box);
            box.Password = newValue;
            Updating.Remove(box);
        }

        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || Updating.Contains(box))
            return;

        Updating.Add(box);
        SetBoundValue(box, box.Password);
        Updating.Remove(box);
    }
}
