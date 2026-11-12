using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Netch.Models;

/// <summary>
///     连接表行（双数据源统一模型）：进程页来自 Redirector 连接表快照，内核页来自 Clash API /connections。
///     进程/内核页为长寿命行对象（按 Id 就地更新属性并通知），支撑 DataGrid 表头排序稳定与流速差分。
/// </summary>
public class ConnectionInfo : INotifyPropertyChanged
{
    private string _process = "";

    private string _target = "";

    private string _rule = "";

    private string _chains = "";

    private ulong _up;

    private ulong _down;

    private ulong _upSpeed;

    private ulong _downSpeed;

    private DateTime _start;

    private string _geo = "";

    /// <summary>Redirector 页为 NF endpoint_id；内核页为内核连接 UUID</summary>
    public string Id { get; set; } = "";

    public string Pid { get; set; } = "";

    public string Process
    {
        get => _process;
        set => SetField(ref _process, value);
    }

    /// <summary>目标地址:端口（进程页是原始目标；内核页是嗅探域名或目标）</summary>
    public string Target
    {
        get => _target;
        set => SetField(ref _target, value);
    }

    public string Network { get; set; } = "";

    /// <summary>内核页：命中规则（类型 + payload）</summary>
    public string Rule
    {
        get => _rule;
        set => SetField(ref _rule, value);
    }

    /// <summary>内核页：出站链（如 proxy->node-0 / direct）</summary>
    public string Chains
    {
        get => _chains;
        set => SetField(ref _chains, value);
    }

    /// <summary>累计上行字节（Redirector 页 tcpSend 口径；内核页 Clash API upload 字段）</summary>
    public ulong Up
    {
        get => _up;
        set => SetField(ref _up, value);
    }

    /// <summary>累计下行字节（Redirector 页含泵写回补记；内核页 Clash API download 字段）</summary>
    public ulong Down
    {
        get => _down;
        set => SetField(ref _down, value);
    }

    /// <summary>上行流速（字节/秒；差分写入，1Hz 快照或内核推送间隔）</summary>
    public ulong UpSpeed
    {
        get => _upSpeed;
        set => SetField(ref _upSpeed, value);
    }

    /// <summary>下行流速（字节/秒）</summary>
    public ulong DownSpeed
    {
        get => _downSpeed;
        set => SetField(ref _downSpeed, value);
    }

    /// <summary>归属地显示名（GeoLookup：v4/v6 统一 ip2region 国家/省/市/ISP；行入表时查一次；库缺失为空串）</summary>
    public string Geo
    {
        get => _geo;
        set => SetField(ref _geo, value);
    }

    /// <summary>远端地址（ip:port）。进程页与 Target 同值；内核页为嗅探域名之外的真实 IP 目标（新增行时赋值恒定）</summary>
    public string RemoteText { get; set; } = "";

    public ulong AgeMs { get; set; }

    public DateTime Start
    {
        get => _start;
        set => SetField(ref _start, value);
    }

    public bool IsUdp { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
///     Clash API GET /connections 响应模型（字段名以实测为准，显式 JsonPropertyName）
/// </summary>
public class ClashConnectionsResponse
{
    [JsonPropertyName("downloadTotal")]
    public long DownloadTotal { get; set; }

    [JsonPropertyName("uploadTotal")]
    public long UploadTotal { get; set; }

    [JsonPropertyName("connections")]
    public List<ClashConnection> Connections { get; set; } = new();
}

public class ClashConnection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("upload")]
    public long Upload { get; set; }

    [JsonPropertyName("download")]
    public long Download { get; set; }

    [JsonPropertyName("start")]
    public DateTime Start { get; set; }

    [JsonPropertyName("chains")]
    public List<string> Chains { get; set; } = new();

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("rulePayload")]
    public string RulePayload { get; set; } = "";

    [JsonPropertyName("metadata")]
    public ClashConnectionMetadata Metadata { get; set; } = new();

    /// <summary>目标显示（嗅探域名优先，回落 IP:端口；连接观测历史页绑定用）</summary>
    [JsonIgnore]
    public string TargetText => !string.IsNullOrEmpty(Metadata.Host)
        ? Metadata.Host
        : $"{Metadata.DestinationIP}:{Metadata.DestinationPort}";
}

public class ClashConnectionMetadata
{
    [JsonPropertyName("network")]
    public string Network { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("sourceIP")]
    public string SourceIP { get; set; } = "";

    [JsonPropertyName("sourcePort")]
    public string SourcePort { get; set; } = "";

    [JsonPropertyName("destinationIP")]
    public string DestinationIP { get; set; } = "";

    [JsonPropertyName("destinationPort")]
    public string DestinationPort { get; set; } = "";

    /// <summary>嗅探域名（无嗅探为 null）</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }
}
