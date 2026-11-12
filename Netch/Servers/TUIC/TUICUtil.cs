using System.Text.RegularExpressions;
using System.Web;
using Netch.Interfaces;
using Netch.Models;
using Netch.Utils;

namespace Netch.Servers;

public class TUICUtil : IServerUtil
{
    public ushort Priority { get; } = 7;

    public string TypeName { get; } = "TUIC";

    public string FullName { get; } = "TUIC";

    public string ShortName { get; } = "TU";

    public string[] UriScheme { get; } = { "tuic" };

    public Type ServerType { get; } = typeof(TUICServer);

    public void Edit(Server s)
    {
        var dialog = Netch.Views.ServerDialog.Edit(s);
        dialog.ShowDialog();
    }

    public void Create()
    {
        var dialog = Netch.Views.ServerDialog.CreateNew(TypeName);
        dialog.ShowDialog();
    }

    public string GetShareLink(Server s)
    {
        var server = (TUICServer)s;

        var query = new List<string> { $"congestion_control={server.CongestionControl}", $"udp_relay_mode={server.UdpRelayMode}" };
        if (!server.Host.IsNullOrWhiteSpace())
            query.Add($"sni={HttpUtility.UrlEncode(server.Host)}");

        if (!server.Alpn.IsNullOrWhiteSpace())
            query.Add($"alpn={HttpUtility.UrlEncode(server.Alpn)}");

        return $"tuic://{server.Uuid}:{HttpUtility.UrlEncode(server.Password)}@{server.Hostname}:{server.Port}?{string.Join("&", query)}#{HttpUtility.UrlEncode(server.Remark)}";
    }

    public IServerController GetController()
    {
        return new SingboxController();
    }

    public IEnumerable<Server> ParseUri(string text)
    {
        var data = new TUICServer();

        if (text.Contains('#'))
        {
            data.Remark = HttpUtility.UrlDecode(text.Split('#')[1]);
            text = text.Split('#')[0];
        }

        if (text.Contains('?'))
        {
            var query = HttpUtility.ParseQueryString(new Uri(text).Query);

            var sni = HttpUtility.UrlDecode(query.Get("sni"));
            if (sni != null)
                data.Host = sni;

            var congestionControl = query.Get("congestion_control");
            if (congestionControl != null && TUICGlobal.CongestionControls.Contains(congestionControl))
                data.CongestionControl = congestionControl;

            var udpRelayMode = query.Get("udp_relay_mode");
            if (udpRelayMode != null && TUICGlobal.UdpRelayModes.Contains(udpRelayMode))
                data.UdpRelayMode = udpRelayMode;

            var alpn = HttpUtility.UrlDecode(query.Get("alpn"));
            if (alpn != null)
                data.Alpn = alpn;

            text = text.Split('?')[0];
        }

        var finder = new Regex(@"^tuic://(?<uuid>.+?):(?<password>.+?)@(?<server>.+):(?<port>\d+)");
        var match = finder.Match(text);
        if (!match.Success)
            throw new FormatException();

        data.Uuid = HttpUtility.UrlDecode(match.Groups["uuid"].Value);
        data.Password = HttpUtility.UrlDecode(match.Groups["password"].Value);
        data.Hostname = match.Groups["server"].Value;
        data.Port = ushort.Parse(match.Groups["port"].Value);

        return new[] { data };
    }

    public bool CheckServer(Server s)
    {
        return true;
    }
}
