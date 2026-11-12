using System.Net.NetworkInformation;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.TunMode;
using Netch.Servers;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Controllers;

/// <summary>
///     TUN 模式控制器（sing-box tun 入站接管）：与 ServerController 合一（同一内核进程
///     mixed+tun 双入站），自动路由/DNS 劫持/UDP NAT 全交给内核，本控制器不再管理
///     路由表与 tun2socks 生命周期，只做 tun 接口的启停兜底校验。
/// </summary>
public class TUNController : IModeController
{
    public string Name => "tun";

    public string InterfaceName => "netch";

    public ModeFeature Features => ModeFeature.SupportSocks5;

    /// <summary>
    ///     sing-box 内核的 Clash API 端口（MainController 注入），启动后探活用
    /// </summary>
    public ushort? ClashApiPort { get; set; }

    public async Task StartAsync(Socks5Server server, Mode mode)
    {
        if (mode is not TunMode)
            throw new InvalidOperationException();

        // 内核 started 日志已由 ServerController 的 Guard 等待，这里校验 tun 接口真实出现：
        // 常见失败因是非管理员运行或 bin\wintun.dll 缺失（wintun 建接口被拒）
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            if (HasInterface())
                return;
        }

        throw new MessageException(I18N.T("controller.tunInterfaceWasNotCreated") + "\n" +
                                   I18N.T("common.runNetchAsAdministratorMake"));
    }

    public async Task StopAsync()
    {
        // 内核进程退出时自删路由/接口/接口 DNS，这里只兜底校验接口消失（残留仅记录，不手写路由）
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            if (!HasInterface())
                return;
        }

        Log.Warning("TUN interface {Name} still present after kernel exit", InterfaceName);
    }

    private bool HasInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Any(ni => ni.Name.StartsWith(InterfaceName));
    }
}