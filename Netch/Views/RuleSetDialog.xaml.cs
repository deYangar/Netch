using System.Windows;
using Netch.Localization;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

namespace Netch.Views;

/// <summary>规则集编辑（07§D）：四字段；tag 合法性/内置冲突/URL/间隔四条校验平移旧 RuleSetForm（错误显示于底部 Caption）。</summary>
public partial class RuleSetDialog
{
    private readonly RuleSetConfig? _editing;

    /// <summary>保存结果（确认后非 null）</summary>
    public RuleSetConfig? Result { get; private set; }

    private RuleSetDialog(RuleSetConfig? editing)
    {
        _editing = editing;
        InitializeComponent();
        if (editing != null)
        {
            tagBox.Text = editing.Tag;
            urlBox.Text = editing.Url;
            intervalBox.Text = editing.UpdateInterval ?? "";
        }

        saveButton.Click += (_, _) => Save();
        cancelButton.Click += (_, _) => Close();
    }

    public static RuleSetDialog Create() => new(null);

    public static RuleSetDialog Edit(RuleSetConfig config) => new(config);

    private void Save()
    {
        var tag = tagBox.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, "^[a-zA-Z0-9-]+$"))
        {
            errorText.Text = I18N.TF("ruleSet.error.tag", tag);
            return;
        }

        // 内置 tag 即使被单条禁用/总开关关闭也保留占用（配置生成按注册表识别内置优先）
        if (BuiltInRuleSets.IsRegistered(tag) || Netch.Global.Settings.RuleSets.Any(r => r.Tag == tag && r != _editing))
        {
            errorText.Text = I18N.TF("ruleSet.error.conflict", tag);
            return;
        }

        var url = urlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errorText.Text = I18N.TF("ruleSet.error.url", url);
            return;
        }

        var interval = intervalBox.Text.Trim();
        if (interval.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(interval, @"^\d+[smhd]$"))
        {
            errorText.Text = I18N.TF("ruleSet.error.interval", interval);
            return;
        }

        Result = new RuleSetConfig
        {
            Tag = tag,
            Url = url,
            UpdateInterval = interval.Length == 0 ? null : interval,
            Type = RuleSetKind.Remote
        };

        DialogResult = true;
        Close();
    }
}
