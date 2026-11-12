using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.UI.Hub;
using WindowsJobAPI;

namespace Netch;

public static class Global
{
    /// <summary>
    ///     用于读取和写入的配置
    /// </summary>
    public static Setting Settings = new();

    public static readonly JobObject Job = new();

    /// <summary>
    ///     用于存储模式
    /// </summary>
    public static readonly List<Mode> Modes = new();

    public static readonly string NetchDir;
    public static readonly string NetchExecutable;

    static Global()
    {
        // WPF 下 Application.StartupPath/ExecutablePath（WinForms）不可用，改取进程本体路径（04§2 平移）
        NetchExecutable = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Environment.ProcessPath is null");
        NetchDir = AppContext.BaseDirectory;
    }

    /// <summary>
    ///     业务层访问主界面/托盘/对话框的唯一通道（启动时装配 WpfUIHub）
    /// </summary>
    public static IMainUIHub UIHub { get; set; } = null!;

    public static JsonSerializerOptions NewCustomJsonSerializerOptions() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
