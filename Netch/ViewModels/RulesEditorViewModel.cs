using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;
using Netch.Services;
using Netch.Utils;

namespace Netch.ViewModels;

public enum RuleEditMode
{
    RuleSetRef,
    Inline
}

/// <summary>两种模式编辑器共用的有序规则列表（RE-3§4.1-4.3，D2）</summary>
public partial class RulesEditorViewModel : ObservableObject
{
    private readonly Dictionary<RouteRule, EditableRouteRuleViewModel> _editors = new();

    public RulesEditorViewModel(ObservableCollection<RouteRule> rules)
    {
        Rules = rules;
        foreach (var rule in rules)
            _editors[rule] = new EditableRouteRuleViewModel(rule);
        Rules.CollectionChanged += (_, e) => SyncEditors(e);
    }

    private void SyncEditors(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            return; // Move 的 OldItems/NewItems 是同一批对象，规则与选中项都必须保留。

        foreach (var rule in e.OldItems?.OfType<RouteRule>() ?? [])
        {
            _editors.Remove(rule);
            if (SelectedRule == rule)
                SelectedRule = null;
        }
        foreach (var rule in e.NewItems?.OfType<RouteRule>() ?? [])
            _editors.TryAdd(rule, new EditableRouteRuleViewModel(rule));
        // D3 预留入口：集合由宿主或其他编辑器实例追加后，所有实例都拿到同一对象的草稿。
        if (SelectedRule != null && !Rules.Contains(SelectedRule))
            SelectedRule = null;
    }

    public ObservableCollection<RouteRule> Rules { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEditable))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RouteRule? _selectedRule;

    // 低危⑩（2026-11-04 审查）：TryGetValue 防缺键 KeyNotFoundException（当前路径闭合，防御性）
    public EditableRouteRuleViewModel? SelectedEditable => SelectedRule != null && _editors.TryGetValue(SelectedRule, out var editor)
        ? editor
        : null;

    public bool HasSelection => SelectedRule != null;

    partial void OnSelectedRuleChanged(RouteRule? value)
    {
        OnPropertyChanged(nameof(SelectedEditable));
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new RouteRule();
        Rules.Add(rule);
        _editors[rule] = new EditableRouteRuleViewModel(rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule == null)
            return;
        _editors.Remove(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
    }

    [RelayCommand]
    private void MoveUp() => Move(SelectedRule == null ? -1 : Rules.IndexOf(SelectedRule) - 1);

    [RelayCommand]
    private void MoveDown() => Move(SelectedRule == null ? -1 : Rules.IndexOf(SelectedRule) + 1);

    [RelayCommand]
    private void MoveTop() => Move(0);

    [RelayCommand]
    private void MoveBottom() => Move(Rules.Count - 1);

    private void Move(int target)
    {
        if (SelectedRule == null)
            return;
        var index = Rules.IndexOf(SelectedRule);
        if (index >= 0 && target >= 0 && target < Rules.Count)
            Rules.Move(index, target);
    }

    public string? CommitAndValidate()
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            _editors[rule].Commit();
            if (rule.RuleSet is { Count: > 0 } && rule.Enabled().Any())
                return I18N.T("kernel.ruleRefInlineExclusive");
            if (!rule.HasMatcher)
                return I18N.TF("modeEditor.rule.emptyRule", i + 1);
            foreach (var tag in rule.RuleSet ?? [])
                if (!RouteRuleValidator.TagReferencable(tag))
                    return I18N.TF("modeEditor.rule.deadRef", i + 1, tag);
            foreach (var dim in rule.Enabled())
                foreach (var value in rule.Get(dim)!)
                {
                    if (RouteRuleValidator.ValidateValue(dim, value) == null)
                        continue;
                    return I18N.TF("modeEditor.rule.badValue", i + 1, I18N.T($"dim.{dim}"), value);
                }
            if (!rule.HasMatcher)
                return I18N.TF("modeEditor.rule.emptyRule", i + 1);
        }
        NotifySummaries();
        return null;
    }

    public void NotifySummaries()
    {
        System.Windows.Data.CollectionViewSource.GetDefaultView(Rules).Refresh();
    }
}

/// <summary>选中规则的草稿；仅 Commit 时写回 Model，切换选中不会丢草稿</summary>
public partial class EditableRouteRuleViewModel : ObservableObject
{
    public EditableRouteRuleViewModel(RouteRule model)
    {
        Model = model;
        Action = model.Action;
        EditMode = model.Enabled().Any() ? RuleEditMode.Inline : RuleEditMode.RuleSetRef;
        Options = new ObservableCollection<RuleSetOptionViewModel>();
        var knownTags = new HashSet<string>();
        foreach (var config in Global.Settings.RuleSets)
        {
            knownTags.Add(config.Tag);
            Options.Add(new RuleSetOptionViewModel(config.Tag,
                config.Type == RuleSetKind.Local
                    ? I18N.T("modeEditor.rule.group.customLocal")
                    : I18N.T("modeEditor.rule.group.customRemote"),
                config.Type == RuleSetKind.Local ? I18N.TF("rulesets.local.summaryEntries", config.Entries?.Count ?? 0) : config.Url,
                model.RuleSet?.Contains(config.Tag) == true));
        }
        foreach (var definition in BuiltInRuleSets.All)
        {
            knownTags.Add(definition.Tag);
            var resource = ExternalResourceService.Find($"ruleset.{definition.Tag}")!;
            Options.Add(new RuleSetOptionViewModel(definition.Tag, I18N.T("modeEditor.rule.group.builtin"),
                I18N.T("rulesets.builtin.ok"), model.RuleSet?.Contains(definition.Tag) == true,
                !Global.Settings.UseBuiltInRuleSets || ExternalResourceService.IsDisabled(resource)));
        }
        foreach (var tag in model.RuleSet?.Where(t => !knownTags.Contains(t)) ?? [])
            Options.Add(new RuleSetOptionViewModel(tag, "", I18N.T("modeEditor.rule.deadRef"),
                true, deadReference: true));
        Dimensions = new ObservableCollection<DimensionRowViewModel>();
        GroupedOptions = System.Windows.Data.CollectionViewSource.GetDefaultView(Options);
        GroupedOptions.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Group"));
        GroupedOptions.Filter = o => o is RuleSetOptionViewModel option &&
            (string.IsNullOrWhiteSpace(SearchText) || option.Tag.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var dim in model.Enabled())
            foreach (var value in model.Get(dim)!)
                Dimensions.Add(new DimensionRowViewModel { Kind = dim, Value = value });
    }

    public RouteRule Model { get; }

    [ObservableProperty]
    private RouteRuleAction _action;

    [ObservableProperty]
    private RuleEditMode _editMode;

    public IEnumerable<RuleSetOptionViewModel> FilteredOptions => Options.Where(o =>
        string.IsNullOrWhiteSpace(SearchText) || o.Tag.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    public ICollectionView GroupedOptions { get; }

    private string _searchText = "";

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredOptions));
            GroupedOptions.Refresh();
        }
    }

    public ObservableCollection<RuleSetOptionViewModel> Options { get; }

    public ObservableCollection<DimensionRowViewModel> Dimensions { get; }

    public IReadOnlyList<DimensionChoice> DimensionChoices { get; } = Enum.GetValues<RouteRuleDimension>()
        .Select(d => new DimensionChoice(d, I18N.T($"dim.{d}"))).ToList();

    public IReadOnlyList<RouteRuleAction> Actions { get; } = Enum.GetValues<RouteRuleAction>();

    public string SummaryText => Model.SummaryText;
    public string RuleSetText => Model.RuleSetText;
    public string ActionText => Model.ActionText;

    public void Commit()
    {
        Model.Action = Action;
        if (EditMode == RuleEditMode.RuleSetRef)
        {
            Model.RuleSet = Options.Where(o => o.IsChecked).Select(o => o.Tag).ToList();
            foreach (var dim in Enum.GetValues<RouteRuleDimension>())
                Model.Set(dim, null);
        }
        else
        {
            Model.RuleSet = null;
            foreach (var dim in Enum.GetValues<RouteRuleDimension>())
            {
                var values = Dimensions.Where(d => d.Kind == dim && !string.IsNullOrWhiteSpace(d.Value))
                    .Select(d => d.Value.Trim()).ToList();
                Model.Set(dim, values.Count > 0 ? values : null);
            }
        }
    }
}

public partial class RuleSetOptionViewModel(string tag, string group, string hint, bool selected, bool disabled = false, bool deadReference = false) : ObservableObject
{
    public string Tag { get; } = tag;
    public string Group { get; } = group;
    public string Hint { get; } = disabled ? I18N.T("rulesets.builtin.disabled") : hint;
    public string DisplayText => deadReference ? I18N.TF("modeEditor.rule.deadRef", 0, Tag) : Tag;
    public string ForegroundToken => deadReference ? "Brush.Danger" : "Brush.TextPrimary";

    [ObservableProperty]
    private bool _isChecked = selected;

    public bool IsDisabled { get; } = disabled;
}

public partial class DimensionRowViewModel : ObservableObject
{
    [ObservableProperty]
    private RouteRuleDimension _kind;

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _error = "";

    public string Validate()
    {
        var key = RouteRuleValidator.ValidateValue(Kind, Value);
        Error = key == null || Value.Length == 0 ? "" : I18N.TF(key, Value);
        return Error;
    }
}

