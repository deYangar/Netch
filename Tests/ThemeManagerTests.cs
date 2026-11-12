using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.UI.Theme;

namespace Tests;

/// <summary>
///     ThemeManager 纯逻辑单测（不依赖 Application 资源装配；字典合并行为由 UIHarness.WPF theme-swatch 像素采样覆盖）
/// </summary>
[TestClass]
public class ThemeManagerTests
{
    [TestMethod]
    public void ResolveSystemMode_RegistrySemantics()
    {
        // AppsUseLightTheme: 1=浅色（含默认值），0=深色
        Assert.AreEqual(ThemeMode.Light, ThemeManager.ResolveSystemMode(1));
        Assert.AreEqual(ThemeMode.Light, ThemeManager.ResolveSystemMode(2));
        Assert.AreEqual(ThemeMode.Dark, ThemeManager.ResolveSystemMode(0));
    }

    [TestMethod]
    public void Apply_LightAndDark_EffectiveMatches()
    {
        ThemeManager.Apply(ThemeMode.Light);
        Assert.AreEqual(ThemeMode.Light, ThemeManager.Effective);

        ThemeManager.Apply(ThemeMode.Dark);
        Assert.AreEqual(ThemeMode.Dark, ThemeManager.Effective);
    }

    [TestMethod]
    public void Apply_System_ResolvesFromRegistry()
    {
        var expected = ThemeManager.ResolveSystemMode(ThemeManager.ReadAppsUseLightTheme());
        ThemeManager.Apply(ThemeMode.System);
        Assert.AreEqual(expected, ThemeManager.Effective);
    }

    [TestMethod]
    public void IsValidAccentHex()
    {
        Assert.IsTrue(ThemeManager.IsValidAccentHex("#FF0000"));
        Assert.IsTrue(ThemeManager.IsValidAccentHex("00aaff"));
        Assert.IsFalse(ThemeManager.IsValidAccentHex("#FFF"));
        Assert.IsFalse(ThemeManager.IsValidAccentHex("#GG0000"));
        Assert.IsFalse(ThemeManager.IsValidAccentHex("#FF00000"));
        Assert.IsFalse(ThemeManager.IsValidAccentHex(""));
        Assert.IsFalse(ThemeManager.IsValidAccentHex(null));
    }

    [TestMethod]
    public void SetAccent_InvalidValue_DoesNotThrow()
    {
        // 非法强调色拒绝：不生效也不抛异常（Application 未装配场景同样安全）
        ThemeManager.SetAccent("not-a-color");
        ThemeManager.SetAccent(null);
        ThemeManager.SetAccent("");
    }
}
