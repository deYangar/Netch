using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Netch.Localization;

namespace Netch.Views;

/// <summary>
///     单行输入小窗（04：Profile 新建/重命名）。Show 返回 null=取消。
///     标题/按钮文案是窗口级内容，走构造参数与 I18N 直取（切换语言时本窗通常是短生命周期）。
/// </summary>
public partial class InputDialog
{
    private InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        promptText.Text = prompt;
        inputBox.Text = defaultValue;
        okButton.Content = I18N.T("common.ok");
        cancelButton.Content = I18N.T("common.cancel");

        Loaded += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        okButton.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        cancelButton.Click += (_, _) => Close();
    }

    /// <summary>ESC=取消</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>弹出输入框。返回输入文本；null=取消</summary>
    public static string? Show(string title, string prompt, string defaultValue = "")
    {
        var owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
        var dialog = new InputDialog(title, prompt, defaultValue);
        if (owner != null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true ? dialog.inputBox.Text : null;
    }
}
