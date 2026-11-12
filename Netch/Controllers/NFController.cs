using System.Net;
using System.ServiceProcess;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Servers;
using Netch.Utils;
using static Netch.Interops.Redirector;
using Netch.Localization;

namespace Netch.Controllers;

public class NFController : IModeController
{
    private Server? _server;
    private Redirector _mode = null!;
    private RedirectorConfig _rdrConfig = null!;

    private static readonly ServiceController NFService = new("netfilter2");

    private static readonly string SystemDriver = $"{Environment.SystemDirectory}\\drivers\\netfilter2.sys";

    public string Name => "Redirector";

    public ModeFeature Features => ModeFeature.SupportIPv6 | ModeFeature.SupportSocks5Auth;

    public async Task StartAsync(Socks5Server server, Mode mode)
    {
        if (mode is not Redirector processMode)
            throw new InvalidOperationException();

        _server = server;
        _mode = processMode;
        _rdrConfig = Global.Settings.Redirector;

        CheckDriver();

        Dial(NameList.AIO_FILTERLOOPBACK, _mode.FilterLoopback);
        Dial(NameList.AIO_FILTERINTRANET, _mode.FilterIntranet);
        Dial(NameList.AIO_FILTERPARENT, _mode.FilterParent ?? _rdrConfig.FilterParent);
        Dial(NameList.AIO_FILTERICMP, _mode.FilterICMP ?? _rdrConfig.FilterICMP);
        if (_mode.FilterICMP ?? _rdrConfig.FilterICMP)
            Dial(NameList.AIO_ICMPING, (_mode.FilterICMP != null ? _mode.ICMPDelay ?? 10 : _rdrConfig.ICMPDelay).ToString());

        Dial(NameList.AIO_FILTERTCP, _mode.FilterTCP ?? _rdrConfig.FilterTCP);
        Dial(NameList.AIO_FILTERUDP, _mode.FilterUDP ?? _rdrConfig.FilterUDP);

        // DNS
        Dial(NameList.AIO_FILTERDNS, _mode.FilterDNS ?? _rdrConfig.FilterDNS);
        Dial(NameList.AIO_DNSONLY, _mode.HandleOnlyDNS ?? _rdrConfig.HandleOnlyDNS);

        // M3-1 A1: 回调内日志默认静默（NF 回调热路径 wcout 拖性能），排查时经设置开启
        Dial(NameList.AIO_VERBOSE, Global.Settings.Redirector.VerboseLog);

        // M3-1 Step B: 转发泵选择（IOCP 默认；legacy 为运行期回退，不必重新编译）
        Dial(NameList.AIO_PUMP, Global.Settings.Redirector.UseLegacyPump ? "legacy" : "iocp");

        var isSingbox = MainController.ServerController is SingboxController;
        if (isSingbox)
        {
            // sing-box 内核：DNS 统一经 SOCKS 通道发原始查询目标，由内核 hijack-dns + dns 模块分流；
            // DNSHost/DNSProxy 用户设置在此内核下不再参与（避免与内核 dns 策略打架）
            Dial(NameList.AIO_DNSPROX, true);
            Dial(NameList.AIO_DNSKEEP, true);
        }
        else
        {
            Dial(NameList.AIO_DNSPROX, _mode.DNSProxy ?? _rdrConfig.DNSProxy);
            Dial(NameList.AIO_DNSKEEP, false);
            if (_mode.FilterDNS ?? _rdrConfig.FilterDNS)
            {
                var dnsStr = _mode.FilterDNS != null ? _mode.DNSHost : _rdrConfig.DNSHost;

                dnsStr = dnsStr.ValueOrDefault() ?? $"{Constants.DefaultPrimaryDNS}:53";

                var dns = IPEndPoint.Parse(dnsStr);
                if (dns.Port == 0)
                    dns.Port = 53;

                Dial(NameList.AIO_DNSHOST, dns.Address.ToString());
                Dial(NameList.AIO_DNSPORT, dns.Port.ToString());
            }
        }

        // Server
        Dial(NameList.AIO_TGTHOST, await server.AutoResolveHostnameAsync());
        Dial(NameList.AIO_TGTPORT, server.Port.ToString());
        Dial(NameList.AIO_TGTUSER, server.Username ?? string.Empty);
        Dial(NameList.AIO_TGTPASS, server.Password ?? string.Empty);

        // Mode Rule
        DialRule();

        if (!await InitAsync())
            throw new MessageException("Redirector start failed.");
    }

    public Task StopAsync()
    {
        return FreeAsync();
    }

    /// <summary>
    ///     网络恢复后重新指向新的本地 socks 端口/节点地址（M3-2）：只改 Dial 参数，
    ///     不 aio_free 重挂驱动——已建立的连接不受影响，新连接走新目标
    /// </summary>
    public async Task RedialServerAsync(Socks5Server server)
    {
        _server = server;

        var host = await server.AutoResolveHostnameAsync();
        Dial(NameList.AIO_TGTHOST, host);
        Dial(NameList.AIO_TGTPORT, server.Port.ToString());
        Dial(NameList.AIO_TGTUSER, server.Username ?? string.Empty);
        Dial(NameList.AIO_TGTPASS, server.Password ?? string.Empty);

        Log.Information("Redirector re-dialed to {Host}:{Port}", host, server.Port);
    }

    #region CheckRule

    /// <summary>
    /// </summary>
    /// <param name="r"></param>
    /// <param name="clear"></param>
    /// <returns>No Problem true</returns>
    private static bool CheckCppRegex(string r, bool clear = true)
    {
        try
        {
            if (r.StartsWith("!"))
                return Dial(NameList.AIO_ADDNAME, r.Substring(1));

            return Dial(NameList.AIO_ADDNAME, r);
        }
        finally
        {
            if (clear)
                Dial(NameList.AIO_CLRNAME, "");
        }
    }

    /// <summary>
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="results"></param>
    /// <returns>No Problem true</returns>
    public static bool CheckRules(IEnumerable<string> rules, out IEnumerable<string> results)
    {
        results = rules.Where(r => !CheckCppRegex(r, false));
        Dial(NameList.AIO_CLRNAME, "");
        return !results.Any();
    }

    public static string GenerateInvalidRulesMessage(IEnumerable<string> rules)
    {
        return $"{string.Join("\n", rules)}\n" + I18N.T("controller.aboveRulesDoesNotConform");
    }

    #endregion

    private void DialRule()
    {
        Dial(NameList.AIO_CLRNAME, "");
        var invalidList = new List<string>();
        foreach (var s in _mode.Bypass)
        {
            if (!Dial(NameList.AIO_BYPNAME, s))
                invalidList.Add(s);
        }

        foreach (var s in _mode.Handle)
        {
            if (!Dial(NameList.AIO_ADDNAME, s))
                invalidList.Add(s);
        }

        if (invalidList.Any())
            throw new MessageException(GenerateInvalidRulesMessage(invalidList));

        // Bypass Self
        Dial(NameList.AIO_BYPNAME, "^" + Global.NetchDir.ToRegexString());
    }

    #region DriverUtil

    private static void CheckDriver()
    {
        var binFileVersion = Utils.Utils.GetFileVersion(Constants.NFDriver);
        var systemFileVersion = Utils.Utils.GetFileVersion(SystemDriver);

        Log.Information("Built-in  netfilter2 driver version: {Name}", binFileVersion);
        Log.Information("Installed netfilter2 driver version: {Name}", systemFileVersion);

        if (!File.Exists(SystemDriver))
        {
            // Install
            InstallDriver();
            return;
        }

        var reinstall = false;
        if (Version.TryParse(binFileVersion, out var binResult) && Version.TryParse(systemFileVersion, out var systemResult))
        {
            if (binResult.CompareTo(systemResult) > 0)
                // Update
                reinstall = true;
            else if (systemResult.Major != binResult.Major)
                // Downgrade when Major version different (may have breaking changes)
                reinstall = true;
        }
        else
        {
            // Parse File versionName to Version failed
            if (!systemFileVersion.Equals(binFileVersion))
                // versionNames are different, Reinstall
                reinstall = true;
        }

        if (!reinstall)
            return;

        Log.Information("Update netfilter2 driver");
        UninstallDriver();
        InstallDriver();
    }

    /// <summary>
    ///     安装 NF 驱动
    /// </summary>
    /// <returns>驱动是否安装成功</returns>
    private static void InstallDriver()
    {
        Log.Information("Install netfilter2 driver");
        Global.UIHub.StatusText(I18N.T("controller.installingNetfilter2Driver"));

        if (!File.Exists(Constants.NFDriver))
            throw new MessageException(I18N.T("controller.builtinDriverFilesMissingCan"));

        try
        {
            File.Copy(Constants.NFDriver, SystemDriver);
        }
        catch (Exception e)
        {
            Log.Error(e, "Copy netfilter2.sys failed\n");
            throw new MessageException($"Copy netfilter2.sys failed\n{e.Message}");
        }

        // 注册驱动文件
        if (Interops.Redirector.aio_register("netfilter2"))
        {
            Log.Information("Install netfilter2 driver finished");
        }
        else
        {
            // 安装失败必须显式失败：带病运行的驱动只会让所有进程模式流量静默失效
            Log.Error("Register netfilter2 failed");
            throw new MessageException(I18N.T("controller.failedRegisterNetfilter2Driver"));
        }
    }

    /// <summary>
    ///     卸载 NF 驱动
    /// </summary>
    /// <returns>是否成功卸载</returns>
    public static bool UninstallDriver()
    {
        Log.Information("Uninstall netfilter2");
        try
        {
            if (NFService.Status == ServiceControllerStatus.Running)
            {
                NFService.Stop();

                // #1145：原先无超时死等，24H2 上驱动卸载卡住会拖死整个停止序列
                try
                {
                    NFService.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    Log.Warning("netfilter2 service did not stop within 10s, forcing one more stop");
                    try
                    {
                        NFService.Stop();
                        NFService.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "netfilter2 service still running; driver file may stay locked");
                    }
                }
            }
        }
        catch (Exception)
        {
            // ignored
        }

        if (!File.Exists(SystemDriver))
            return true;

        Interops.Redirector.aio_unregister("netfilter2");
        // 低危⑪（2026-11-04 审查）：服务未停净时驱动文件仍被锁，裸删抛 IOException 会使启动失败
        // （上层只弹一句不友好异常）——删除留待下次启动重试，本次如实记日志
        try
        {
            File.Delete(SystemDriver);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Delete driver file failed (locked by a not-yet-stopped service?), will retry next start");
        }

        return true;
    }

    #endregion
}