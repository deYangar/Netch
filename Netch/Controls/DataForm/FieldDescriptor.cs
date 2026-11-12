using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Netch.Localization;
using Netch.Models;

namespace Netch.Controls.DataForm;

/// <summary>一行字段类型（06§A1）</summary>
public enum FieldType
{
    Text,
    Password,
    Select,
    Check,
    Notice
}

/// <summary>一行字段。Getter/Setter 直连 Server 模型属性（与旧 xxForm.CreateTextBox(cb, save) 一一对应）</summary>
public sealed record FieldDescriptor
{
    public required string Key { get; init; }

    /// <summary>i18n key: serverForm.<proto>.<key></summary>
    public required FieldType Type { get; init; }

    public Func<Server, object?>? Getter { get; init; }

    public Action<Server, object?>? Setter { get; init; }

    /// <summary>Select 选项（静态表）</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>校验：返回 null=合法，否则=错误文案 key</summary>
    public Func<string?, string?>? Validator { get; init; }

    /// <summary>占列宽权重（Password:Port 类同行布局用）</summary>
    public double WidthWeight { get; init; } = 1;

    public string Label => I18N.T($"serverForm.{Key}");
}

/// <summary>协议表单定义：基类三字段（备注/地址:端口 同行）+ 协议字段</summary>
public sealed class ServerFormDefinition
{
    public required string ProtocolType { get; init; }

    public required IReadOnlyList<FieldDescriptor> Fields { get; init; }
}

/// <summary>描述符构造糖（06§A3 示例的 F.Text/F.Select 形态）</summary>
public static class F
{
    public const string ErrorRequired = "serverForm.error.required";
    public const string ErrorPort = "serverForm.error.port";
    public const string ErrorInt = "serverForm.error.int";

    public static FieldDescriptor Text(string key, Func<Server, object?> get, Action<Server, object?> set,
        Func<string?, string?>? validator = null, double widthWeight = 1)
        => new() { Key = key, Type = FieldType.Text, Getter = get, Setter = set, Validator = validator, WidthWeight = widthWeight };

    public static FieldDescriptor Password(string key, Func<Server, object?> get, Action<Server, object?> set,
        Func<string?, string?>? validator = null, double widthWeight = 1)
        => new() { Key = key, Type = FieldType.Password, Getter = get, Setter = set, Validator = validator, WidthWeight = widthWeight };

    public static FieldDescriptor Select(string key, Func<Server, object?> get, Action<Server, object?> set,
        IReadOnlyList<string> options)
        => new() { Key = key, Type = FieldType.Select, Getter = get, Setter = set, Options = options };

    public static FieldDescriptor Check(string key, Func<Server, object?> get, Action<Server, object?> set)
        => new() { Key = key, Type = FieldType.Check, Getter = get, Setter = set };

    public static FieldDescriptor Notice(string key)
        => new() { Key = key, Type = FieldType.Notice };

    /// <summary>端口校验（ushort 且非 0）</summary>
    public static string? ValidatePort(string? value)
        => ushort.TryParse(value, out var port) && port != 0 ? null : ErrorPort;

    /// <summary>整数校验</summary>
    public static string? ValidateInt(string? value)
        => int.TryParse(value, out _) ? null : ErrorInt;
}

/// <summary>行 VM：值/错误状态驱动编辑器模板（红描边 + Caption 错误文本，Save CanExecute 联动）</summary>
public partial class FieldRowViewModel(FieldDescriptor descriptor, Server server) : ObservableObject
{
    public FieldDescriptor Descriptor { get; } = descriptor;

    public string Label => Descriptor.Label;

    public FieldType Type => Descriptor.Type;

    public IReadOnlyList<string>? Options => Descriptor.Options;

    private string? _value = descriptor.Getter?.Invoke(server)?.ToString() ?? "";

    public string? Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
                Validate();
        }
    }

    private bool? _checkValue;

    public bool CheckValue
    {
        get => _checkValue ??= Value == "true";
        set => SetProperty(ref _checkValue, value);
    }

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => ErrorMessage != null;

    /// <summary>失焦/变更即校验；合法返回 null，错误返回文案（已本地化）</summary>
    public string? Validate()
    {
        var error = Type == FieldType.Notice ? null : Descriptor.Validator?.Invoke(Value);
        ErrorMessage = error == null ? null : I18N.T(error);
        OnPropertyChanged(nameof(HasError));
        return ErrorMessage;
    }

    /// <summary>合法则写回模型；返回 false=有错不写</summary>
    public bool Apply(Server target)
    {
        if (Type == FieldType.Notice)
            return true;

        if (Validate() != null)
            return false;

        Descriptor.Setter?.Invoke(target, Value);
        return true;
    }
}
