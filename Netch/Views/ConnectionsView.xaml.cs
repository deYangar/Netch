using System.Windows;
using System.Windows.Controls;
using Netch.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace Netch.Views;

/// <summary>
///     连接观测页（07§C）：纯绑定；页面常驻（VM 单例，导航切换不丢历史）。
///     列宽记忆：三表列不可重排（CanUserReorderColumns 默认 false，序号稳定），退出时记录、
///     构造时应用；出厂值 = XAML 声明值，构造即快照，是「恢复默认大小」的复位来源。
/// </summary>
public partial class ConnectionsView : UserControl
{
    /// <summary>当前列宽出厂口径版本：XAML 默认列宽调整时 +1，旧版本记忆整体作废</summary>
    internal const int WidthSchema = 1;

    /// <summary>持久化 key 前缀：表名与 XAML 定义顺序一一对应</summary>
    private static readonly string[] TabKeys = { "process", "kernel", "history" };

    /// <summary>当前实例（页面经 MainViewModel._pages 缓存常驻，进程内至多一个；null = 尚未导航到本页）</summary>
    private static ConnectionsView? _instance;

    /// <summary>出厂列宽快照（key 同持久化格式），仅首次构造记录；null = 未快照</summary>
    private static Dictionary<string, DataGridLength>? _factoryWidths;

    public ConnectionsView()
    {
        InitializeComponent();
        _instance = this;
        _factoryWidths ??= CaptureWidths(this);
        ApplySavedWidths(this);
    }

    /// <summary>三张表与其持久化前缀的配对</summary>
    private static List<KeyValuePair<string, DataGrid>> TabGrids(ConnectionsView view) => new()
    {
        new(TabKeys[0], view.processGrid),
        new(TabKeys[1], view.kernelGrid),
        new(TabKeys[2], view.historyGrid)
    };

    /// <summary>按持久化格式抓取三表当前列宽（star 列记 -1）</summary>
    private static Dictionary<string, DataGridLength> CaptureWidths(ConnectionsView view)
    {
        var widths = new Dictionary<string, DataGridLength>();
        foreach (var (key, grid) in TabGrids(view))
            for (var i = 0; i < grid.Columns.Count; i++)
                widths[$"{key}:{i}"] = grid.Columns[i].Width;
        return widths;
    }

    /// <summary>应用记忆列宽（仅覆盖有记录的列；页面构造时调用一次；口径版本不符整批作废）</summary>
    private static void ApplySavedWidths(ConnectionsView view)
    {
        if (Global.Settings.ConnectionsColumnWidthsSchema != WidthSchema)
            return;

        foreach (var (key, grid) in TabGrids(view))
        {
            for (var i = 0; i < grid.Columns.Count; i++)
            {
                if (!Global.Settings.ConnectionsColumnWidths.TryGetValue($"{key}:{i}", out var width))
                    continue;
                grid.Columns[i].Width = width < 0
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : new DataGridLength(width, DataGridLengthUnitType.Pixel);
            }
        }
    }

    /// <summary>退出时记录列宽（SaveWindowSize 同路调用；页面未打开过 = 无改动可记，no-op）</summary>
    public static void SaveColumnWidths()
    {
        if (_instance == null)
            return;

        var settings = Global.Settings.ConnectionsColumnWidths;
        settings.Clear();
        Global.Settings.ConnectionsColumnWidthsSchema = WidthSchema;
        foreach (var (key, grid) in TabGrids(_instance))
        {
            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var width = grid.Columns[i].Width;
                // 非星号列存 Value（设定值恒有效；DisplayValue 未布局时为 NaN 会炸 JSON）
                settings[$"{key}:{i}"] = width.IsStar ? -1 : width.Value;
            }
        }
    }

    /// <summary>恢复出厂列宽：清记忆；页面已打开则立即复位（「恢复默认大小」按钮同路调用）</summary>
    public static void ResetColumnWidths()
    {
        Global.Settings.ConnectionsColumnWidths.Clear();
        if (_instance == null || _factoryWidths == null)
            return;

        foreach (var (key, grid) in TabGrids(_instance))
            for (var i = 0; i < grid.Columns.Count; i++)
                grid.Columns[i].Width = _factoryWidths[$"{key}:{i}"];
    }
}
