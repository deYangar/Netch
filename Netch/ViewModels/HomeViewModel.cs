using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Application = System.Windows.Application;
using Netch.Controllers;
using Netch.Enums;
using Netch.Interfaces;
using Netch.Localization;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Services;
using Netch.UI.Hub;
using Netch.Utils;

namespace Netch.ViewModels;

/// <summary>
///     主页 VM（04§5）：服务器卡（全量行 + 过滤视图 + 索引兼容层）、模式卡、Profile 槽位。
///     选中=当前服务器/模式（对象语义，写入时回写 Settings 索引保持 settings.json 兼容）。
///     编辑器过渡期开旧 WinForms 窗体（ProcessForm/RouteForm，07 换 WPF 编辑器）。
/// </summary>
[Fody.ConfigureAwait(true)]  // AsyncRelayCommand 完成回调须回 UI 线程刷新 CanExecute（Fody 全局 false 会把 continuation 丢线程池崩 UI）
public partial class HomeViewModel : ObservableObject
{
    private readonly Func<State> _stateGetter;
    private MainViewModel? _main;

    public HomeViewModel(Func<State> stateGetter)
    {
        _stateGetter = stateGetter;

        ReloadServers();
        RebuildModes();
        ReloadProfiles();

        UIEvents.ServersChanged += OnServersChanged;
        UIEvents.ModesChanged += OnModesChanged;
        UIEvents.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>语言切换：模式列表显示名（i18NRemark）与服务器类型过滤重建</summary>
    private void OnLanguageChanged()
    {
        RebuildModes();
        RebuildServerTypes();
        OnPropertyChanged(nameof(AddableServerMenus));
    }

    /// <summary>宿主 MainViewModel 注入（构造后立刻赋值；Profile 激活的 Stop+Start 编排走它）。
    /// 必须 public：HomeView 以 {Binding Main.Xxx} 绑定此属性，WPF 绑定引擎（TypeDescriptor）只见
    /// public 成员——internal 时横幅可见性/文本/命令三处绑定全部静默失败（横幅常显、文本空、按钮无命令）</summary>
    public MainViewModel Main
    {
        get => _main ?? throw new InvalidOperationException("HomeViewModel.Main not attached");
        set => _main = value;
    }

    private State State => _stateGetter();

    /// <summary>启动中禁改配置（平移旧 StartDisableItems：服务器/模式增删改、导入）</summary>
    private bool CanEditConfig => MainViewModel.IsWaiting(State);

    /// <summary>咩咩 2026-10-31 反馈①：运行中切换节点/模式不生效（内核只在启动时用当时选中值建配置），
    /// 选择控件须锁死防误解。节点列表锁「选中」（行内测速/复制链接不受影响），模式下拉整锁</summary>
    public bool CanSelectServer => MainViewModel.IsWaiting(State);

    /// <summary>同 CanSelectServer：运行中锁模式下拉（IsEnabled 整锁）</summary>
    public bool CanSelectMode => MainViewModel.IsWaiting(State);

    /// <summary>Profile 操作（平移旧 ProfileGroupBox.Enabled：Starting/Stopping/Degraded 禁）</summary>
    private bool CanOperateProfile => State is State.Waiting or State.Stopped or State.Started;

    /// <summary>MainViewModel 状态变化时刷新全部命令 CanExecute</summary>
    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSelectServer));
        OnPropertyChanged(nameof(CanSelectMode));
        EditServerCommand.NotifyCanExecuteChanged();
        DeleteServerCommand.NotifyCanExecuteChanged();
        AddServerCommand.NotifyCanExecuteChanged();
        ImportFromClipboardCommand.NotifyCanExecuteChanged();
        TestAllDelayCommand.NotifyCanExecuteChanged();
        TestOneDelayCommand.NotifyCanExecuteChanged();
        EditModeCommand.NotifyCanExecuteChanged();
        DeleteModeCommand.NotifyCanExecuteChanged();
        NewProcessModeCommand.NotifyCanExecuteChanged();
        NewRouteModeCommand.NotifyCanExecuteChanged();
        SelectProfileCommand.NotifyCanExecuteChanged();
        AddProfileCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        MoveProfileUpCommand.NotifyCanExecuteChanged();
        MoveProfileDownCommand.NotifyCanExecuteChanged();
    }

    #region 服务器

    /// <summary>全量服务器行</summary>
    public ObservableCollection<ServerRowViewModel> Servers { get; } = new();

    /// <summary>过滤视图（重建集合，不用 CollectionView 避免线程问题）</summary>
    public ObservableCollection<ServerRowViewModel> FilteredServers { get; } = new();

    /// <summary>类型过滤项：「全部」+ 全量类型去重</summary>
    public ObservableCollection<string> ServerTypes { get; } = new();

    /// <summary>添加服务器菜单：11 协议按 Priority 排序（平移旧 AddAddServerToolStripMenuItems）</summary>
    public static IReadOnlyList<ServerMenuItemViewModel> AddableServerMenus { get; } = ServerHelper.ServerUtilDictionary.Values
        .OrderBy(i => i.Priority)
        .Where(i => !string.IsNullOrEmpty(i.FullName))
        .Select(i => new ServerMenuItemViewModel(i))
        .ToList();

    [ObservableProperty]
    private ServerRowViewModel? _selectedServer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _serverTypeFilter;

    partial void OnSelectedServerChanged(ServerRowViewModel? value)
    {
        // 对象选中 → 回写索引（settings.json 兼容层）。
        // null 不回写：过滤重建集合时 ListBox 会先弹 SelectedItem 为 null，这里保持旧索引，
        // 由 RebuildFiltered 恢复对象选中后再回写正确索引（否则删搜索词=丢失上次选择）
        if (value == null)
            return;

        Global.Settings.ServerComboBoxSelectedIndex = Global.Settings.Server.IndexOf(value.Model);
    }

    partial void OnSearchTextChanged(string value) => RebuildFiltered();

    partial void OnServerTypeFilterChanged(string? value) => RebuildFiltered();

    /// <summary>重载服务器列表（构造/订阅更新/导入/增删后）</summary>
    public void ReloadServers()
    {
        Servers.Clear();
        foreach (var server in Global.Settings.Server)
            Servers.Add(new ServerRowViewModel(server));

        RebuildServerTypes();
        RebuildFiltered();

        // SelectLastServer 语义平移：索引合法选索引，否则有元素选第一个
        var index = Global.Settings.ServerComboBoxSelectedIndex;
        SelectedServer = index > 0 && index < Servers.Count ? Servers[index] : Servers.FirstOrDefault();
    }

    private void RebuildServerTypes()
    {
        var current = ServerTypeFilter;
        ServerTypes.Clear();
        ServerTypes.Add(I18N.T("home.server.filterAll"));
        foreach (var type in Servers.Select(r => r.Type).Distinct().OrderBy(t => t))
            ServerTypes.Add(type);

        ServerTypeFilter = current != null && ServerTypes.Contains(current) ? current : ServerTypes[0];
    }

    private void RebuildFiltered()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;
        var typeFilter = ServerTypeFilter == I18N.T("home.server.filterAll") || ServerTypeFilter == null
            ? null
            : ServerTypeFilter;

        // 过滤重建会把 ListBox.SelectedItem 弹为 null（TwoWay 写穿源），先记住再恢复：
        // 搜索词清掉后选中对象仍在结果集里 → 高亮与索引都还原（咩咩 2026-10-28 反馈④）
        var selected = SelectedServer;

        FilteredServers.Clear();
        foreach (var row in Servers)
        {
            if (typeFilter != null && row.Type != typeFilter)
                continue;

            if (keyword.Length > 0 &&
                !row.Remark.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.Hostname.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            FilteredServers.Add(row);
        }

        // 恢复对象选中（搜索场景它必然在结果集里 → 高亮与索引一起还原；
        // 被类型过滤滤掉时 Selector 弹回 null，旧索引已由 OnSelectedServerChanged 保住）
        if (selected != null && Servers.Contains(selected))
            SelectedServer = selected;
    }

    /// <summary>1Hz 行延迟同步（MainViewModel 计时器调用；Model.Delay 被 PingAsync/SetDelay 更新后刷到行）</summary>
    public void RefreshDelays()
    {
        foreach (var row in Servers)
            row.SyncFromModel();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private async Task AddServerAsync(string? typeName)
    {
        if (typeName == null || !ServerHelper.ServerUtilDictionary.TryGetValue(typeName, out var util))
            return;

        // 06：新 ServerDialog（描述符 DataForm）接管，退役旧 WinForms 编辑窗
        var dialog = Views.ServerDialog.CreateNew(typeName);
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
        if (dialog.Saved)
        {
            ReloadServers();
            await Configuration.SaveAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private async Task ImportFromClipboardAsync()
    {
        var text = System.Windows.Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var servers = ShareLink.ParseText(text);
        foreach (var server in servers)
            server.Group = Constants.DefaultGroup;

        Global.Settings.Server.AddRange(servers);
        Global.UIHub.NotifyTip(I18N.TF("home.importDone", servers.Count));

        ReloadServers();
        await Configuration.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private async Task EditServerAsync(ServerRowViewModel? row)
    {
        if (row == null)
        {
            Global.UIHub.Alert(I18N.T("home.selectServerFirst"));
            return;
        }

        var oldRemark = row.Model.Remark;
        var dialog = Views.ServerDialog.Edit(row.Model);
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
        if (dialog.Saved)
        {
            // 低危②（2026-11-04 审查）：Profile 按备注字符串匹配 server，节点改名后引用全部静默失效；
            // 改名时联动迁移引用它的 Profile
            MigrateProfilesOnServerRename(oldRemark, row.Model);
            ReloadServers();
            await Configuration.SaveAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private async Task DeleteServerAsync(ServerRowViewModel? row)
    {
        if (row == null)
            return;

        if (!await Global.UIHub.ConfirmAsync(I18N.TF("home.deleteServer0", row.Remark)))
            return;

        Global.Settings.Server.Remove(row.Model);
        ReloadServers();
        await Configuration.SaveAsync();
    }

    [RelayCommand]
    private async Task CopyLinkAsync(ServerRowViewModel? row)
    {
        if (row == null)
            return;

        try
        {
            System.Windows.Clipboard.SetText(ShareLink.GetShareLink(row.Model));
        }
        catch (Exception)
        {
            // 剪贴板被占用（旧代码同语义：忽略）
        }

        await Task.CompletedTask;
    }

    /// <summary>单点测速：等待态 TCPing（PingAsync）；运行态走 Clash API 实测（多节点语义，MainController 同源）</summary>
    [RelayCommand]
    private async Task TestOneDelayAsync(ServerRowViewModel? row)
    {
        if (row == null)
            return;

        if (MainViewModel.IsWaiting(State))
        {
            await row.Model.PingAsync();
            row.SyncFromModel();
        }
        else
        {
            await MainController.TestNodeDelayAsync(row.Model);
            row.SyncFromModel();
        }
    }

    /// <summary>测速全部：等待态走周期测速池（DelayTestHelper）；运行态 Clash API 限流遍历
    /// （C-5：与等待态共用 16 并发池——无上限并发在数百节点下是连接风暴）</summary>
    [RelayCommand]
    private async Task TestAllDelayAsync()
    {
        if (MainViewModel.IsWaiting(State))
        {
            await DelayTestHelper.PerformTestAsync(true);
            RefreshDelays();
            return;
        }

        Global.UIHub.StatusText(I18N.T("common.testing"));
        await DelayTestHelper.PerformRunningTestAsync();
        RefreshDelays();
        Global.UIHub.StatusText(null);
    }

    /// <summary>编辑器打开期间隐藏主窗（平移旧 Hide/Show）。
    /// WinForms 模态对话框自带消息泵，直接在 UI 线程同步调用（互操作已由 App.EnableWindowsFormsInterop 开启）</summary>
    private static void WithMainWindowHidden(Action editor)
    {
        var window = Application.Current?.MainWindow;
        window?.Hide();
        try
        {
            editor();
        }
        finally
        {
            window?.Show();
        }
    }

    #endregion

    #region 模式

    public ObservableCollection<Mode> Modes { get; } = new();

    [ObservableProperty]
    private Mode? _selectedMode;

    partial void OnSelectedModeChanged(Mode? value)
    {
        try
        {
            Global.Settings.ModeComboBoxSelectedIndex = value == null ? -1 : Global.Modes.IndexOf(value);
        }
        catch
        {
            Global.Settings.ModeComboBoxSelectedIndex = 0;
        }
    }

    /// <summary>重载模式列表（ModeService 事件/编辑器保存后）。排序语义=Global.Modes（i18NRemark Ordinal，ModeService.Sort 已定）。
    ///     命名避开 VSTHRD103 的 ReloadModes/ReloadModesAsync 模式匹配</summary>
    public void RebuildModes()
    {
        // 记住选中对象：编辑保存后 Sort 只重排不重建，按对象恢复（平移旧 LoadModes）
        var selected = SelectedMode;

        Modes.Clear();
        foreach (var mode in Global.Modes)
            Modes.Add(mode);

        if (selected != null && Modes.Contains(selected))
        {
            SelectedMode = selected;
            return;
        }

        // SelectLastMode 语义平移：索引合法选索引，否则有元素选第一个
        var index = Global.Settings.ModeComboBoxSelectedIndex;
        SelectedMode = index > 0 && index < Modes.Count ? Modes[index] : Modes.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private void NewProcessMode()
    {
        var editor = Views.ModeEditor.ProcessModeEditorView.CreateNew();
        editor.Owner = Application.Current?.MainWindow;
        editor.ShowDialog();
        RebuildModes();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private void NewRouteMode()
    {
        var editor = Views.ModeEditor.RouteModeEditorView.CreateNew();
        editor.Owner = Application.Current?.MainWindow;
        editor.ShowDialog();
        RebuildModes();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private void EditMode()
    {
        if (SelectedMode is not { } mode)
        {
            Global.UIHub.Alert(I18N.T("home.selectModeFirst"));
            return;
        }

        switch (mode.Type)
        {
            case ModeType.ProcessMode:
            {
                var editor = Views.ModeEditor.ProcessModeEditorView.Edit((Models.Modes.ProcessMode.Redirector)mode);
                editor.Owner = Application.Current?.MainWindow;
                editor.ShowDialog();
                break;
            }
            case ModeType.TunMode:
            {
                var editor = Views.ModeEditor.RouteModeEditorView.Edit((Models.Modes.TunMode.TunMode)mode);
                editor.Owner = Application.Current?.MainWindow;
                editor.ShowDialog();
                break;
            }
            case ModeType.ShareMode:
            default:
                Utils.Utils.Open(mode.FullName);
                break;
        }

        RebuildModes();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private Task DeleteModeAsync()
    {
        if (SelectedMode is not { } mode)
        {
            Global.UIHub.Alert(I18N.T("home.selectModeFirst"));
            return Task.CompletedTask;
        }

        return DeleteModeCoreAsync(mode);
    }

    private async Task DeleteModeCoreAsync(Mode mode)
    {
        if (!await Global.UIHub.ConfirmAsync(I18N.TF("home.deleteMode0", mode.i18NRemark)))
            return;

        ModeService.Delete(mode);
        RebuildModes();
    }

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private Task ReloadModesAsync()
    {
        ModeService.Instance.Load();
        return Task.CompletedTask;
    }

    #endregion

    #region Profile（快速配置槽位）

    /// <summary>槽位视图：Index 0..ProfileCount-1，空槽 Profile=null</summary>
    public ObservableCollection<ProfileSlotViewModel> ProfileSlots { get; } = new();

    /// <summary>当前激活槽（点击激活后描边标记）</summary>
    [ObservableProperty]
    private ProfileSlotViewModel? _activeProfile;

    /// <summary>顶栏 Profile ComboBox 选中项（与 ActiveProfile 双向联动：下拉选择即激活）</summary>
    [ObservableProperty]
    private ProfileSlotViewModel? _profileComboSelection;

    partial void OnProfileComboSelectionChanged(ProfileSlotViewModel? value)
    {
        if (value != null && !ReferenceEquals(value, ActiveProfile))
            SelectProfileCommand.Execute(value);
    }

    partial void OnActiveProfileChanged(ProfileSlotViewModel? value)
    {
        foreach (var slot in ProfileSlots)
            slot.NotifyActivationChanged();

        // 直接写字段避免重入（SelectProfileCommand 路径会再进 OnProfileComboSelectionChanged）
        SetProperty(ref _profileComboSelection, value, nameof(ProfileComboSelection));
    }

    public void ReloadProfiles()
    {
        ProfileSlots.Clear();
        for (var i = 0; i < Global.Settings.ProfileCount; i++)
            ProfileSlots.Add(new ProfileSlotViewModel(i, Global.Settings.Profiles.SingleOrDefault(p => p.Index == i), this));
    }

    /// <summary>激活 Profile（平移 ProfileButton_Click）：按备注找 server/mode → 选中 → Stop → Start</summary>
    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private async Task SelectProfileAsync(ProfileSlotViewModel? slot)
    {
        if (slot?.Profile is not { } profile)
        {
            Global.UIHub.Alert(I18N.T("home.noSavedProfileHereSave"));
            return;
        }

        var server = Servers.Select(r => r.Model).FirstOrDefault(s => s.Remark.Equals(profile.ServerRemark));
        var mode = Modes.FirstOrDefault(m => m.Remark.Values.Contains(profile.ModeRemark));

        if (server == null)
        {
            Global.UIHub.Alert("Server not found.", DialogLevel.Error);
            return;
        }

        if (mode == null)
        {
            Global.UIHub.Alert("Mode not found.", DialogLevel.Error);
            return;
        }

        // 对象选中（经 OnSelectedXxxChanged 回写索引）
        SelectedServer = Servers.FirstOrDefault(r => ReferenceEquals(r.Model, server));
        SelectedMode = Modes.Contains(mode) ? mode : Modes.FirstOrDefault();
        ActiveProfile = slot;

        await Main.ActivateProfileAsync(server, mode);
    }

    /// <summary>新建 Profile：存当前 server+mode 到第一个空槽（满则追加），名称经 InputDialog 输入</summary>
    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private async Task AddProfileAsync()
    {
        if (SelectedServer?.Model is not { } server)
        {
            Global.UIHub.Alert(I18N.T("home.selectServerFirst"));
            return;
        }

        if (SelectedMode is not { } mode)
        {
            Global.UIHub.Alert(I18N.T("home.selectModeFirst"));
            return;
        }

        var name = Views.InputDialog.Show(
            I18N.T("main.profile.newTitle"),
            I18N.T("main.profile.namePrompt"),
            server.Remark);
        if (name == null)
            return;

        // 第一个空槽；全满则追加（Index=ProfileCount，同时扩 ProfileCount 保证槽位显示）
        var used = Global.Settings.Profiles.Select(p => p.Index).ToHashSet();
        var index = -1;
        for (var i = 0; i < Global.Settings.ProfileCount; i++)
        {
            if (!used.Contains(i))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            index = Global.Settings.ProfileCount;
            Global.Settings.ProfileCount++;
        }

        Global.Settings.Profiles.RemoveAll(p => p.Index == index);
        Global.Settings.Profiles.Add(new Profile(server, mode, name, index));

        ReloadProfiles();
        ActiveProfile = ProfileSlots.FirstOrDefault(s => s.Index == index);
        await Configuration.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private async Task RenameProfileAsync(ProfileSlotViewModel? slot)
    {
        if (slot?.Profile is not { } profile)
            return;

        var name = Views.InputDialog.Show(
            I18N.T("main.profile.renameTitle"),
            I18N.T("main.profile.namePrompt"),
            profile.ProfileName);
        if (name == null)
            return;

        profile.ProfileName = name;
        slot.SyncDisplayName();
        await Configuration.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private async Task DeleteProfileAsync(ProfileSlotViewModel? slot)
    {
        if (slot?.Profile == null)
            return;

        Global.Settings.Profiles.Remove(slot.Profile);
        slot.Profile = null;
        if (ReferenceEquals(ActiveProfile, slot))
            ActiveProfile = null;

        await Configuration.SaveAsync();
    }

    /// <summary>上移（04§4.1 卡片角标操作）：与相邻槽交换 Profile 归属（Index 互换，空槽亦可交换位置）</summary>
    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private Task MoveProfileUpAsync(ProfileSlotViewModel? slot) => MoveProfileCoreAsync(slot, up: true);

    /// <summary>下移：同上移</summary>
    [RelayCommand(CanExecute = nameof(CanOperateProfile))]
    private Task MoveProfileDownAsync(ProfileSlotViewModel? slot) => MoveProfileCoreAsync(slot, up: false);

    private async Task MoveProfileCoreAsync(ProfileSlotViewModel? slot, bool up)
    {
        if (slot == null)
            return;

        var targetIndex = up ? slot.Index - 1 : slot.Index + 1;
        var target = ProfileSlots.FirstOrDefault(s => s.Index == targetIndex);
        if (target == null)
            return;

        if (slot.Profile != null)
            slot.Profile.Index = targetIndex;
        if (target.Profile != null)
            target.Profile.Index = slot.Index;

        ReloadProfiles();
        await Configuration.SaveAsync();
    }

    #endregion

    /// <summary>服务器改名后迁移 Profile 引用（Profile 按 Remark 匹配，改名不迁移=激活静默失效）</summary>
    private static void MigrateProfilesOnServerRename(string oldRemark, Server server)
    {
        if (string.Equals(oldRemark, server.Remark, StringComparison.Ordinal))
            return;

        foreach (var profile in Global.Settings.Profiles.Where(p => p.ServerRemark == oldRemark))
            profile.ServerRemark = server.Remark;
    }

    private void OnServersChanged() => ReloadServers();

    private void OnModesChanged() => RebuildModes();
}

/// <summary>服务器行（04§5）：包 Server 模型，Delay/DelaySource 驱动 DelayBadge</summary>
public partial class ServerRowViewModel(Server model) : ObservableObject
{
    public Server Model { get; } = model;

    public string Remark
    {
        get => Model.Remark;
        set => SetProperty(Model.Remark, value, Model, (m, v) => m.Remark = v);
    }

    public string Hostname => Model.Hostname;

    public string Type => Model.Type;

    /// <summary>地址显示（地址:端口）</summary>
    public string Endpoint => $"{Model.Hostname}:{Model.Port}";

    [ObservableProperty]
    private int _delay = -1;

    [ObservableProperty]
    private DelaySource _delaySource = DelaySource.None;

    /// <summary>行显示脏检查缓存（P-6②：1Hz RefreshDelays 对全部行无条件通知 Remark/Endpoint，
    /// 331 行 × 每秒两次绑定刷新纯属浪费；值没变不通知）</summary>
    private string _lastRemark = string.Empty;

    private string _lastEndpoint = string.Empty;

    private bool _dirtyChecked;

    /// <summary>编辑保存/测速后刷新行显示（Remark 等可变字段；1Hz 轮询路径复用，脏检查防无谓绑定刷新）</summary>
    public void SyncFromModel()
    {
        if (!_dirtyChecked || !string.Equals(Remark, _lastRemark, StringComparison.Ordinal))
        {
            _lastRemark = Remark;
            OnPropertyChanged(nameof(Remark));
        }

        if (!_dirtyChecked || !string.Equals(Endpoint, _lastEndpoint, StringComparison.Ordinal))
        {
            _lastEndpoint = Endpoint;
            OnPropertyChanged(nameof(Endpoint));
        }

        _dirtyChecked = true;
        Delay = Model.Delay;
        DelaySource = Model.DelaySource;
    }
}

/// <summary>添加服务器菜单项（11 协议，按 Priority）</summary>
public class ServerMenuItemViewModel(IServerUtil util)
{
    public string TypeName => util.TypeName;

    public string MenuText => I18N.TF("home.addServerFormat", util.FullName);
}

/// <summary>Profile 槽位（Index + 可空 Profile）。命令/激活态转发宿主 VM（ContextMenu 不在视觉树，绑定链经本对象闭合）</summary>
public partial class ProfileSlotViewModel(int index, Profile? profile, HomeViewModel owner) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    private Profile? _profile = profile;

    public string DisplayName => Profile?.ProfileName ?? I18N.T("main.profile.none");

    public bool HasProfile => Profile != null;

    /// <summary>激活描边（owner.ActiveProfile == 本槽）</summary>
    public bool IsActive => ReferenceEquals(owner.ActiveProfile, this);

    internal void NotifyActivationChanged() => OnPropertyChanged(nameof(IsActive));

    /// <summary>Profile 对象内部字段（名称）被修改后手动刷新显示</summary>
    public void SyncDisplayName() => OnPropertyChanged(nameof(DisplayName));

    public System.Windows.Input.ICommand SelectCommand => owner.SelectProfileCommand;

    public System.Windows.Input.ICommand RenameCommand => owner.RenameProfileCommand;

    public System.Windows.Input.ICommand DeleteCommand => owner.DeleteProfileCommand;

    public System.Windows.Input.ICommand MoveUpCommand => owner.MoveProfileUpCommand;

    public System.Windows.Input.ICommand MoveDownCommand => owner.MoveProfileDownCommand;

    partial void OnProfileChanged(Profile? value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasProfile));
    }
}
