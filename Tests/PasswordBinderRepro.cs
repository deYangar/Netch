using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netch.Controls.DataForm;

namespace Tests;

/// <summary>
///     B1 实机发现的 PasswordBox 写回问题复现：打字进 PasswordBox 后 FieldRow.Value 应同步（必填校验应通过）。
/// </summary>
[TestClass]
public class PasswordBinderRepro
{
    private static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw error;
    }

    [TestMethod]
    public void PasswordChanged_Should_WriteBack_To_BoundValue()
    {
        RunInSta(() =>
        {
            var box = new PasswordBox();
            var vm = new FieldRowVm();
            var binding = new Binding("Value")
            {
                Mode = BindingMode.TwoWay,
                Source = vm
            };
            BindingOperations.SetBinding(box, PasswordBinder.BoundValueProperty, binding);

            // 模拟用户打字：Password 属性 setter 触发 PasswordChanged
            box.Password = "testpass123";

            Assert.AreEqual("testpass123", vm.Value);
        });
    }

    [TestMethod]
    public void BoundValue_Should_PushDown_To_PasswordBox()
    {
        RunInSta(() =>
        {
            var box = new PasswordBox();
            var vm = new FieldRowVm();
            var binding = new Binding("Value")
            {
                Mode = BindingMode.TwoWay,
                Source = vm
            };
            BindingOperations.SetBinding(box, PasswordBinder.BoundValueProperty, binding);

            vm.Value = "abc";

            Assert.AreEqual("abc", box.Password);
        });
    }

    [TestMethod]
    public void DataForm_Row_Password_WriteBack_Via_RowValue()
    {
        RunInSta(() =>
        {
            // 走 FieldRowViewModel 的真实链路：模板里 BoundValue 绑定 Row.Value
            var server = new Netch.Servers.ShadowsocksServer();
            var descriptor = F.Password("Password", s => ((Netch.Servers.ShadowsocksServer)s).Password,
                (s, v) => ((Netch.Servers.ShadowsocksServer)s).Password = (string)v!,
                v => string.IsNullOrWhiteSpace(v) ? F.ErrorRequired : null);
            var row = new FieldRowViewModel(descriptor, server);

            var box = new PasswordBox();
            var binding = new Binding("Value")
            {
                Mode = BindingMode.TwoWay,
                Source = row
            };
            BindingOperations.SetBinding(box, PasswordBinder.BoundValueProperty, binding);

            box.Password = "testpass123";

            Assert.IsNull(row.Validate());
            Assert.AreEqual("testpass123", row.Value);
        });
    }
}

/// <summary>最小可观察 VM（等价 FieldRowViewModel.Value 的行为）</summary>
public class FieldRowVm : System.ComponentModel.INotifyPropertyChanged
{
    private string? _value = "";

    public string? Value
    {
        get => _value;
        set
        {
            _value = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
