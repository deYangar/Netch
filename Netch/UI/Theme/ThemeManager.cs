using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;

namespace Netch.UI.Theme;

/// <summary>
///     三态主题（Light/Dark/System）应用与持久化入口。
///     资源结构：MergedDictionaries = [Tokens.Light|Tokens.Dark（调色板，Apply 换实例）, Tokens.Base, Controls]，
///     语义刷子 key 两主题同名（Brush.*），DynamicResource 引用随调色板字典整体替换热切换。
///     WPF-UI 侧经 ApplicationThemeManager 同步（FluentWindow/标题栏/其类型化控件的外观），
///     我方结构字典在其合并之后重排到末尾，保证我方隐式样式优先。
/// </summary>
public static class ThemeManager
{
    private static readonly object Lock = new();

    // pack URI 惰性构造：pack scheme 仅在 WPF 宿主注册，单测进程静态初始化会抛 UriFormatException
    private static Uri? _lightSource;
    private static Uri? _darkSource;
    private static Uri? _baseSource;
    private static Uri? _controlsSource;

    private static Uri LightSource => _lightSource ??= new Uri("pack://application:,,,/Netch;component/Themes/Tokens.Light.xaml");
    private static Uri DarkSource => _darkSource ??= new Uri("pack://application:,,,/Netch;component/Themes/Tokens.Dark.xaml");
    private static Uri BaseSource => _baseSource ??= new Uri("pack://application:,,,/Netch;component/Themes/Tokens.Base.xaml");
    private static Uri ControlsSource => _controlsSource ??= new Uri("pack://application:,,,/Netch;component/Themes/Controls.xaml");

    private static string? _accentHex;

    /// <summary>用户选择的主题态（System 未解析）</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>当前生效（已解析）主题</summary>
    public static ThemeMode Effective { get; private set; } = ThemeMode.Light;

    /// <summary>生效主题变化（App 内非绑定文本手动刷新用）</summary>
    public static event Action<ThemeMode>? EffectiveChanged;

    /// <summary>
    ///     应用主题。mode=System 时读注册表 HKCU\...\Themes\Personalize\AppsUseLightTheme 判定实际值。
    ///     无 Application 场景（单测/harness 装配前）只记录状态。
    /// </summary>
    public static void Apply(ThemeMode mode)
    {
        lock (Lock)
        {
            Mode = mode;
            var effective = mode == ThemeMode.System ? ResolveSystemMode(ReadAppsUseLightTheme()) : mode;
            Effective = effective;

            if (Application.Current == null)
                return;

            var dictionaries = Application.Current.Resources.MergedDictionaries;

            // 结构字典只装一次
            EnsureStructure(dictionaries);

            // 调色板字典整体替换（固定占第 0 位）
            var palette = new ResourceDictionary { Source = effective == ThemeMode.Light ? LightSource : DarkSource };
            if (dictionaries.Count > 0 && IsPalette(dictionaries[0]))
                dictionaries[0] = palette;
            else
                dictionaries.Insert(0, palette);

            // WPF-UI 同步（FluentWindow 标题栏与其类型化控件），随后把我方字典重排到末尾保证隐式样式优先
            ApplicationThemeManager.Apply(effective == ThemeMode.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            ReassertStructureLast(dictionaries);

            // 注：运行中切换时 WPF-UI 会把 Window.Background 延迟覆写为全透明（backdrop 语义，
            // theme-probe 实证且代码重设抢不过其派发时序）；窗口壳的对策在 MainWindow.xaml——
            // 根 DockPanel 铺不透明 Brush.Background，透明面板永远透出我方语义色（B2 实机缺陷治本）。

            if (!string.IsNullOrEmpty(_accentHex))
                ApplyAccentBrush(_accentHex);

        }
    }

    /// <summary>系统主题变化通知（System 模式下自动重 Apply）。主窗口 WM_SETTINGCHANGE 钩子触发</summary>
    public static void OnSystemThemeChanged()
    {
        if (Mode != ThemeMode.System)
            return;

        Apply(ThemeMode.System);
    }

    /// <summary>强调色覆盖（#RRGGBB），null/空=恢复默认 Accent。非法值拒绝（保持现状）</summary>
    public static void SetAccent(string? hex)
    {
        lock (Lock)
        {
            if (string.IsNullOrEmpty(hex))
            {
                _accentHex = null;
                if (Application.Current != null)
                    ResetAccentBrush();
                return;
            }

            if (!IsValidAccentHex(hex))
            {
                Log.Warning("Invalid accent color \"{Hex}\", keep current", hex);
                return;
            }

            _accentHex = NormalizeAccent(hex);
            if (Application.Current != null)
                ApplyAccentBrush(_accentHex);
        }
    }

    /// <summary>注册表读取（_appsUseLightTheme: 1=浅色默认）。读取失败回退浅色</summary>
    public static int ReadAppsUseLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i ? i : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>System 态解析（纯函数，单测用）</summary>
    public static ThemeMode ResolveSystemMode(int appsUseLightTheme)
    {
        return appsUseLightTheme == 0 ? ThemeMode.Dark : ThemeMode.Light;
    }

    /// <summary>强调色合法性：#RRGGBB（6 位十六进制，可带 # 前缀）</summary>
    public static bool IsValidAccentHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var body = hex.StartsWith('#') ? hex[1..] : hex;
        if (body.Length != 6)
            return false;

        return body.All(char.IsAsciiHexDigit);
    }

    private static string NormalizeAccent(string hex)
    {
        var body = hex.StartsWith('#') ? hex[1..] : hex;
        return $"#{body.ToUpperInvariant()}";
    }

    private static void EnsureStructure(IList<ResourceDictionary> dictionaries)
    {
        // 按 URI 值相等判定（App.xaml 里的相对 Source 与 pack URI 不 ReferenceEqual，判重会漏 → 字典重复加载）
        if (!dictionaries.Any(d => SameSource(d.Source, BaseSource)))
            dictionaries.Add(new ResourceDictionary { Source = BaseSource });

        if (!dictionaries.Any(d => SameSource(d.Source, ControlsSource)))
            dictionaries.Add(new ResourceDictionary { Source = ControlsSource });
    }

    private static bool SameSource(Uri? a, Uri b)
    {
        if (a == null)
            return false;
        return Uri.Compare(a, b, UriComponents.Path, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>把 Base/Controls 移到合并字典末尾（WPF-UI Apply 会把它的字典追加到后面，需重排压制其隐式样式）</summary>
    private static void ReassertStructureLast(IList<ResourceDictionary> dictionaries)
    {
        foreach (var source in new[] { BaseSource, ControlsSource })
        {
            ResourceDictionary? found = null;
            foreach (var dict in dictionaries)
            {
                if (SameSource(dict.Source, source))
                {
                    found = dict;
                    break;
                }
            }

            if (found == null)
                continue;

            dictionaries.Remove(found);
            dictionaries.Add(found);
        }
    }

    private static bool IsPalette(ResourceDictionary? dictionary)
    {
        return dictionary != null && (ReferenceEquals(dictionary.Source, LightSource) || ReferenceEquals(dictionary.Source, DarkSource));
    }

    private static void ApplyAccentBrush(string hex)
    {
        var resources = Application.Current.Resources;
        var accent = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        accent.Freeze();
        resources["Brush.Accent"] = accent;
        resources["Brush.Primary"] = accent;
    }

    /// <summary>恢复默认强调色：移除覆盖条目，DynamicResource 回落到调色板字典里的语义刷子（不走颜色解析）</summary>
    private static void ResetAccentBrush()
    {
        if (Application.Current == null)
            return;

        var resources = Application.Current.Resources;
        if (resources.Contains("Brush.Accent"))
            resources.Remove("Brush.Accent");
        if (resources.Contains("Brush.Primary"))
            resources.Remove("Brush.Primary");
    }
}
