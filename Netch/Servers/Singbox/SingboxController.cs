using System.Net;
using System.Text.Json;
using Netch.Controllers;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes.ProcessMode;
using Netch.Models.Modes.TunMode;
using Netch.Utils;

namespace Netch.Servers;

public class SingboxController : Guard, IServerController
{
    public SingboxController() : base("sing-box.exe")
    {
    }

    // 日志级别 warn（SingboxLog）下不再有 "started" 行，不能靠 StartedKeywords 判启动（等不到行=50s 超时误报）；
    // 启动判定立即放行，真实就绪校验由 TUNController（接口轮询）/Clash API 探活承接。
    // FATAL 失败检测不限启动态（Guard.ReadOutputAsync 标记 FatalAfterStart，2026-11-04 审查 C-4）：
    // 配置错误/端口冲突在放行后打出 FATAL 也能被抓住——编排层跳过看门狗重启循环直接报错
    protected override IEnumerable<string> StartedKeywords => Array.Empty<string>();

    protected override IEnumerable<string> FailedKeywords => new[] { "FATAL" };

    public override string Name => "sing-box";

    public ushort? Socks5LocalPort { get; set; }

    public string? LocalAddress { get; set; }

    /// <summary>
    ///     当前实例的节点出站 tag 到 Server 的映射（多节点模式）；
    ///     Clash API delay 测试按 tag 请求后经此回填 Server.Delay
    /// </summary>
    public IReadOnlyDictionary<string, Server> OutboundTagMap { get; private set; } = new Dictionary<string, Server>();

    /// <summary>
    ///     进程模式的路由层规则（MainController 注入）：非空时生成 route.rules 并开启 Clash API
    /// </summary>
    public Redirector? Mode { get; set; }

    /// <summary>
    ///     TUN 模式（MainController 注入）：非空时配置追加 tun 入站（与 mixed 入站同进程合一），
    ///     路由规则/DNS 接管语义见 SingboxConfigUtils
    /// </summary>
    public TunMode? TunMode { get; set; }

    /// <summary>
    ///     Clash API 端口（一律开启：连接表观测/延迟测试/规则命中查询；仅绑 127.0.0.1 无暴露面）
    /// </summary>
    public ushort? ClashApiPort { get; private set; }

    public bool MultiNode => OutboundTagMap.Count > 1;

    public virtual async Task<Socks5Server> StartAsync(Server s)
    {
        Dictionary<string, Server> tagMap;
        await using (var fileStream = new FileStream(Constants.TempConfig, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            SingboxConfig config;
            var groupServers = CollectGroupServers(s);

            // Clash API 一律开启（EV-1）：单节点实例同样可观测连接表；
            // 只绑 127.0.0.1 空闲端口，无攻击面，每实例仅多占一个回环端口
            ClashApiPort = PortHelper.GetAvailablePort(PortType.TCP);
            (config, var map) = await SingboxConfigUtils.GenerateClientConfigAsync(groupServers, s, ClashApiPort, Mode, TunMode);
            tagMap = map;

            OutboundTagMap = tagMap;
            await JsonSerializer.SerializeAsync(fileStream, config, Global.NewCustomJsonSerializerOptions());
        }

        await StartGuardAsync("run -c ..\\data\\last.json");
        return new Socks5Server(IPAddress.Loopback.ToString(), this.Socks5LocalPort(), s.Hostname);
    }

    private static List<Server> CollectGroupServers(Server primary)
    {
        if (!primary.IsInGroup())
            return new List<Server> { primary };

        return Global.Settings.Server.Where(server => server.Group == primary.Group).ToList();
    }
}
