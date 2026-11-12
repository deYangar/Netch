using System.Net;
using System.Text.Json;
using Netch.Controllers;
using Netch.Interfaces;
using Netch.Models;
using Netch.Utils;

namespace Netch.Servers;

/// <summary>
///     Xray 辅内核：承载 Xray 独占能力的存量节点（xhttp 传输等）。
///     配置格式与 v2ray 同构（Xray-core 为 v2ray fork），复用 V2rayConfigUtils 生成；
///     bin 下无内核时引导到设置页下载或自备 xray.exe
/// </summary>
public class XrayController : Guard, IServerController
{
    public XrayController() : base("xray.exe")
    {
    }

    protected override IEnumerable<string> StartedKeywords => new[] { "started" };

    protected override IEnumerable<string> FailedKeywords => new[] { "failed to", "not readable", "FATAL" };

    public override string Name => "Xray";

    public ushort? Socks5LocalPort { get; set; }

    public string? LocalAddress { get; set; }

    public virtual async Task<Socks5Server> StartAsync(Server s)
    {
        await using (var fileStream = new FileStream(Constants.TempConfig, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(fileStream, await V2rayConfigUtils.GenerateClientConfigAsync(s), Global.NewCustomJsonSerializerOptions());
        }

        await StartGuardAsync("run -c ..\\data\\last.json");
        return new Socks5Server(IPAddress.Loopback.ToString(), this.Socks5LocalPort(), s.Hostname);
    }
}
