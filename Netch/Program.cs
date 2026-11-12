using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Microsoft.VisualStudio.Threading;
using Netch.Controllers;
using Netch.Enums;
using Netch.UI.Hub;
using Netch.Utils;
using Serilog.Events;
using SingleInstance;
using Netch.Localization;
#if RELEASE
using Windows.Win32.UI.WindowsAndMessaging;
#endif

namespace Netch;

/// <summary>
///     程序静态资产（UI 重构 04 后不再承担入口职责，WPF 入口在 App）：
///     单实例服务 / 控制台句柄 / 日志装配 / 环境检查。
/// </summary>
public static class Program
{
    public static readonly ISingleInstanceService SingleInstance = new SingleInstanceService($"Global\\{nameof(Netch)}");

    internal static HWND ConsoleHwnd { get; private set; }

#pragma warning disable VSTHRD002
    // VSTHRD002: Avoid problematic synchronous waits
    // 仅启动路径同步等待一次，语义与旧 Program.Main 相同

    /// <summary>启动时记录环境信息（App.OnStartup 调用）</summary>
    public static async Task LogEnvironmentAsync()
    {
        Log.Information("Netch Version: {Version}", $"{UpdateChecker.Owner}/{UpdateChecker.Repo}@{UpdateChecker.Version}");
        Log.Information("OS: {OSVersion}", Environment.OSVersion);
        // P-5（2026-11-04 审查）：单文件发版后 exe 自嵌内核 zip 已 219MB，每次启动全量读盘算哈希纯为写一行日志；
        // 仅 --ForceUpdate（更新校验场景）才值得算
        if (Flags.AlwaysShowNewVersionFound)
            Log.Information("SHA256: {Hash}", $"{await Utils.Utils.Sha256CheckSumAsync(Global.NetchExecutable)}");
        Log.Information("System Language: {Language}", CultureInfo.CurrentCulture.Name);

#if RELEASE
        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            // TODO log level setting
            Task.Run(() => Log.Debug("Third-party Drivers:\n{Drivers}", string.Join(Constants.EOF, SystemInfo.SystemDrivers(false)))).Forget();
            Task.Run(() => Log.Debug("Running Processes: \n{Processes}", string.Join(Constants.EOF, SystemInfo.Processes(false)))).Forget();
        }
#endif
    }

    /// <summary>CLR 版本检查（不支持的版本提示一次；App.OnStartup 调用）</summary>
    public static void CheckClr()
    {
        var framework = Assembly.GetExecutingAssembly().GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (framework == null)
        {
            Log.Warning("TargetFrameworkAttribute null");
            return;
        }

        var frameworkName = new FrameworkName(framework);

        if (frameworkName.Version.Major != Environment.Version.Major)
        {
            Log.Information("CLR: {Version}", Environment.Version);
            Flags.NoSupport = true;
            if (!Global.Settings.NoSupportDialog)
                Global.UIHub.Alert(
                    I18N.TF("common.0WonTGetDevelopers",
                        "CLR " + Environment.Version),
                    DialogLevel.Warning);
        }
    }

    /// <summary>OS 版本检查（App.OnStartup 调用）</summary>
    public static void CheckOS()
    {
        if (Environment.OSVersion.Version.Build < 17763)
        {
            Flags.NoSupport = true;
            if (!Global.Settings.NoSupportDialog)
                Global.UIHub.Alert(
                    I18N.TF("common.0WonTGetDevelopers",
                        Environment.OSVersion),
                    DialogLevel.Warning);
        }
    }

#pragma warning restore VSTHRD002

    /// <summary>分配控制台窗口（RELEASE 下隐藏）</summary>
    public static void InitConsole()
    {
        PInvoke.AllocConsole();

        ConsoleHwnd = PInvoke.GetConsoleWindow();
#if RELEASE
        // hide console window
        PInvoke.ShowWindow(ConsoleHwnd, SHOW_WINDOW_CMD.SW_HIDE);
#endif
    }

    public static void CreateLogger()
    {
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Verbose()
#else
            .MinimumLevel.Debug()
#endif
            .WriteTo.Async(c => c.File(Path.Combine(Global.NetchDir, Constants.LogFile),
                outputTemplate: Constants.OutputTemplate,
                rollOnFileSizeLimit: false))
            .WriteTo.Console(outputTemplate: Constants.OutputTemplate)
            .MinimumLevel.Override(@"Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .CreateLogger();
    }

    /// <summary>二次启动实例的参数回调：唤起主窗</summary>
    public static void OnSingleInstanceReceived((string, Action<string>) receive)
    {
        var (arg, endFunc) = receive;
        if (arg == Constants.Parameter.Show)
        {
            Global.UIHub.ShowMainForm();
        }

        endFunc(string.Empty);
    }
}
