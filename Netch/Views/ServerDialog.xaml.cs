using System.ComponentModel;
using System.Windows;
using Microsoft.VisualStudio.Threading;
using Netch.Controls.DataForm;
using Netch.Localization;
using Netch.Models;
using Netch.UI.Hub;
using Netch.Utils;

namespace Netch.Views;

/// <summary>
///     服务器编辑器（06§A4）：新建=追加+保存；编辑=回写。DataForm 校验有错时 Save 禁用（行校验联动）；
///     保存即关窗（行为变化：去掉旧「Saved」弹窗）。平移旧 IServerUtil.Create()/Edit(s) 的职责。
/// </summary>
public partial class ServerDialog
{
    private readonly Server _server;
    private readonly bool _isNew;

    /// <summary>保存是否已生效</summary>
    public bool Saved { get; private set; }

    private ServerDialog(Server server, bool isNew)
    {
        _server = server;
        _isNew = isNew;
        InitializeComponent();

        Title = $"{I18N.T("serverForm.title")} {ServerHelper.GetUtilByTypeName(server.Type).FullName}";
        form.Definition = ServerFormRegistry.All[server.Type];
        form.Server = server;

        // 行校验联动 Save 启用（Rows 换代后重新订阅）
        form.PropertyChanged += OnFormRowsChanged;
        HookRows();

        saveButton.Click += (_, _) =>
        {
            if (!form.ApplyAll(_server))
                return;

            Saved = true;
            if (_isNew)
            {
                Global.Settings.Server.Add(_server);
                UIEvents.RaiseServersChanged();
            }

            Configuration.SaveAsync().Forget();
            Close();
        };

        cancelButton.Click += (_, _) => Close();
    }

    private void OnFormRowsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataForm.Rows))
            HookRows();
    }

    private void HookRows()
    {
        saveButton.IsEnabled = form.ValidateAll();
        foreach (var row in form.Rows)
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(FieldRowViewModel.Value) or nameof(FieldRowViewModel.ErrorMessage))
                    saveButton.IsEnabled = form.ValidateAll();
            };
    }

    /// <summary>新建：按协议类型实例化（旧 IServerUtil.Create 的 WPF 版；Group 语义=DefaultGroup）</summary>
    public static ServerDialog CreateNew(string typeName)
    {
        var server = (Server)Activator.CreateInstance(ServerHelper.GetTypeByTypeName(typeName))!;
        server.Group = Constants.DefaultGroup;
        return new ServerDialog(server, isNew: true);
    }

    /// <summary>编辑（平移旧 IServerUtil.Edit）</summary>
    public static ServerDialog Edit(Server server)
    {
        return new ServerDialog(server, isNew: false);
    }
}
