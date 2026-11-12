namespace Netch.Models;

public class RedirectorConfig
{
    public bool FilterTCP { get; set; } = true;

    public bool FilterUDP { get; set; } = true;

    public bool FilterDNS { get; set; } = true;

    public bool FilterParent { get; set; } = false;

    public bool HandleOnlyDNS { get; set; } = true;

    public bool DNSProxy { get; set; } = true;

    public string DNSHost { get; set; } = $"{Constants.DefaultPrimaryDNS}:53";

    public int ICMPDelay { get; set; } = 10;

    public bool FilterICMP { get; set; } = false;

    /// <summary>
    ///     Redirector 回调详细日志（默认关：NF 回调线程写控制台会拖慢热路径，仅排查时开）
    /// </summary>
    public bool VerboseLog { get; set; } = false;

    /// <summary>
    ///     回退到 Step A 的线程对转发泵（默认用 IOCP）。仅供排查/灰度，无需重新编译
    /// </summary>
    public bool UseLegacyPump { get; set; } = false;
}