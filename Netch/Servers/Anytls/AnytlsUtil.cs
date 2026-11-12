using System.Text.RegularExpressions;
using System.Web;
using Netch.Interfaces;
using Netch.Models;
using Netch.Utils;

namespace Netch.Servers;

public class AnytlsUtil : IServerUtil
{
    public ushort Priority { get; } = 5;

    public string TypeName { get; } = "AnyTLS";

    public string FullName { get; } = "AnyTLS";

    public string ShortName { get; } = "AT";

    public string[] UriScheme { get; } = { "anytls" };

    public Type ServerType { get; } = typeof(AnytlsServer);

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
        var server = (AnytlsServer)s;

        var query = server.Host.IsNullOrWhiteSpace() ? "" : $"?sni={HttpUtility.UrlEncode(server.Host)}";

        return $"anytls://{HttpUtility.UrlEncode(server.Password)}@{server.Hostname}:{server.Port}{query}#{HttpUtility.UrlEncode(server.Remark)}";
    }

    public IServerController GetController()
    {
        return new SingboxController();
    }

    public IEnumerable<Server> ParseUri(string text)
    {
        var data = new AnytlsServer();

        if (text.Contains('#'))
        {
            data.Remark = HttpUtility.UrlDecode(text.Split('#')[1]);
            text = text.Split('#')[0];
        }

        if (text.Contains('?'))
        {
            var sni = HttpUtility.UrlDecode(HttpUtility.ParseQueryString(new Uri(text).Query).Get("sni"));
            if (sni != null)
                data.Host = sni;

            text = text.Split('?')[0];
        }

        var finder = new Regex(@"^anytls://(?<password>.+?)@(?<server>.+):(?<port>\d+)");
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
