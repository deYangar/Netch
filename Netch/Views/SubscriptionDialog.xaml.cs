using System.Windows;
using Netch.Localization;
using Netch.Models;
using Netch.Utils;
using Microsoft.VisualStudio.Threading;

namespace Netch.Views;

/// <summary>添加/编辑订阅（06§B2）：平移旧 SubscriptionForm 编辑组三字段；Link 非空校验。</summary>
public partial class SubscriptionDialog
{
    private readonly Subscription? _editing;

    /// <summary>新建模式的结果（编辑模式为 null）</summary>
    public Subscription Result { get; private set; } = new();

    private SubscriptionDialog(Subscription? editing)
    {
        _editing = editing;
        InitializeComponent();

        if (editing != null)
        {
            remarkBox.Text = editing.Remark;
            linkBox.Text = editing.Link;
            userAgentBox.Text = editing.UserAgent;
            Result = editing;
        }
        else
        {
            // 咩咩 2026-10-31 反馈③：新建订阅给默认 UA（与实际请求发送的兜底 UA 同源，可手改；
            // 留空语义不变——SendAsync 空值仍回落 WebUtil.DefaultUserAgent）
            userAgentBox.Text = WebUtil.DefaultUserAgent;
        }

        ValidateLink();
    }

    public static SubscriptionDialog Create() => new(null);

    public static SubscriptionDialog Edit(Subscription sub) => new(sub);

    private void ValidateLink()
    {
        var empty = string.IsNullOrWhiteSpace(linkBox.Text);
        linkError.Text = empty ? I18N.T("subscription.error.linkRequired") : "";
        saveButton.IsEnabled = !empty;
    }

    private void LinkBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidateLink();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(linkBox.Text))
            return;

        Result.Remark = remarkBox.Text.Trim();
        Result.Link = linkBox.Text.Trim();
        Result.UserAgent = userAgentBox.Text.Trim();

        if (_editing == null)
            Result.Enable = true;

        Configuration.SaveAsync().Forget();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
