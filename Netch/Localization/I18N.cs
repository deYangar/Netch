using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using System.Windows.Markup;

namespace Netch.Localization;

/// <summary>
///     i18n 键值资源制管理器（05§B1）。中性 Strings.resx=en-US，Strings.zh-CN.resx=简中；
///     缺失回退中性，中性也缺=抛异常（开发期暴露）。
/// </summary>
public static class I18N
{
    private static readonly ResourceManager Manager = new("Netch.Localization.Strings", typeof(I18N).Assembly);

    private static CultureInfo _culture = ResolveCulture(null);

    /// <summary>语言切换后触发：页面级手动刷新用（绑定文案由 I18NFacade 自动刷新）</summary>
    public static event Action? LanguageChanged;

    /// <summary>当前解析后的文化（System 启动时=系统 UI 文化）</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>设置语言（"System"/"zh-CN"/"en-US"），立即生效</summary>
    public static void SetLanguage(string? langCode)
    {
        _culture = ResolveCulture(langCode);
        LanguageChanged?.Invoke();
        I18NFacade.Instance.RaiseChanged();
    }

    private static CultureInfo ResolveCulture(string? langCode)
    {
        if (string.IsNullOrEmpty(langCode) || langCode is "System" or "Auto")
            return CultureInfo.CurrentUICulture;

        try
        {
            return CultureInfo.GetCultureInfo(langCode);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentUICulture;
        }
    }

    /// <summary>查 key（当前文化自动选 resx；缺失回退中性，中性也缺=抛异常暴露）</summary>
    public static string T(string key)
    {
        return Manager.GetString(key, _culture)
               ?? throw new InvalidOperationException($"i18n key missing: {key}");
    }

    /// <summary>格式化查 key（string.Format）</summary>
    public static string TF(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }
}

/// <summary>
///     绑定桥：Item[key] → I18N.T(key)。SetLanguage 时触发 Item[] 变更通知，全部绑定一次刷新（05§B4）。
/// </summary>
public sealed class I18NFacade : INotifyPropertyChanged
{
    public static readonly I18NFacade Instance = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => I18N.T(key);

    internal void RaiseChanged()
    {
        PropertyChanged?.Invoke(this, EventArgsCache.Item);
    }

    private static class EventArgsCache
    {
        public static readonly PropertyChangedEventArgs Item = new("Item[]");
    }
}

/// <summary>
///     XAML 用法：<c>Text="{I18N main.start}"</c>——返回绑定到 I18NFacade 索引器，语言切换自动刷新。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class I18NExtension : MarkupExtension
{
    public I18NExtension()
    {
    }

    public I18NExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"Item[{Key}]")
        {
            Source = I18NFacade.Instance,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }
}
