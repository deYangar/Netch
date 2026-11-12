using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Localization;
using Netch.Models;
using Netch.Servers;
using Netch.Services;
using Netch.UI.Hub;
using Netch.Utils;

namespace Netch.ViewModels;

/// <summary>规则集内容库：内容管理与设置页下载管理分责（RE-2§3.3-3.6）</summary>
[Fody.ConfigureAwait(true)]
public partial class RuleSetsViewModel : ObservableObject
{
    public ObservableCollection<BuiltinRuleSetRowViewModel> BuiltinRows { get; } = new();
    public ObservableCollection<CustomRuleSetRowViewModel> CustomRows { get; } = new();

    public RuleSetsViewModel()
    {
        ReloadRows();
        UIEvents.LanguageChanged += ReloadRows;
    }

    public bool UseBuiltInRuleSets
    {
        get => Global.Settings.UseBuiltInRuleSets;
        set
        {
            if (Global.Settings.UseBuiltInRuleSets == value)
                return;
            Global.Settings.UseBuiltInRuleSets = value;
            OnPropertyChanged();
            ReloadRows();
            _ = Configuration.SaveAsync();
        }
    }

    public void ReloadRows()
    {
        BuiltinRows.Clear();
        foreach (var definition in BuiltInRuleSets.All)
            BuiltinRows.Add(new BuiltinRuleSetRowViewModel(definition, this));
        CustomRows.Clear();
        foreach (var config in Global.Settings.RuleSets)
            CustomRows.Add(new CustomRuleSetRowViewModel(config, this));
        OnPropertyChanged(nameof(HasCustomRows));
    }

    public bool HasCustomRows => CustomRows.Count > 0;

    [RelayCommand]
    private async Task NewLocalAsync()
    {
        var dialog = Views.LocalRuleSetDialog.Create();
        dialog.Owner = Application.Current?.MainWindow;
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;
        Global.Settings.RuleSets.Add(dialog.Result);
        ReloadRows();
        await Configuration.SaveAsync();
    }

    [RelayCommand]
    private async Task NewRemoteAsync()
    {
        var dialog = Views.RuleSetDialog.Create();
        dialog.Owner = Application.Current?.MainWindow;
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;
        Global.Settings.RuleSets.Add(dialog.Result);
        ReloadRows();
        await Configuration.SaveAsync();
    }

    public async Task EditAsync(CustomRuleSetRowViewModel row)
    {
        var config = row.Model;
        string tag = config.Tag;
        RuleSetConfig? result;
        if (config.Type == RuleSetKind.Local)
        {
            var dialog = Views.LocalRuleSetDialog.Edit(config);
            dialog.Owner = Application.Current?.MainWindow;
            if (dialog.ShowDialog() != true)
                return;
            result = dialog.Result;
        }
        else
        {
            var dialog = Views.RuleSetDialog.Edit(config);
            dialog.Owner = Application.Current?.MainWindow;
            if (dialog.ShowDialog() != true)
                return;
            result = dialog.Result;
        }

        if (result == null)
            return;
        try
        {
            var count = await RuleSetReferenceService.RenameRuleSetAsync(tag, result);
            ReloadRows();
            if (tag != result.Tag)
                Global.UIHub.Alert(I18N.TF("rulesets.renameSynced", count));
        }
        catch (Exception e)
        {
            ReloadRows();
            Global.UIHub.Alert(e.Message, DialogLevel.Error);
        }
    }

    public async Task DeleteAsync(CustomRuleSetRowViewModel row)
    {
        var config = row.Model;
        var referencing = RuleSetReferenceService.ReferencingModes(config.Tag);
        var confirmed = referencing.Count > 0
            ? await Global.UIHub.ConfirmAsync(I18N.TF("rulesets.deleteReferenced", config.Tag,
                string.Join(", ", referencing.Select(m => m.i18NRemark))))
            : await Global.UIHub.ConfirmAsync(I18N.TF("external.confirmDelete", config.Tag));
        if (!confirmed)
            return;
        var resource = ExternalResourceService.Find(ExternalResourceService.CustomRuleSetKey(config.Tag));
        Global.Settings.RuleSets.Remove(config);
        if (resource != null && ExternalResourceService.Exists(resource))
            ExternalResourceService.MoveToTrash(resource);
        ReloadRows();
        await Configuration.SaveAsync();
    }
}

public partial class BuiltinRuleSetRowViewModel : ObservableObject
{
    private readonly ExternalResource _resource;
    private readonly RuleSetsViewModel _owner;
    public BuiltinRuleSetRowViewModel(BuiltInRuleSets.Definition definition, RuleSetsViewModel owner)
    {
        Tag = definition.Tag;
        _owner = owner;
        _resource = ExternalResourceService.Find($"ruleset.{Tag}")!;
        RefText = ReferenceText(Tag);
    }
    public string Tag { get; }
    public string RefText { get; }
    public bool IsEnabled
    {
        get => !ExternalResourceService.IsDisabled(_resource);
        set
        {
            if (IsEnabled == value)
                return;
            _ = ExternalResourceService.MutateStateAsync(_resource, s => s.Disabled = !value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public bool CanToggle => Global.Settings.UseBuiltInRuleSets;
    public string StatusText => !Global.Settings.UseBuiltInRuleSets || !IsEnabled
        ? I18N.T("rulesets.builtin.disabled") : I18N.T("rulesets.builtin.ok");

    public static string ReferenceText(string tag)
    {
        var modes = RuleSetReferenceService.ReferencingModes(tag);
        return modes.Count == 0 ? I18N.T("rulesets.refs.none")
            : I18N.TF("rulesets.refs", modes.Count) + ": " + string.Join(", ", modes.Select(m => m.i18NRemark));
    }
}

public sealed class CustomRuleSetRowViewModel
{
    private readonly RuleSetsViewModel _owner;
    public CustomRuleSetRowViewModel(RuleSetConfig model, RuleSetsViewModel owner)
    {
        Model = model;
        _owner = owner;
        EditCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => owner.EditAsync(this));
        DeleteCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => owner.DeleteAsync(this));
    }
    public RuleSetConfig Model { get; }
    public string Tag => Model.Tag;
    public string TypeText => I18N.T(Model.Type == RuleSetKind.Local ? "rulesets.type.local" : "rulesets.type.remote");
    public string SummaryText => Model.Type == RuleSetKind.Local
        ? I18N.TF("rulesets.local.summaryEntries", Model.Entries?.Count ?? 0)
        : (Uri.TryCreate(Model.Url, UriKind.Absolute, out var uri) ? uri.Host : Model.Url) + (string.IsNullOrEmpty(Model.UpdateInterval) ? "" : $" · {Model.UpdateInterval}");
    public string RefText => BuiltinRuleSetRowViewModel.ReferenceText(Tag);
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand EditCommand { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand DeleteCommand { get; }
}
