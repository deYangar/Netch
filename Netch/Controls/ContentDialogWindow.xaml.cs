using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Netch.UI.Hub;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Controls;

/// <summary>
///     Token 化 ContentDialog（02§4：替代 MessageBox；Info=ℹ Warning=⚠ Error=⛔ 圆叉——修正旧 Exclamation 错配）。
///     实现：无边框模态 owned window（WPF-UI 的 ContentDialog 需要 Page 级 presenter，此处以同视觉规格自绘）。
///     主按钮 Primary，次按钮默认；ESC=取消。05 i18n 键制接入后文案换 key。
/// </summary>
public partial class ContentDialogWindow : Window
{
    private bool _result;

    private ContentDialogWindow(Window? owner, string text, string title, DialogLevel level, bool confirm)
    {
        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        MaxHeight = 520;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        if (owner != null)
            Owner = owner;

        TitleText.Text = title;
        BodyText.Text = text;

        var (glyph, colorKey) = level switch
        {
            DialogLevel.Warning => ("⚠", "Brush.Warning"),
            DialogLevel.Error => ("⛔", "Brush.Danger"),
            _ => ("ℹ", "Brush.Accent")
        };

        LevelIcon.Text = glyph;
        LevelIcon.SetResourceReference(TextBlock.ForegroundProperty, colorKey);

        PrimaryButton.Content = confirm ? I18N.T("common.ok") : I18N.T("common.ok");
        PrimaryButton.Style = (Style)FindResource("Btn.Primary");
        PrimaryButton.Click += (_, _) =>
        {
            _result = true;
            Close();
        };

        SecondaryButton.Content = I18N.T("common.cancel");
        SecondaryButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Click += (_, _) => Close();
    }

    /// <summary>ESC=取消</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>标题区拖拽（无边框窗口移动句柄）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    public static void Alert(Window? owner, string text, DialogLevel level = DialogLevel.Info, string? title = null)
    {
        ShowDialogCore(owner, text, level, title, confirm: false);
    }

    public static bool Confirm(Window? owner, string text, string? title = null)
    {
        return ShowDialogCore(owner, text, DialogLevel.Warning, title, confirm: true);
    }

    private static bool ShowDialogCore(Window? owner, string text, DialogLevel level, string? title, bool confirm)
    {
        var fallbackTitle = level switch
        {
            DialogLevel.Warning => I18N.T("common.warn"),
            DialogLevel.Error => I18N.T("common.error"),
            _ => I18N.T("common.info")
        };

        var dialog = new ContentDialogWindow(owner, text, string.IsNullOrEmpty(title) ? fallbackTitle : title, level, confirm);

        // 无主窗（启动早期/托盘路径）：居屏幕弹出
        if (dialog.Owner == null)
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._result;
    }
}
