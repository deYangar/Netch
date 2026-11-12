using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Localization;
using Netch.Services;
using Netch.Utils;

namespace Netch.ViewModels;

/// <summary>
///     进程模式编辑器 VM（07§A1 三段式：参数区/进程列表/工具行）。字段绑定 Redirector 模型；
///     保存写 mode\Custom\&lt;name&gt;.json（M2-4 JSON 格式）+ ModeService 重载 + 选中语义。
/// </summary>
public partial class ProcessModeEditorViewModel : ObservableObject
{
    private readonly Redirector? _editing;

    public ProcessModeEditorViewModel(Redirector? editing)
    {
        _editing = editing;
        if (editing != null)
        {
            Remark = editing.i18NRemark;
            HandleTCP = editing.FilterTCP;
            HandleUDP = editing.FilterUDP;
            HandleICMP = editing.FilterICMP;
            HandleDNS = editing.FilterDNS;
            HandleProcDNS = editing.HandleOnlyDNS;
            DNSProxy = editing.DNSProxy;
            HandleChildProcess = editing.FilterParent;
            ICMPDelay = editing.ICMPDelay ?? 10;
            Handle = new ObservableCollection<string>(editing.Handle);
            Rules = new ObservableCollection<RouteRule>(editing.Rules ?? []);
        }
        else
        {
            Remark = "";
            HandleTCP = HandleUDP = HandleDNS = HandleProcDNS = DNSProxy = true;
            HandleICMP = false;
            ICMPDelay = 10;
            Handle = new ObservableCollection<string>();
            Rules = new ObservableCollection<RouteRule>();
        }
        RulesEditor = new RulesEditorViewModel(Rules);
    }

    [ObservableProperty]
    private string _remark = "";

    /// <summary>三态说明（null=跟随全局设置，提示各 Handle 项的当前全局值）</summary>
    public string GlobalHint =>
        I18N.TF("modeEditor.globalHint",
            I18N.T(Global.Settings.Redirector.FilterTCP == true ? "common.on" : "common.off"));

    [ObservableProperty]
    private bool? _handleTCP;

    [ObservableProperty]
    private bool? _handleUDP;

    [ObservableProperty]
    private bool? _handleICMP;

    [ObservableProperty]
    private bool? _handleDNS;

    [ObservableProperty]
    private bool? _handleProcDNS;

    [ObservableProperty]
    private bool? _dNSProxy;

    [ObservableProperty]
    private bool? _handleChildProcess;

    [ObservableProperty]
    private int _iCMPDelay;

    [ObservableProperty]
    private string _newProcessName = "";

    public ObservableCollection<string> Handle { get; }

    public ObservableCollection<RouteRule> Rules { get; }

    public RulesEditorViewModel RulesEditor { get; }

    [ObservableProperty]
    private string? _ruleError;

    [ObservableProperty]
    private string? _selectedProcess;

    public bool Saved { get; private set; }

    [RelayCommand]
    private void AddProcess()
    {
        var name = NewProcessName.Trim();
        if (name.Length == 0 || Handle.Contains(name))
            return;

        Handle.Add(name);
        NewProcessName = "";
    }

    [RelayCommand]
    private void RemoveProcess()
    {
        if (SelectedProcess != null)
            Handle.Remove(SelectedProcess);
    }

    [RelayCommand]
    private void MoveUp()
    {
        Move(-1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        Move(1);
    }

    private void Move(int delta)
    {
        if (SelectedProcess == null)
            return;

        var index = Handle.IndexOf(SelectedProcess);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Handle.Count)
            return;

        Handle.Move(index, target);
    }

    [RelayCommand]
    private void ClearProcesses()
    {
        Handle.Clear();
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Remark))
            return;

        RuleError = RulesEditor.CommitAndValidate();
        if (RuleError != null)
            return;

        var mode = _editing ?? new Redirector();
        mode.i18NRemark = Remark;
        mode.FilterTCP = HandleTCP;
        mode.FilterUDP = HandleUDP;
        mode.FilterICMP = HandleICMP;
        mode.FilterDNS = HandleDNS;
        mode.HandleOnlyDNS = HandleProcDNS;
        mode.DNSProxy = DNSProxy;
        mode.FilterParent = HandleChildProcess;
        mode.ICMPDelay = ICMPDelay;
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
