using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Netch.Controls;

/// <summary>
///     路径自适应显示文本块：宽度放得下就显示完整路径，放不下按实际像素宽度做中间省略
///     （尽量长的头部前缀 + "..." + 完整文件名），列再窄文件名也不丢。
///     列宽拖动/窗口缩放经 SizeChanged 实时重排，替代写死字符数的 GetCompactPath。
///     展示文本计算在 <see cref="ComputeDisplayText"/>（纯函数，可离屏单测）。
/// </summary>
public class AdaptivePathTextBlock : TextBlock
{
    public static readonly DependencyProperty FullPathProperty = DependencyProperty.Register(
        nameof(FullPath), typeof(string), typeof(AdaptivePathTextBlock),
        new FrameworkPropertyMetadata("", OnFullPathChanged));

    /// <summary>完整路径（数据绑定源；Text 为按宽度自适应后的展示文本）</summary>
    public string FullPath
    {
        get => (string)GetValue(FullPathProperty);
        set => SetValue(FullPathProperty, value);
    }

    private const string Ellipsis = "...";

    public AdaptivePathTextBlock()
    {
        // 自算文本恒放得下；CharacterEllipsis 只兜极端窄列
        TextTrimming = TextTrimming.CharacterEllipsis;
        Loaded += (_, _) => Recompute();
        SizeChanged += (_, _) => Recompute();
    }

    private static void OnFullPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 行回收重用（VirtualizationMode=Recycling）换绑新路径时 ActualWidth 已有效，直接重算
        ((AdaptivePathTextBlock)d).Recompute();
    }

    private void Recompute()
    {
        var path = FullPath;
        if (string.IsNullOrEmpty(path))
        {
            Text = "";
            return;
        }

        var available = ActualWidth;
        if (double.IsNaN(available) || available <= 0)
        {
            // 未布局（首帧）：先给文件名，SizeChanged 到位后按真实宽度重排
            Text = FileName(path);
            return;
        }

        Text = ComputeDisplayText(path, available,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize, Foreground ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static string FileName(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>
    ///     自适应展示文本（纯函数）：全路径放得下给全路径；否则「最长头部前缀 + ... + 完整文件名」；
    ///     连 "...文件名" 都放不下返回原文（调用方 TextTrimming 尾部截断兜底）。
    /// </summary>
    internal static string ComputeDisplayText(string path, double available, Typeface typeface,
        double fontSize, Brush foreground, double pixelsPerDip)
    {
        if (MeasureWidth(path, typeface, fontSize, foreground, pixelsPerDip) <= available)
            return path;

        var fileName = FileName(path);
        var minTail = MeasureWidth(Ellipsis + fileName, typeface, fontSize, foreground, pixelsPerDip);
        if (minTail > available)
            return path;

        // 二分最大前缀：prefix + "..." + 文件名 ≤ 可用宽（头部信息量最大化，文件名永远完整）
        var headSeparator = path.IndexOf('\\');
        var lo = 0;
        var hi = path.Length - fileName.Length - Ellipsis.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (MeasureWidth(path[..mid] + Ellipsis + fileName, typeface, fontSize, foreground, pixelsPerDip) <= available)
                lo = mid;
            else
                hi = mid - 1;
        }

        return path[..lo] + Ellipsis + fileName;
    }

    private static double MeasureWidth(string text, Typeface typeface, double fontSize, Brush foreground, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            foreground,
            pixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}

/// <summary>
///     连接页进程列：绑定 FullPath（Process）的自适应路径列；ToolTip 绑同一路径展示全路径。
///     排序沿用 BoundColumn 契约（SortMemberPath = 绑定路径 = Process）。
/// </summary>
public class AdaptivePathColumn : DataGridBoundColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = new AdaptivePathTextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (Binding != null)
        {
            element.SetBinding(AdaptivePathTextBlock.FullPathProperty, Binding);
            element.SetBinding(System.Windows.FrameworkElement.ToolTipProperty, Binding);
        }

        return element;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        // 本表 IsReadOnly，不会进入编辑态；契约要求实现，占位返回
        return new TextBlock();
    }
}
