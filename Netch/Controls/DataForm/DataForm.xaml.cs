using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Media;
using Netch.Models;

namespace Netch.Controls.DataForm;

/// <summary>
///     字段描述符驱动的表单（06§A2）。Rows 由定义+Server 实例构建；Save 前 Apply 全行（有错禁用保存）。
/// </summary>
public partial class DataForm : UserControl, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnRowsChanged() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Rows)));

    public static readonly DependencyProperty DefinitionProperty = DependencyProperty.Register(
        nameof(Definition), typeof(ServerFormDefinition), typeof(DataForm),
        new PropertyMetadata(null, (d, _) => ((DataForm)d).Rebuild()));

    public static readonly DependencyProperty ServerProperty = DependencyProperty.Register(
        nameof(Server), typeof(Server), typeof(DataForm),
        new PropertyMetadata(null, (d, _) => ((DataForm)d).Rebuild()));

    public ServerFormDefinition? Definition
    {
        get => (ServerFormDefinition?)GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    public Server? Server
    {
        get => (Server?)GetValue(ServerProperty);
        set => SetValue(ServerProperty, value);
    }

    public IReadOnlyList<FieldRowViewModel> Rows { get; private set; } = Array.Empty<FieldRowViewModel>();

    public DataForm()
    {
        InitializeComponent();
    }

    private void Rebuild()
    {
        if (Definition == null || Server == null)
        {
            Rows = Array.Empty<FieldRowViewModel>();
            return;
        }

        Rows = Definition.Fields.Select(f => new FieldRowViewModel(f, Server)).ToList();
        OnRowsChanged();
    }

    /// <summary>全行校验：任一错误返回 false（Save 按钮 CanExecute 用）</summary>
    public bool ValidateAll() => Rows.All(r => r.Validate() == null);

    /// <summary>全行写回模型；任一错误返回 false 不写</summary>
    public bool ApplyAll(Server target)
    {
        if (!ValidateAll())
            return false;

        foreach (var row in Rows)
            row.Apply(target);
        return true;
    }
}

/// <summary>按 FieldType 选模板（Notice=灰 Caption 段，Check=CheckBox，Select=ComboBox，Password=PasswordBox，其余 TextBox）</summary>
public class FieldTypeTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not FieldRowViewModel row || container is not FrameworkElement fe)
            return null;

        var key = row.Type switch
        {
            FieldType.Notice => "FieldRow.Notice",
            FieldType.Check => "FieldRow.Check",
            FieldType.Select => "FieldRow.Select",
            FieldType.Password => "FieldRow.Password",
            _ => "FieldRow.Text"
        };

        return fe.TryFindResource(key) as DataTemplate;
    }
}
