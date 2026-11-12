using System.Text.Json;
using Netch.UI.Theme;

namespace Netch.Models;

/// <summary>
///     外观配置（UI 重构 S2）：主题三态 + 强调色覆盖（#RRGGBB，空=默认）
/// </summary>
public class Appearance
{
    /// <summary>主题态（System=跟随系统）</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>强调色覆盖（#RRGGBB）；空字符串 = 使用默认 Accent</summary>
    public string Accent { get; set; } = "";
}

/// <summary>
///     用于读取和写入的配置的类
/// </summary>
public class Setting
{
    public RedirectorConfig Redirector { get; set; } = new();

    /// <summary>
    ///     服务器列表
    /// </summary>
    public List<Server> Server { get; set; } = new();

    /// <summary>
    ///     是否检查 Beta 更新
    /// </summary>
    public bool CheckBetaUpdate { get; set; } = false;

    /// <summary>
    ///     主窗口宽度（退出时记录，0 = 未自定义按默认）
    /// </summary>
    public int MainFormWidth { get; set; } = 0;

    /// <summary>
    ///     主窗口高度（退出时记录，0 = 未自定义按默认）
    /// </summary>
    public int MainFormHeight { get; set; } = 0;

    /// <summary>
    ///     连接观测页三张表的列宽记忆（退出时记录）。
    ///     key = "{process|kernel|history}:{列序号}"（列不可重排，序号稳定）；
    ///     value = 宽度 DIP，-1 = 星号列（吃剩余空间）。空字典 = 未自定义按 XAML 默认。
    /// </summary>
    public Dictionary<string, double> ConnectionsColumnWidths { get; set; } = new();

    /// <summary>
    ///     列宽记忆的出厂口径版本：XAML 默认列宽调整时 +1，旧版本记忆整体作废
    ///     （防新默认被旧记忆覆盖，用户拖宽后仍以用户值为准）。
    /// </summary>
    public int ConnectionsColumnWidthsSchema { get; set; } = 0;

    /// <summary>
    ///     是否打开软件时检查更新
    /// </summary>
    public bool CheckUpdateWhenOpened { get; set; } = true;

    /// <summary>
    ///     测试所有服务器心跳/秒
    /// </summary>
    public int DetectionTick { get; set; } = 10;

    /// <summary>
    ///     是否关闭窗口时退出
    /// </summary>
    public bool ExitWhenClosed { get; set; } = false;

    /// <summary>
    ///     HTTP 本地端口
    /// </summary>
    public ushort HTTPLocalPort { get; set; } = 2802;

    /// <summary>
    ///     语言设置
    /// </summary>
    public string Language { get; set; } = "System";

    /// <summary>
    ///     HTTP 和 Socks5 本地代理地址
    /// </summary>
    public string LocalAddress { get; set; } = "127.0.0.1";

    /// <summary>
    ///     是否启动后自动最小化
    /// </summary>
    public bool MinimizeWhenStarted { get; set; } = false;

    /// <summary>
    ///     模式选择位置
    /// </summary>
    public int ModeComboBoxSelectedIndex { get; set; } = -1;

    /// <summary>
    ///     快捷配置数量
    /// </summary>
    public int ProfileCount { get; set; } = 4;

    /// <summary>
    ///     已保存的快捷配置
    /// </summary>
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>
    ///     配置最大列数
    /// </summary>
    public byte ProfileTableColumnCount { get; set; } = 5;

    /// <summary>
    ///     网页请求超时 毫秒
    /// </summary>
    public int RequestTimeout { get; set; } = 10000;

    /// <summary>
    ///     是否开机启动软件
    /// </summary>
    public bool RunAtStartup { get; set; } = false;

    /// <summary>
    ///     服务器选择位置
    /// </summary>
    public int ServerComboBoxSelectedIndex { get; set; } = -1;

    /// <summary>
    ///     服务器测试方式 false.ICMPing true.TCPing
    /// </summary>
    public bool ServerTCPing { get; set; } = true;

    /// <summary>
    ///     Socks5 本地端口
    /// </summary>
    public ushort Socks5LocalPort { get; set; } = 2801;

    /// <summary>
    ///     启动后延迟测试间隔/秒
    /// </summary>
    public int StartedPingInterval { get; set; } = -1;

    /// <summary>
    ///     是否打开软件时启动加速
    /// </summary>
    public bool StartWhenOpened { get; set; } = false;

    /// <summary>
    ///     是否退出时停止
    /// </summary>
    public bool StopWhenExited { get; set; } = false;

    /// <summary>
    ///     STUN测试服务器，完整地址 host:port（无端口按 3478）
    /// </summary>
    public string STUN_Server { get; set; } = "stun.syncthing.net";

    /// <summary>STUN 默认端口（RFC 5389）</summary>
    public const ushort StunDefaultPort = 3478;

    /// <summary>
    ///     外观（主题三态 + 强调色覆盖）
    /// </summary>
    public Appearance AppearanceSettings { get; set; } = new();

    /// <summary>
    ///     订阅链接列表
    /// </summary>
    public List<Subscription> Subscription { get; set; } = new();

    /// <summary>
    ///     TUNTAP 适配器配置
    /// </summary>
    public TUNConfig TUNTAP { get; set; } = new();

    /// <summary>
    ///     是否打开软件时更新订阅
    /// </summary>
    public bool UpdateServersWhenOpened { get; set; } = false;

    public V2rayConfig V2RayConfig { get; set; } = new();

    /// <summary>
    ///     代理内核配置
    /// </summary>
    public KernelConfig Kernel { get; set; } = new();

    /// <summary>
    ///     用户自定义远程规则集（.srs），路由规则以 tag 引用；内置 geosite-cn / geoip-cn 无需配置
    /// </summary>
    public List<RuleSetConfig> RuleSets { get; set; } = new();

    /// <summary>
    ///     启用内置规则集（geosite-cn / geoip-cn）。关闭后视同不存在：DNS 分流不再引用，
    ///     路由规则引用内置 tag 校验失败，规则编辑器不显示内置项
    /// </summary>
    public bool UseBuiltInRuleSets { get; set; } = true;

    /// <summary>
    ///     进程模式 DNS 策略（sing-box 内核接管）
    /// </summary>
    public DnsConfig DNS { get; set; } = new();

    /// <summary>
    ///     可靠性：断网自愈与内核守护
    /// </summary>
    public NetworkConfig Network { get; set; } = new();

    /// <summary>
    ///     外部资源统一管理（R 线 §4.1）：自动更新调度 + 每资源状态（缺省条目不落盘）
    /// </summary>
    public ExternalResourceSettings ExternalResources { get; set; } = new();

    /// <summary>
    ///     订阅/更新检查在运行时经本地 mixed 入站（墙内订阅地址直连常失败）
    /// </summary>
    public bool SubscriptionUseProxy { get; set; } = true;

    public bool NoSupportDialog { get; set; } = false;

    #region Migration

    [Obsolete]
    public JsonElement SubscribeLink
    {
        set
        {
            if (Subscription == null! || !Subscription.Any())
                Subscription = value.Deserialize<List<Subscription>>()!;
        }
    }

    #endregion

    public Setting ShallowCopy()
    {
        return (Setting)MemberwiseClone();
    }

    public void Set(Setting value)
    {
        // Skip write-only properties (such as SubscribeLink, a legacy migration property): reflection on a property without a getter throws ArgumentException (triggered by the backup restore chain's Set, fixed at root 2026-11-11)
        foreach (var p in typeof(Setting).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite)
                continue;

            p.SetValue(this, p.GetValue(value));
        }
    }
}