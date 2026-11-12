using System.Diagnostics;
using Microsoft.VisualStudio.Threading;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.TunMode;
using Netch.Servers;
using Netch.Services;
using Netch.UI.Hub;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Controllers;

public static class MainController
{
    public static Socks5Server? Socks5Server { get; private set; }

    public static Server? Server { get; private set; }

    public static Mode? Mode { get; private set; }

    public static IServerController? ServerController { get; private set; }

    public static IModeController? ModeController { get; private set; }

    private static readonly AsyncSemaphore Lock = new(1);

    public static async Task StartAsync(Server server, Mode mode)
    {
        using var releaser = await Lock.EnterAsync();

        Log.Information("Start MainController: {Server} {Mode}", $"{server.Type}", $"[{(int)mode.Type}]{mode.i18NRemark}");

        EnsureSocks5PortAvailable();

        if (await DnsUtils.LookupAsync(server.Hostname) == null)
            throw new MessageException(I18N.T("controller.lookupServerHostnameFailed"));

        // TODO Disable NAT Type Test setting
        // cache STUN Server ip to prevent "Wrong STUN Server"
        DnsUtils.LookupAsync(DnsUtils.SplitHostPort(Global.Settings.STUN_Server, Models.Setting.StunDefaultPort).Host).Forget();

        Server = server;
        Mode = mode;

        await Task.WhenAll(Task.Run(NativeMethods.RefreshDNSCache), Task.Run(Firewall.AddNetchFwRules));

        try
        {
            ModeController = ModeService.GetModeControllerByType(mode.Type, out var modePort, out var portName);

            if (modePort != null)
                TryReleaseTcpPort((ushort)modePort, portName);

            if (mode is TunMode tunMode)
            {
                // TUN 模式（M4-1）：sing-box 单进程 mixed+tun 双入站合一，无本地 socks 中转；
                // 仅 v2ray 可表达的节点（SSR/mKCP/xhttp 等）在 TUN 下无从接管，启动即报错指引换节点或内核
                ServerController = CreateServerController(server);
                if (ServerController is not SingboxController singboxController)
                    throw new MessageException(I18N.T("controller.tunModeOnlySupportedSing"));

                singboxController.TunMode = tunMode;

                Global.UIHub.StatusText(I18N.TF("controller.starting0", ServerController.Name));

                TryReleaseTcpPort(ServerController.Socks5LocalPort(), "Socks5");
                Socks5Server = await ServerController.StartAsync(server);

                StatusPortInfoText.Socks5Port = Socks5Server.Port;
                StatusPortInfoText.UpdateShareLan();

                if (ModeController is TUNController tunController)
                    tunController.ClashApiPort = singboxController.ClashApiPort;
            }
            else if (Server is Socks5Server socks5 && (!socks5.Auth() || ModeController.Features.HasFlag(ModeFeature.SupportSocks5Auth)))
            {
                Socks5Server = socks5;
            }
            else
            {
                // Start Server Controller to get a local socks5 server
                Log.Debug("Server Information: {Data}", $"{server.Type} {server.MaskedData()}");

                ServerController = CreateServerController(server);
                if (ServerController is SingboxController singboxController)
                {
                    singboxController.Mode = mode as Models.Modes.ProcessMode.Redirector;
                }
                else if (mode is Models.Modes.ProcessMode.Redirector { Rules: { Count: > 0 } })
                {
                    // Xray 兜底内核暂未生成路由层规则，避免「配了规则却不生效」的静默错误
                    Log.Warning("Mode \"{Mode}\" has routing rules but kernel {Kernel} does not support them yet; all traffic goes to outbound", mode.i18NRemark, ServerController.Name);
                }

                Global.UIHub.StatusText(I18N.TF("controller.starting0", ServerController.Name));

                TryReleaseTcpPort(ServerController.Socks5LocalPort(), "Socks5");
                Socks5Server = await ServerController.StartAsync(server);

                StatusPortInfoText.Socks5Port = Socks5Server.Port;
                StatusPortInfoText.UpdateShareLan();
            }

            // Start Mode Controller
            Global.UIHub.StatusText(I18N.TF("controller.starting0", ModeController.Name));

            await ModeController.StartAsync(Socks5Server, mode);

            // M3-2: 内核进程运行中意外退出 -> 编排层重启（主动停止不会走到这里）
            if (ServerController is Guard guard)
                guard.InstanceExited += OnKernelExited;
        }
        catch (Exception e)
        {
            releaser.Dispose();
            await StopAsync();

            switch (e)
            {
                case DllNotFoundException:
                case FileNotFoundException:
                    throw new Exception(e.Message + "\n\n" + I18N.T("controller.missingFileRuntimeComponents"));
                case MessageException:
                    throw;
                default:
                    Log.Error(e, "Unhandled Exception When Start MainController");
                    Utils.Utils.Open(Constants.LogFile);
                    throw new MessageException($"{I18N.T("controller.unhandledException")}\n{e.Message}");
            }
        }
    }

    public static async Task StopAsync()
    {
        if (Lock.CurrentCount == 0)
        {
            (await Lock.EnterAsync()).Dispose();
            if (ServerController == null && ModeController == null)
                // stopped
                return;

            // else begin stop
        }

        using var _ = await Lock.EnterAsync();

        if (ServerController == null && ModeController == null)
            return;

        Log.Information("Stop Main Controller");
        StatusPortInfoText.Reset();

        try
        {
            if (ModeController is NFController)
            {
                // #1145：aio_free 必须先释放回调链，再杀内核进程。
                // 并行停止时驱动回调与内核退出竞争，24H2 上偶发死锁。
                await StopOneAsync(ModeController);
                await StopOneAsync(ServerController);
            }
            else
            {
                // TUN/Share 无共享资源，保持并行
                await Task.WhenAll(StopOneAsync(ServerController), StopOneAsync(ModeController));
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "MainController Stop Error");
        }

        ServerController = null;
        ModeController = null;
    }

    private static async Task StopOneAsync(IController? controller)
    {
        if (controller == null)
            return;

        try
        {
            await controller.StopAsync();
        }
        catch (Exception e)
        {
            Log.Error(e, "Stop {Name} failed", controller.Name);
        }
    }

    /// <summary>
    ///     断网有序暂停（进程模式）：只停内核进程，NF 拦截层（驱动 + DLL 回调）保留挂载，
    ///     恢复时重新 Dial 即可，避免反复 aio_free/aio_init 的挂载开销
    /// </summary>
    public static async Task StopServerControllerOnlyAsync()
    {
        using var releaser = await Lock.EnterAsync();

        if (ServerController == null)
            return;

        Log.Information("Stop server controller only (network pause)");
        await StopOneAsync(ServerController);
        ServerController = null;
    }

    /// <summary>
    ///     本地 Socks5 监听口被外部进程占用时自动顺延到空闲端口并回写设置。
    ///     默认 2801/2802 与常见代理软件（7890/1080 等）无重叠，被占时顺延而非启动失败；
    ///     Netch 自家残留进程仍由 TryReleaseTcpPort 清理。
    /// </summary>
    private static void EnsureSocks5PortAvailable()
    {
        var port = Global.Settings.Socks5LocalPort;
        var external = PortHelper.GetProcessByUsedTcpPort(port)
            .Where(p => p.MainModulePath is not { } fileName || !fileName.StartsWith(Global.NetchDir))
            .ToList();
        if (external.Count == 0)
            return;

        var free = PortHelper.GetAvailablePort(PortType.TCP);
        Log.Information("Socks5 port {Port} is occupied by external process(es) {Names}, fallback to {Free}",
            port, string.Join(",", external.Select(p => $"({p.Id}){p.ProcessName}")), free);
        Global.Settings.Socks5LocalPort = free;
        Configuration.SaveAsync().Forget();
    }

    /// <summary>
    ///     恢复重建：重新解析节点域名 -> 重建内核 -> TUN 模式重新校验接口出现 / 进程模式 NF 重新 Dial 新端口。
    ///     与用户操作经同一把 AsyncSemaphore 互斥。
    /// </summary>
    public static async Task RestartCoreAsync()
    {
        using var releaser = await Lock.EnterAsync();

        EnsureSocks5PortAvailable();

        if (Mode == null || Server == null)
            throw new MessageException("No active session to restore");

        if (ServerController != null)
        {
            await StopOneAsync(ServerController);
            ServerController = null;
        }

        // 节点域名可能已解析到新 IP：显式清缓存强制重新解析（TTL 内的旧记录也不许短路自愈链路，2026-11-04 审查 F-1）
        DnsUtils.ClearCache();
        await DnsUtils.LookupAsync(Server.Hostname);

        if (!(Server is Socks5Server socks5 && (!socks5.Auth() || ModeController?.Features.HasFlag(ModeFeature.SupportSocks5Auth) == true)))
        {
            var controller = CreateServerController(Server);
            if (controller is SingboxController singboxController)
            {
                singboxController.Mode = Mode as Models.Modes.ProcessMode.Redirector;
                // TUN 模式必须回填，否则重建的内核配置缺 tun 入站，接口与接管路由不会恢复（实机 20 轮启停验收发现的缺陷）
                singboxController.TunMode = Mode as Models.Modes.TunMode.TunMode;
            }

            TryReleaseTcpPort(controller.Socks5LocalPort(), "Socks5");

            ServerController = controller;
            Socks5Server = await controller.StartAsync(Server);

            StatusPortInfoText.Socks5Port = Socks5Server.Port;

            if (ServerController is Guard guard)
                guard.InstanceExited += OnKernelExited;
        }

        // NF 重新指向新的 socks 端口（不 aio_free 重挂驱动）
        if (ModeController is NFController nf && Socks5Server != null)
            await nf.RedialServerAsync(Socks5Server);

        // TUN 模式：内核重建后兜底校验 tun 接口真实出现（auto_route/接口由内核自建）
        if (ModeController is Controllers.TUNController tun && Socks5Server != null && Mode != null)
            await tun.StartAsync(Socks5Server, Mode);
    }

    /// <summary>
    ///     内核进程意外退出 -> 交给 UI 线程按退避策略重启
    /// </summary>
    private static void OnKernelExited(Guard guard)
    {
        UIEvents.RaiseKernelExited(guard);
    }

    /// <summary>
    ///     内核路由策略（2026-10-31 拍板去手选，全自动）：sing-box 唯一主力，Xray 按节点协议自动兜底
    ///     （xhttp / mKCP / QUIC / tcp+http 伪装 / 明文 Trojan）。
    ///     内核禁用闭环（R 线 §4.4）：外部资源页禁用的内核，任何路由路径都不再实例化。
    /// </summary>
    public static IServerController CreateServerController(Server server)
    {
        var singBoxBlocked = IsResourceDisabled("kernel.singbox");
        var xrayBlocked = IsResourceDisabled("kernel.xray");

        switch (server)
        {
            // xhttp 优先于安全层判定：reality+xhttp 组合仅 Xray 同时支持两种能力（sing-box 无 xhttp 传输）
            case VLESSServer { TransferProtocol: "xhttp" }:
            case VMessServer { TransferProtocol: "xhttp" }:
                if (xrayBlocked)
                    throw new MessageException(I18N.TF("external.kernelBlocked0", "Xray"));
                return new XrayController();

            case AnytlsServer or Hysteria2Server or TUICServer:
            case VLESSServer { TLSSecureType: "reality" }:
                if (singBoxBlocked)
                    throw new MessageException(I18N.TF("external.kernelBlocked0", "sing-box"));
                return new SingboxController();

            // SSR 是 v2ray-sn 自建改造版私有能力，随槽位退役（2026-10-31 拍板 A：明确报不再支持）
            case ShadowsocksRServer:
            // 旧 XTLS flow（xtls-rprx-direct）Xray 1.8+ 已删除，服务端生态已死：明确报不再支持
            case VLESSServer { TLSSecureType: "xtls" }:
            case TrojanServer { TLSSecureType: "xtls" }:
            // 裸 QUIC 传输双死：sing-box 从未实现；随包 Xray 26.10.21 已删除该传输
            // （实测 xray run -test 原文「QUIC transport … has been removed」，2026-10-31 施工探针）
            case VMessServer { TransferProtocol: "quic" }:
                throw new MessageException(I18N.TF("controller.serverType0NotSupported", server.Type));

            // 协议语义在 sing-box 无等价实现，自动兜底 Xray（复用 V2rayConfigUtils 生成器）
            // （tcp+http 伪装 / mKCP 传输 sing-box 均未实现或无法表达；明文 Trojan 非 sing-box 支持形态）
            case VMessServer { TransferProtocol: "tcp", FakeType: "http" }:
            case VMessServer { TransferProtocol: "kcp" }:
            case TrojanServer { TLSSecureType: "none" }:
                if (xrayBlocked)
                    throw new MessageException(I18N.TF("external.kernelBlocked0", "Xray"));
                return new XrayController();
        }

        if (singBoxBlocked)
            throw new MessageException(I18N.TF("external.kernelBlocked0", "sing-box"));

        return new SingboxController();
    }

    private static bool IsResourceDisabled(string key)
    {
        var resource = Services.ExternalResourceService.Find(key);
        return resource != null && Services.ExternalResourceService.IsDisabled(resource);
    }

    /// <summary>
    ///     多节点 sing-box 实例运行期间，组内节点的延迟由 Clash API 实测维护，
    ///     TCPing 数值语义不同且会覆盖 API 延迟，对这些节点应跳过裸 TCPing
    /// </summary>
    public static bool IsMultiNodeMember(Server server)
    {
        return ServerController is SingboxController { MultiNode: true } controller &&
               controller.OutboundTagMap.Values.Contains(server);
    }

    /// <summary>
    ///     单测一个节点的延迟：多节点实例成员经 Clash API 实测（全链路语义），其余走 TCPing/ICMP
    /// </summary>
    public static async Task TestNodeDelayAsync(Server server)
    {
        if (ServerController is SingboxController { ClashApiPort: not null } controller &&
            controller.OutboundTagMap.Values.Contains(server))
        {
            var tag = controller.OutboundTagMap.First(kv => ReferenceEquals(kv.Value, server)).Key;
            var delay = await ClashApiClient.GetProxyDelayAsync(controller.ClashApiPort.Value, tag);
            server.SetDelay(delay ?? -4);
        }
        else
        {
            await server.PingAsync();
        }
    }

    public static void PortCheck(ushort port, string portName, PortType portType = PortType.Both)
    {
        try
        {
            PortHelper.CheckPort(port, portType);
        }
        catch (PortInUseException)
        {
            throw new MessageException(I18N.TF("controller.0PortUse", $"{portName} ({port})"));
        }
        catch (PortReservedException)
        {
            throw new MessageException(I18N.TF("controller.0PortReservedSystem", $"{portName} ({port})"));
        }
    }

    public static void TryReleaseTcpPort(ushort port, string portName)
    {
        foreach (var owner in PortHelper.GetProcessByUsedTcpPort(port))
        {
            var fileName = owner.MainModulePath;
            if (fileName == null)
                continue;

            if (fileName.StartsWith(Global.NetchDir))
            {
                using var p = Process.GetProcessById(owner.Id);
                p.Kill();
                p.WaitForExit();
            }
            else
            {
                throw new MessageException(I18N.TF("controller.0PortUsed1", $"{portName} ({port})", $"({owner.Id}){fileName}"));
            }
        }

        PortCheck(port, portName, PortType.TCP);
    }

    public static Task<NatTypeTestResult> DiscoveryNatTypeAsync(CancellationToken ctx = default)
    {
        Debug.Assert(Socks5Server != null, nameof(Socks5Server) + " != null");
        return Socks5ServerTestUtils.DiscoveryNatTypeAsync(Socks5Server, ctx);
    }

    public static Task<int?> HttpConnectAsync(CancellationToken ctx = default)
    {
        Debug.Assert(Socks5Server != null, nameof(Socks5Server) + " != null");
        try
        {
            return Socks5ServerTestUtils.HttpConnectAsync(Socks5Server, ctx);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled Socks5ServerTestUtils.HttpConnectAsync Exception");
        }

        return Task.FromResult<int?>(null);
    }
}