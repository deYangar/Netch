using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Models.Modes;
using Netch.Models.Modes.TunMode;
using Netch.Services;
using Netch.Utils;

namespace Netch.ViewModels;

/// <summary>路由模式编辑器：共用规则控件 + 保留旧 Handle-only 数据（RE-3§4.4）</summary>
public partial class RouteModeEditorViewModel : ObservableObject
{
    private readonly TunMode? _editing;

    public RouteModeEditorViewModel(TunMode? editing)
    {
        _editing = editing;
        Remark = editing?.i18NRemark ?? "";
        Handle = new ObservableCollection<string>(editing?.Handle ?? []);
        Rules = new ObservableCollection<RouteRule>(editing?.Rules ?? []);
        RulesEditor = new RulesEditorViewModel(Rules);
    }

    [ObservableProperty]
    private string _remark = "";

    public ObservableCollection<string> Handle { get; }
    public ObservableCollection<RouteRule> Rules { get; }
    public RulesEditorViewModel RulesEditor { get; }

    [ObservableProperty]
    private string? _ruleError;

    public bool Saved { get; private set; }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Remark))
            return;
        RuleError = RulesEditor.CommitAndValidate();
        if (RuleError != null)
            return;

        var mode = _editing ?? new TunMode();
        mode.i18NRemark = Remark;
        mode.Handle = Handle.ToList();
        mode.Rules = Rules.Count > 0 ? Rules.ToList() : null;
        if (_editing == null)
        {
            if (File.Exists(ModeService.Instance.GetFullPath(ModeEditorUtils.GetCustomModeRelativePath(Remark))))
            {
                Global.UIHub.Alert(Netch.Localization.I18N.T("modeEditor.fileExists"));
                return;
            }
            mode.FullName = ModeService.Instance.GetFullPath(ModeEditorUtils.GetCustomModeRelativePath(Remark));
            ModeService.Instance.Add(mode);
        }
        else
        {
            mode.WriteFile();
        }
        Global.Settings.ModeComboBoxSelectedIndex = Global.Modes.IndexOf(mode);
        ModeService.Instance.Load();
        Saved = true;
    }
}
