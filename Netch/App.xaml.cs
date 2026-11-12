using System.Windows;
using Application = System.Windows.Application;
using Microsoft.VisualStudio.Threading;
using Netch.Controls;
using Netch.Controllers;
using Netch.Services;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Netch.Enums;
using Netch.UI.Hub;
using Netch.UI.Theme;
using Netch.Utils;
using Netch.ViewModels;
using Netch.Views;
using Serilog;
using Netch.Localization;

// VSTHRD002: 启动路径同步等待保持时序（04§2 平移旧 Program.Main 语义，其原文件顶部同 pragma）
#pragma warning disable VSTHRD002

namespace Netch;

/// <summary>
///     WPF 入口（UI 重构 04，平移自 Program.Main，04§2 清单逐段落位）。
/// </summary>
public partial class App : Application
{
    /// <summary>主 VM 单例（WpfUIHub/托盘/页面取用）</summary>
    public static MainViewModel? CurrentMain { get; internal set; }

    /// <summary>托盘图标单例（NotifyTip 经此发气泡；退出时 Dispose）</summary>
    public static TaskbarIcon? CurrentTrayIcon => _trayIcon;

    private static TaskbarIcon? _trayIcon;

    // 托盘菜单四项（Header 为命令式赋值文本，语言切换时重赋）
    private static System.Windows.Controls.MenuItem? _trayShowItem, _trayStartItem, _trayStopItem, _trayExitItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 注：勿在此强制 SoftwareOnly——WPF-UI 主题切换会把窗口背景刷成透明（backdrop 语义），
        // 软渲染合成不出 backdrop，透明区域直接黑掉（B2 实机抓到，探针 _tools/theme-probe 复现实锤）。
        // P0 白屏真因是缺 WPF-UI 字典合并，与渲染模式无关（裸 WPF 对照组已证环境无责）。

        base.OnStartup(e);

        // 参数处理 ForceUpdate（旧 34-35）
        if (e.Args.Contains(Constants.Parameter.ForceUpdate))
            Flags.AlwaysShowNewVersionFound = true;

        // CWD / PATH（旧 38-42；NetchDir 已随 Global 静态构造改 Environment.ProcessPath 体系）
        Directory.SetCurrentDirectory(Global.NetchDir);
        var binPath = Path.Combine(Global.NetchDir, "bin");
        Environment.SetEnvironmentVariable("PATH", $"{Environment.GetEnvironmentVariable("PATH")};{binPath}");

        // 预建目录（旧 54-60 的目录段提前：日志/配置的写入依赖在此之后立即发生）
        var directories = new[] { "mode\\Custom", "data", "i18n", "logging" };
        foreach (var item in directories)
            if (!Directory.Exists(item))
                Directory.CreateDirectory(item);

        // 单实例（旧 66-73）前移（2026-11-04 审查低危⑦）：第二实例在 EnsureKernels/CleanOld/清日志之前
        // 就应退出——与第一实例并发删 .old/写 bin/删活动日志都有竞态窗口。
        // 库内部 await 回捕获上下文（此时 Dispatcher 尚未 Run，sync wait 必死锁），切线程池等待再同步收尾
        if (!Program.SingleInstance.TryStartSingleInstance())
        {
            var notify = Task.Run(() => Program.SingleInstance.SendMessageToFirstInstanceAsync(Constants.Parameter.Show));
            notify.Wait();
            Environment.Exit(0);
            return;
        }

        Program.SingleInstance.Received.Subscribe(Program.OnSingleInstanceReceived);

        // 清理旧日志（旧 76-88；必须在 CreateLogger 之前——logger 打开后文件被占用，删除会抛）
        // 旧日志清理是尽力而为（2026-11-04 实测 VM 事故：旧 application.log 被占用时 Delete 抛 IOException，
        // 此段在挂钩与 logger 之前 = 静默中断启动。失败=保留旧文件，Serilog 追加写，无害——吞掉继续启动）
        try
        {
            if (Directory.Exists("logging"))
            {
                var directory = new DirectoryInfo("logging");
                foreach (var file in directory.GetFiles())
                    file.Delete();
                foreach (var dir in directory.GetDirectories())
                    dir.Delete(true);
            }
        }
        catch (Exception)
        {
            // 旧日志被占用（杀软扫描/残留句柄）：跳过清理，追加写
        }

        Program.InitConsole();
        Program.CreateLogger();

        // 异常挂钩前移（C-1，2026-11-04 审查）：原挂在 MainViewModel 构造之后，EnsureKernels/LoadConfig/
        // Theme 任何早期异常 = 静默闪退且无日志。现在 logger 已建，此后所有启动路径异常都有日志与可见提示
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI error");
            args.Handled = true;
            // 低危⑧：静默吞掉后 UI 可能停在中间态（如卡 Starting），至少给托盘气泡；
            // UIHub 未装配（极早期）时跳过提示
            Global.UIHub?.NotifyTip(I18N.T("main.unhandledUiErrorTip") + "\n" + args.Exception.Message, info: false);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "Unhandled error");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task error");
            args.SetObserved();
        };

        // 内核随包自释放（咩咩 2026-10-31 拍板）：首启/缺失/残缺时从嵌入 zip 释放到 bin\；
        // 用户区有效内核（含自行升级版）一个字节都不碰。静默不抛，失败由缺内核的 Guard 报错兜底
        Utils.KernelBootstrap.EnsureKernels(Global.NetchDir);

#if !DEBUG
        // bin 目录检查 + 退出码 2（旧 44-52；UIHub 未装配，用 WPF 自带 MessageBox，仅此一处）
        if (!Directory.Exists("bin") || !Directory.EnumerateFileSystemEntries("bin").Any())
        {
            I18N.SetLanguage("System");
            System.Windows.MessageBox.Show(I18N.T("common.extractAllFilesThenRun"));
            Environment.Exit(2);
        }
#endif

        // 清理旧文件（旧 54-60 的 CleanOld 段；单实例通过后才动 .old，与第一实例无并发）
        Updater.CleanOld(Global.NetchDir);

        // 装配配置（旧 63；同步等待保持时序，不引入并发）
        Configuration.LoadAsync().GetAwaiter().GetResult();

        // 外部资源 .trash 隔离区隔次启动清理（R 线 §4.4：删除的撤销窗口 = 一个会话生命周期）
        ExternalResourceService.CleanTrashOnStartup();

        // i18n 键值资源制（05）：跟随设置语言
        I18N.SetLanguage(Global.Settings.Language);

        // 主题三态（02）：窗口创建前应用
        ThemeManager.Apply(Global.Settings.AppearanceSettings.Theme);
        ThemeManager.SetAccent(string.IsNullOrEmpty(Global.Settings.AppearanceSettings.Accent)
            ? null
            : Global.Settings.AppearanceSettings.Accent);

        // 装配 hub 与主 VM（CheckClr/CheckOS 的 NoSupport 弹窗依赖 hub）
        CurrentMain = new MainViewModel();
        Global.UIHub = new WpfUIHub();

        // 环境日志与兼容性检查（旧 95-97）
        Program.LogEnvironmentAsync().Forget();
        Program.CheckClr();
        Program.CheckOS();

        // 旧 SetHighDpiMode/EnableVisualStyles/CompatibleTextRendering 删除（WPF 不需要，DPI 由 App.manifest 兜底）

        // 主窗优先于托盘：托盘图标构建的任何问题不得阻断主窗显示
        //（2026-10-29 事故：图标从 NetchDir\Resources 文件加载，release/ 该目录缺失 → OnStartup 中断 → 无窗口只有任务栏死图标）
        CurrentMain.InitializeNavigation();
        new MainWindow { DataContext = CurrentMain }.Show();

        BuildTrayIcon();

        // 外部资源自动更新调度（R 线 §4.5：启动延迟 30s 静默检查，仅 AutoUpdate=true 资源；内核只提醒不自动换）
        Services.ExternalResourceScheduler.Start();
    }

    /// <summary>
    ///     托盘/窗口图标源：程序集嵌入资源（csproj EmbeddedResource），单文件发布零外部依赖。
    ///     文件分发形态（NetchDir\Resources\Netch.ico）是发布通道不可控的外部依赖，曾因目录缺失炸掉启动
    /// </summary>
    internal static System.Windows.Media.ImageSource LoadTrayIconSource()
    {
        var assembly = typeof(App).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("Resources.Netch.ico", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var icon = new System.Windows.Media.Imaging.BitmapImage();
        icon.BeginInit();
        icon.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 流释放后图像仍可用
        icon.StreamSource = stream;
        icon.EndInit();
        icon.Freeze();
        return icon;
    }

    /// <summary>托盘图标（04§7）：双击唤起主窗；右键菜单 显示主窗/启动/停止（按 State 显隐）/退出</summary>
    private void BuildTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = LoadTrayIconSource(),
            ToolTipText = UpdateChecker.Name,
            Visibility = Visibility.Visible
        };

        _trayIcon.TrayMouseDoubleClick += (_, _) => Global.UIHub.ShowMainForm();

        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = I18N.T("tray.show") };
        showItem.Click += (_, _) => Global.UIHub.ShowMainForm();
        menu.Items.Add(showItem);
        _trayShowItem = showItem;

        var startItem = new System.Windows.Controls.MenuItem { Header = I18N.T("main.start") };
        startItem.SetBinding(System.Windows.Controls.MenuItem.VisibilityProperty,
            new System.Windows.Data.Binding("State")
            {
                Source = CurrentMain,
                // Stopped 也可启动（与 MainViewModel.CanStartStop 一致）
                Converter = new StateVisibilityConverter(State.Waiting, State.Stopped)
            });
        startItem.Click += (_, _) => CurrentMain?.StartStopCommand.Execute(null);
        menu.Items.Add(startItem);
        _trayStartItem = startItem;

        var stopItem = new System.Windows.Controls.MenuItem { Header = I18N.T("main.stop") };
        stopItem.SetBinding(System.Windows.Controls.MenuItem.VisibilityProperty,
            new System.Windows.Data.Binding("State")
            {
                Source = CurrentMain,
                Converter = new StateVisibilityConverter(State.Started, State.Degraded)
            });
        stopItem.Click += (_, _) => CurrentMain?.StartStopCommand.Execute(null);
        menu.Items.Add(stopItem);
        _trayStopItem = stopItem;

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = I18N.T("tray.exit") };
        exitItem.Click += (_, _) => CurrentMain?.ExitCommand.Execute(null);
        menu.Items.Add(exitItem);
        _trayExitItem = exitItem;

        _trayIcon.ContextMenu = menu;

        UIEvents.LanguageChanged += RefreshTrayMenuText;
    }

    /// <summary>语言切换：托盘菜单 Header 为命令式赋值文本，随 LanguageChanged 重赋（05§B4）；
    /// LanguageChanged 由设置页在 UI 线程发出</summary>
    private static void RefreshTrayMenuText()
    {
        if (_trayShowItem != null) _trayShowItem.Header = I18N.T("tray.show");
        if (_trayStartItem != null) _trayStartItem.Header = I18N.T("main.start");
        if (_trayStopItem != null) _trayStopItem.Header = I18N.T("main.stop");
        if (_trayExitItem != null) _trayExitItem.Header = I18N.T("tray.exit");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
