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
            nodeDnsBox.Text = string.Join(Environment.NewLine, editing.NodeDns);
            hostsBox.Text = FormatHosts(editing.Hosts);
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
        Result.NodeDns = ParseLines(nodeDnsBox.Text);
        Result.Hosts = ParseHosts(hostsBox.Text);

        if (_editing == null)
            Result.Enable = true;

        Configuration.SaveAsync().Forget();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    ///     多行文本 → 非空行列表（trim、去重保序）；空输入得到空列表（= 系统 DNS / 无映射）
    /// </summary>
    internal static List<string> ParseLines(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     多行文本 → Host 映射：每行「源域名=目标」（等号两侧 trim）；
    ///     # 开头注释行与空行跳过，无等号的非法行跳过（宽松解析，格式自由度优先）
    /// </summary>
    internal static Dictionary<string, string> ParseHosts(string text)
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
                continue;

            hosts[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return hosts;
    }

    private static string FormatHosts(Dictionary<string, string> hosts)
    {
        return string.Join(Environment.NewLine, hosts.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
