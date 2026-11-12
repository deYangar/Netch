using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;
using Netch.ViewModels;
using Netch.Utils;

namespace Netch.Views;

/// <summary>Local 规则集内容编辑：副本编辑，保存时统一校验，取消不碰原配置（RE-2§3.5）</summary>
public partial class LocalRuleSetDialog
{
    private readonly RuleSetConfig? _editing;

    public RuleSetConfig? Result { get; private set; }

    public ObservableCollection<LocalEntryRow> Entries { get; } = new();

    public IReadOnlyList<DimensionChoice> Dimensions { get; } = Enum.GetValues<RouteRuleDimension>()
        .Select(d => new DimensionChoice(d, I18N.T($"dim.{d}"))).ToList();

    private LocalRuleSetDialog(RuleSetConfig? editing)
    {
        _editing = editing;
        InitializeComponent();
        DataContext = this;
        if (editing != null)
        {
            RuleSetTag = editing.Tag;
            foreach (var entry in editing.Entries ?? [])
                Entries.Add(new LocalEntryRow(entry));
        }
    }

    public string RuleSetTag { get; set; } = "";

    public static LocalRuleSetDialog Create() => new(null);

    public static LocalRuleSetDialog Edit(RuleSetConfig config) => new(config);

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        var tag = RuleSetTag.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, "^[a-zA-Z0-9-]+$"))
        {
            errorText.Text = I18N.TF("ruleSet.error.tag", tag);
            return;
        }
        if (BuiltInRuleSets.IsRegistered(tag) || Global.Settings.RuleSets.Any(r => r.Tag == tag && r != _editing))
        {
            errorText.Text = I18N.T("rulesets.duplicateTag");
            return;
        }
        for (var i = 0; i < Entries.Count; i++)
        {
            var row = Entries[i];
            if (string.IsNullOrWhiteSpace(row.Value))
            {
                errorText.Text = I18N.TF("rulesets.local.entryEmpty", i + 1);
                return;
            }
            var key = RouteRuleValidator.ValidateValue(row.Kind, row.Value.Trim());
            if (key != null)
            {
                errorText.Text = I18N.TF(key, row.Value.Trim());
                return;
            }
        }

        Result = new RuleSetConfig
        {
            Tag = tag,
            Type = RuleSetKind.Local,
            Entries = Entries.Select(r => new RuleSetEntry { Kind = r.Kind, Value = r.Value.Trim() }).ToList()
        };
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void AddEntry_Click(object sender, RoutedEventArgs e) => Entries.Add(new LocalEntryRow(null));

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalEntryRow row })
            Entries.Remove(row);
    }

    private void BulkImport_Click(object sender, RoutedEventArgs e)
    {
        bulkPanel.Visibility = bulkPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Parse_Click(object sender, RoutedEventArgs e)
    {
        var (entries, skipped) = BulkRuleParser.Parse(bulkBox.Text);
        foreach (var entry in entries)
            Entries.Add(new LocalEntryRow(entry));
        bulkResult.Text = I18N.TF("rulesets.local.bulkResult", entries.Count, skipped);
    }

    private void MoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalEntryRow row, Tag: string direction })
        {
            var index = Entries.IndexOf(row);
            var target = index + (direction == "up" ? -1 : 1);
            if (target >= 0 && target < Entries.Count)
                Entries.Move(index, target);
        }
    }

    private void Value_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: LocalEntryRow row })
        {
            var key = RouteRuleValidator.ValidateValue(row.Kind, row.Value);
            row.Error = string.IsNullOrWhiteSpace(row.Value) || key == null ? "" : I18N.TF(key, row.Value);
        }
    }
}

public partial class LocalEntryRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public LocalEntryRow(RuleSetEntry? entry)
    {
        Kind = entry?.Kind ?? RouteRuleDimension.DomainSuffix;
        Value = entry?.Value ?? "";
    }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private RouteRuleDimension _kind;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _value;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _error = "";
}
