using System.Globalization;
using System.Windows.Data;

namespace Netch.Controls;

/// <summary>
///     字节数/流速自动单位格式化：1024 进制，1 位小数，尾零省略（1.2 MB/s、856 KB/s、3 B/s）。
///     parameter "rate" 附加 "/s"（流速列）；数值排序走绑定属性本身，不经本转换器。
/// </summary>
public class BytesConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            ulong u => (double)u,
            long l => l,
            int i => i,
            _ => 0d
        };

        var suffix = Equals(parameter, "rate") ? "/s" : "";
        var unitIndex = 0;
        while (bytes >= 1024 && unitIndex < Units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        var text = unitIndex == 0
            ? ((ulong)bytes).ToString("0", CultureInfo.InvariantCulture)
            : bytes.ToString("0.#", CultureInfo.InvariantCulture);

        return $"{text} {Units[unitIndex]}{suffix}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
