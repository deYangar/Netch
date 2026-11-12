using System.Net;
using System.Windows;
using Netch.Localization;
using Microsoft.VisualStudio.Threading;
using Netch.Utils;

namespace Netch.Views;

/// <summary>
///     全局绕过 IP 编辑（07§D）：一行一 IP/CIDR，逐行 IPAddress.TryParse 校验，保存 TUNTAP.BypassIPs。
/// </summary>
public partial class BypassIPDialog
{
    public BypassIPDialog()
    {
        InitializeComponent();
        ipBox.Text = string.Join(Environment.NewLine, Global.Settings.TUNTAP.BypassIPs);

        saveButton.Click += (_, _) =>
        {
            var lines = ipBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            foreach (var line in lines)
            {
                if (!IsValidEntry(line))
                {
                    errorText.Text = I18N.T("bypassIp.invalidCidr") + line;
                    return;
                }
            }

            Global.Settings.TUNTAP.BypassIPs = lines;
            Configuration.SaveAsync().Forget();
            Close();
        };

        cancelButton.Click += (_, _) => Close();
    }

    /// <summary>行格式：纯 IP 或 ip/prefix（旧版语义：IPAddress + 前缀长度合成 CIDR）</summary>
    private static bool IsValidEntry(string line)
    {
        var slash = line.IndexOf('/');
        var ipPart = slash < 0 ? line : line[..slash];
        if (!System.Net.IPAddress.TryParse(ipPart, out var ip))
            return false;

        if (slash < 0)
            return true;

        var maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return byte.TryParse(line[(slash + 1)..], out var prefix) && prefix <= maxPrefix;
    }
}
