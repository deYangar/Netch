using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Netch.Enums;

namespace Netch.Controls;

/// <summary>
///     延迟徽章（02§2 档位映射的治本替换）：
///     &lt;0 未测=TextSecondary 无底；0-80=Success；81-200=Warning；&gt;200=Danger。
///     实色 = Clash API 实测全链路延迟（DelaySource.ClashApi），Soft = TCPing/ICMP 握手延迟（DelaySource.TCPing）
///     ——沿用旧 ComboBox_DrawItem 的实/浅语义。
/// </summary>
public class DelayBadge : System.Windows.Controls.Control
{
    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(
        nameof(Delay), typeof(int), typeof(DelayBadge),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualStateChanged));

    public static readonly DependencyProperty DelaySourceProperty = DependencyProperty.Register(
        nameof(DelaySource), typeof(DelaySource), typeof(DelayBadge),
        new FrameworkPropertyMetadata(DelaySource.None, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualStateChanged));

    /// <summary>未测文案（宿主模板绑定 i18n key；默认中文兜底，05 键制接入后由使用方覆盖）</summary>
    public static readonly DependencyProperty NotTestedTextProperty = DependencyProperty.Register(
        nameof(NotTestedText), typeof(string), typeof(DelayBadge),
        new FrameworkPropertyMetadata("未测", FrameworkPropertyMetadataOptions.AffectsRender, OnVisualStateChanged));

    static DelayBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DelayBadge), new FrameworkPropertyMetadata(typeof(DelayBadge)));
    }

    public int Delay
    {
        get => (int)GetValue(DelayProperty);
        set => SetValue(DelayProperty, value);
    }

    public DelaySource DelaySource
    {
        get => (DelaySource)GetValue(DelaySourceProperty);
        set => SetValue(DelaySourceProperty, value);
    }

    public string NotTestedText
    {
        get => (string)GetValue(NotTestedTextProperty);
        set => SetValue(NotTestedTextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVisual();
    }

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((DelayBadge)d).UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (GetTemplateChild("PART_Root") is not Border root || GetTemplateChild("PART_Text") is not TextBlock text)
            return;

        if (Delay < 0)
        {
            root.Background = null;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            text.Text = NotTestedText;
            return;
        }

        var tier = Delay <= 80 ? "Success" : Delay <= 200 ? "Warning" : "Danger";
        // 实色 = Clash API 实测；Soft = 握手延迟
        var brushKey = DelaySource == DelaySource.ClashApi ? $"Brush.{tier}" : $"Brush.{tier}.Soft";
        root.SetResourceReference(Border.BackgroundProperty, brushKey);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        text.Text = $"{Delay} ms";
    }
}
