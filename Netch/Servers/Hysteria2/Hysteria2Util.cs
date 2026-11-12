using System.Text.RegularExpressions;
using System.Web;
using Netch.Interfaces;
using Netch.Models;
using Netch.Utils;

namespace Netch.Servers;

public class Hysteria2Util : IServerUtil
{
    public ushort Priority { get; } = 6;

    public string TypeName { get; } = "Hysteria2";

    public string FullName { get; } = "Hysteria2";

    public string ShortName { get; } = "H2";

    public string[] UriScheme { get; } = { "hysteria2", "hy2" };

    public Type ServerType { get; } = typeof(Hysteria2Server);

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
        var server = (Hysteria2Server)s;

        var query = new List<string>();
        if (!server.Host.IsNullOrWhiteSpace())
            query.Add($"sni={HttpUtility.UrlEncode(server.Host)}");

        if (server.UpMbps > 0)
            query.Add($"up={server.UpMbps}");

        if (server.DownMbps > 0)
            query.Add($"down={server.DownMbps}");

        if (!server.ObfsPassword.IsNullOrWhiteSpace())
        {
            query.Add("obfs=salamander");
            query.Add($"obfs-password={HttpUtility.UrlEncode(server.ObfsPassword)}");
        }

        var queryString = query.Any() ? $"?{string.Join("&", query)}" : "";

        return $"hysteria2://{HttpUtility.UrlEncode(server.Password)}@{server.Hostname}:{server.Port}{queryString}#{HttpUtility.UrlEncode(server.Remark)}";
    }

    public IServerController GetController()
    {
        return new SingboxController();
    }

    public IEnumerable<Server> ParseUri(string text)
    {
        var data = new Hysteria2Server();

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

            var up = query.Get("up");
            if (up != null && int.TryParse(up, out var upMbps))
                data.UpMbps = upMbps;

            var down = query.Get("down");
            if (down != null && int.TryParse(down, out var downMbps))
                data.DownMbps = downMbps;

            var obfsPassword = HttpUtility.UrlDecode(query.Get("obfs-password"));
            if (obfsPassword != null)
                data.ObfsPassword = obfsPassword;

            text = text.Split('?')[0];
        }

        var finder = new Regex(@"^hysteria2://(?<password>.+?)@(?<server>.+):(?<port>\d+)");
        var match = finder.Match(text);
        if (!match.Success)
            throw new FormatException();

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
