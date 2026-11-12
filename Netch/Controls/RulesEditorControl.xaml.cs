using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Netch.Models.Modes;
using Netch.ViewModels;

namespace Netch.Controls;

/// <summary>模式规则共用编辑控件（RE-3§4.1）</summary>
public partial class RulesEditorControl : UserControl
{
    public static readonly DependencyProperty RulesProperty = DependencyProperty.Register(nameof(Rules),
        typeof(ObservableCollection<RouteRule>), typeof(RulesEditorControl),
        new PropertyMetadata(null, (s, _) => ((RulesEditorControl)s).BindEditor()));

    public static readonly DependencyProperty RulesErrorProperty = DependencyProperty.Register(nameof(RulesError),
        typeof(string), typeof(RulesEditorControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ObservableCollection<RouteRule> Rules
    {
        get => (ObservableCollection<RouteRule>)GetValue(RulesProperty);
        set => SetValue(RulesProperty, value);
    }

    public string? RulesError
    {
        get => (string?)GetValue(RulesErrorProperty);
        set => SetValue(RulesErrorProperty, value);
    }

    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor),
        typeof(RulesEditorViewModel), typeof(RulesEditorControl),
        new PropertyMetadata(null, (s, _) => ((RulesEditorControl)s).BindEditor()));

    public RulesEditorViewModel? Editor
    {
        get => (RulesEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public RulesEditorControl()
    {
        InitializeComponent();
        BindEditor();
    }

    private void BindEditor()
    {
        var rulesBinding = System.Windows.Data.BindingOperations.GetBinding(this, RulesProperty);
        var editorBinding = System.Windows.Data.BindingOperations.GetBinding(this, EditorProperty);
        // 绑定求值期间先稳定内部 DataContext，不能提前 return 让按钮绑到宿主页面 VM。
        // 宿主绑定 Editor 时，两个依赖属性最终会指向同一个 Rules 实例。
        if (Editor != null && Rules != null && !ReferenceEquals(Editor.Rules, Rules))
        {
            if (editorBinding != null)
                throw new InvalidOperationException("Rules 与 Editor 必须引用同一个规则集合");
            Editor = null;
        }

        if (Editor == null && Rules != null && editorBinding == null)
            SetCurrentValue(EditorProperty, new RulesEditorViewModel(Rules));
        if (Editor != null)
            editorRoot.DataContext = Editor;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Editor?.SelectedEditable != null)
            Editor.SelectedEditable.SearchText = ruleSetSearch.Text;
    }

    private void AddDimension_Click(object sender, RoutedEventArgs e)
    {
        Editor?.SelectedEditable?.Dimensions.Add(new DimensionRowViewModel());
    }

    private void DeleteDimension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DimensionRowViewModel row })
            Editor?.SelectedEditable?.Dimensions.Remove(row);
    }

    private void Value_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DimensionRowViewModel row })
            RulesError = row.Validate();
    }
}

