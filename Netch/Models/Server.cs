using System.Net.Sockets;
using System.Text.Json.Serialization;
using Netch.Enums;
using Netch.Localization;
using Netch.Utils;

namespace Netch.Models;

public abstract class Server : ICloneable
{
    /// <summary>
    ///     延迟
    /// </summary>
    [JsonIgnore]
    public int Delay { get; private set; } = -1;

    /// <summary>
    ///     延迟的测量来源（TCPing 与 Clash API 数值语义不同）
    /// </summary>
    [JsonIgnore]
    public DelaySource DelaySource { get; private set; } = DelaySource.None;

    /// <summary>
    ///     组
    /// </summary>
    public string Group { get; set; } = Constants.DefaultGroup;

    /// <summary>
    ///     地址
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    ///     端口
    /// </summary>
    public ushort Port { get; set; }

    /// <summary>
    ///     倍率
    /// </summary>
    public double Rate { get; } = 1.0;

    /// <summary>
    ///     备注
    /// </summary>
    public string Remark { get; set; } = "";

    /// <summary>
    ///     代理类型
    /// </summary>
    [JsonPropertyOrder(int.MinValue)]
    public abstract string Type { get; }

    public object Clone()
    {
        return MemberwiseClone();
    }

    /// <summary>
    ///     获取备注
    /// </summary>
    /// <returns>备注</returns>
    public override string ToString()
    {
        var remark = string.IsNullOrWhiteSpace(Remark) ? $"{Hostname}:{Port}" : Remark;

        var shortName = ServerHelper.GetUtilByTypeName(Type).ShortName;

        return $"[{shortName}][{Group}] {remark}";
    }

    public abstract string MaskedData();

    /// <summary>
    ///     回填测速结果（Clash API delay 测试经出站实测后写入）
    /// </summary>
    public void SetDelay(int delay)
    {
        Delay = delay;
        DelaySource = DelaySource.ClashApi;
    }

    /// <summary>
    ///     测试延迟
    /// </summary>
    /// <returns>延迟</returns>
    public async Task<int> PingAsync()
    {
        try
        {
            var destination = await ServerDnsResolver.ResolveAsync(this);
            if (destination == null)
                return RecordTcpingDelay(-2);

            var list = new Task<int>[3];
            for (var i = 0; i < 3; i++)
            {
                Task<int> PingCoreAsync()
                {
                    try
                    {
                        return Global.Settings.ServerTCPing ? Utils.Utils.TCPingAsync(destination, Port) : Utils.Utils.ICMPingAsync(destination);
                    }
                    catch (Exception)
                    {
                        return Task.FromResult(-4);
                    }
                }

                list[i] = PingCoreAsync();
            }

            var resTask = await Task.WhenAny(list[0], list[1], list[2]);

            return RecordTcpingDelay(await resTask);
        }
        catch (Exception)
        {
            return RecordTcpingDelay(-4);
        }
    }

    private int RecordTcpingDelay(int delay)
    {
        Delay = delay;
        DelaySource = DelaySource.TCPing;
        return delay;
    }
}

public static class ServerExtension
{
    /// <summary>
    ///     解析节点连接地址（订阅级 Hosts 映射/DoH 在此生效，见 ServerDnsResolver）；
    ///     全链路失败显式报错（旧实现对 null 直接 ! 解引用 NRE，无从排查）
    /// </summary>
    public static async Task<string> AutoResolveHostnameAsync(this Server server, AddressFamily inet = AddressFamily.Unspecified)
    {
        var address = await ServerDnsResolver.ResolveAsync(server, inet);
        if (address == null)
            throw new MessageException(I18N.TF("kernel.serverHostnameResolveFailed0", server.Hostname));

        return address.ToString();
    }

    public static bool IsInGroup(this Server server)
    {
        return server.Group is not Constants.DefaultGroup;
    }
}