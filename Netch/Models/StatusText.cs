using Netch.Utils;
using Netch.Localization;

namespace Netch.Models;

public static class StatusPortInfoText
{
    private static ushort? _socks5Port;
    private static ushort? _httpPort;
    private static bool _shareLan;

    public static ushort HttpPort
    {
        set => _httpPort = value;
    }

    public static ushort Socks5Port
    {
        set => _socks5Port = value;
    }

    public static string Value
    {
        get
        {
            var strings = new List<string>();

            if (_socks5Port != null)
                strings.Add($"Socks5 {I18N.T("main.localPort") + I18N.T("common.colon")}{_socks5Port}");

            if (_httpPort != null)
                strings.Add($"HTTP {I18N.T("main.localPort") + I18N.T("common.colon")}{_httpPort}");

            if (!strings.Any())
                return string.Empty;

            return $" ({(_shareLan ? I18N.T("main.allowOtherDevicesConnect") + " " : "")}{string.Join(" | ", strings)})";
        }
    }

    public static void UpdateShareLan()
    {
        _shareLan = Global.Settings.LocalAddress != "127.0.0.1";
    }

    public static void Reset()
    {
        _httpPort = _socks5Port = null;
    }
}