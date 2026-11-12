using System.Buffers.Binary;
using System.Net;

namespace Netch.Utils;

/// <summary>
///     ip2region xdb v3 离线查询（IPv4 + IPv6，国家|省份|城市|ISP|ISO2 五段格式）。
///     官方数据 lionsoul2014/ip2region（v4 走 jsDelivr 外部资源通道；v6 库 37MB 超 jsDelivr 20MB 限制，
///     GitHub raw 直链下载 + 设置页「外部资源经内置代理下载」兜底，bin\ip2region.xdb / bin\ip2region_v6.xdb 全内存）：
///     国内段全中文，国外段国家/省市为英文 + ISO2 码（由内置 ISO 映射表转中文国名）。
///     查询算法与 ProcessNetMonitor 的 ip_geo.cpp 同源（v4：256B 头 + vIndex 二分 + 14B 段索引小端；
///     v6：同 vIndex + 38B 段索引，IP 字段大端原样 memcmp，dataLen@32/dataPtr@34）。
///     文件缺失/损坏按未加载处理返回空串（连接页每秒调用，失败不抛错不入日志噪音）。
/// </summary>
public static class Ip2Region
{
    private static readonly object Lock = new();
    private static byte[]? _buffer;
    private static bool _loadTried;
    private static byte[]? _buffer6;
    private static bool _loadTried6;

    /// <summary>xdb 结构常量：头 256B + vIndex 512KiB；v4 段索引 14B（IP 小端），v6 段索引 38B（IP 大端）</summary>
    private const int HeaderSize = 256;
    private const int VectorIndexSize = 512 * 1024;
    private const int SegmentIndexSize = 14;
    private const int SegmentIndexSize6 = 38;

    /// <summary>ISO2 → 中文国名（来源 ProcessNetMonitor ip_geo.cpp kIsoCn，139 条）</summary>
    internal static readonly Dictionary<string, string> IsoChineseNames = new(StringComparer.Ordinal)
    {
        ["CN"] = "中国",
        ["US"] = "美国",
        ["JP"] = "日本",
        ["KR"] = "韩国",
        ["KP"] = "朝鲜",
        ["GB"] = "英国",
        ["DE"] = "德国",
        ["FR"] = "法国",
        ["RU"] = "俄罗斯",
        ["AU"] = "澳大利亚",
        ["CA"] = "加拿大",
        ["SG"] = "新加坡",
        ["IN"] = "印度",
        ["NL"] = "荷兰",
        ["SE"] = "瑞典",
        ["NO"] = "挪威",
        ["FI"] = "芬兰",
        ["DK"] = "丹麦",
        ["CH"] = "瑞士",
        ["IT"] = "意大利",
        ["ES"] = "西班牙",
        ["PT"] = "葡萄牙",
        ["BE"] = "比利时",
        ["AT"] = "奥地利",
        ["IE"] = "爱尔兰",
        ["PL"] = "波兰",
        ["CZ"] = "捷克",
        ["SK"] = "斯洛伐克",
        ["HU"] = "匈牙利",
        ["RO"] = "罗马尼亚",
        ["BG"] = "保加利亚",
        ["GR"] = "希腊",
        ["TR"] = "土耳其",
        ["UA"] = "乌克兰",
        ["BY"] = "白俄罗斯",
        ["KZ"] = "哈萨克斯坦",
        ["UZ"] = "乌兹别克斯坦",
        ["AE"] = "阿联酋",
        ["SA"] = "沙特阿拉伯",
        ["IL"] = "以色列",
        ["IR"] = "伊朗",
        ["PK"] = "巴基斯坦",
        ["BD"] = "孟加拉国",
        ["TH"] = "泰国",
        ["VN"] = "越南",
        ["MY"] = "马来西亚",
        ["ID"] = "印尼",
        ["PH"] = "菲律宾",
        ["NZ"] = "新西兰",
        ["MX"] = "墨西哥",
        ["BR"] = "巴西",
        ["AR"] = "阿根廷",
        ["CL"] = "智利",
        ["CO"] = "哥伦比亚",
        ["PE"] = "秘鲁",
        ["ZA"] = "南非",
        ["EG"] = "埃及",
        ["NG"] = "尼日利亚",
        ["KE"] = "肯尼亚",
        ["MA"] = "摩洛哥",
        ["HK"] = "中国香港",
        ["MO"] = "中国澳门",
        ["TW"] = "中国台湾",
        ["CY"] = "塞浦路斯",
        ["IS"] = "冰岛",
        ["LT"] = "立陶宛",
        ["LV"] = "拉脱维亚",
        ["EE"] = "爱沙尼亚",
        ["SI"] = "斯洛文尼亚",
        ["HR"] = "克罗地亚",
        ["RS"] = "塞尔维亚",
        ["GE"] = "格鲁吉亚",
        ["AM"] = "亚美尼亚",
        ["AZ"] = "阿塞拜疆",
        ["MN"] = "蒙古",
        ["CU"] = "古巴",
        ["VE"] = "委内瑞拉",
        ["QA"] = "卡塔尔",
        ["KW"] = "科威特",
        ["OM"] = "阿曼",
        ["LK"] = "斯里兰卡",
        ["NP"] = "尼泊尔",
        ["MM"] = "缅甸",
        ["KH"] = "柬埔寨",
        ["LA"] = "老挝",
        ["TJ"] = "塔吉克斯坦",
        ["KG"] = "吉尔吉斯斯坦",
        ["TM"] = "土库曼斯坦",
        ["MD"] = "摩尔多瓦",
        ["AL"] = "阿尔巴尼亚",
        ["MK"] = "北马其顿",
        ["BA"] = "波黑",
        ["LU"] = "卢森堡",
        ["MC"] = "摩纳哥",
        ["MT"] = "马耳他",
        ["AD"] = "安道尔",
        ["LI"] = "列支敦士登",
        ["FJ"] = "斐济",
        ["PG"] = "巴布亚新几内亚",
        ["TT"] = "特立尼达和多巴哥",
        ["JM"] = "牙买加",
        ["DO"] = "多米尼加",
        ["GT"] = "危地马拉",
        ["SV"] = "萨尔瓦多",
        ["HN"] = "洪都拉斯",
        ["NI"] = "尼加拉瓜",
        ["CR"] = "哥斯达黎加",
        ["PA"] = "巴拿马",
        ["EC"] = "厄瓜多尔",
        ["BO"] = "玻利维亚",
        ["PY"] = "巴拉圭",
        ["UY"] = "乌拉圭",
        ["TN"] = "突尼斯",
        ["DZ"] = "阿尔及利亚",
        ["LY"] = "利比亚",
        ["SD"] = "苏丹",
        ["ET"] = "埃塞俄比亚",
        ["TZ"] = "坦桑尼亚",
        ["UG"] = "乌干达",
        ["GH"] = "加纳",
        ["CI"] = "科特迪瓦",
        ["SN"] = "塞内加尔",
        ["CM"] = "喀麦隆",
        ["ZW"] = "津巴布韦",
        ["MZ"] = "莫桑比克",
        ["AO"] = "安哥拉",
        ["NA"] = "纳米比亚",
        ["BW"] = "博茨瓦纳",
        ["ZM"] = "赞比亚",
        ["MW"] = "马拉维",
        ["MG"] = "马达加斯加",
        ["MU"] = "毛里求斯",
        ["SC"] = "塞舌尔",
        ["CV"] = "佛得角",
        ["HT"] = "海地",
        ["PR"] = "波多黎各",
        ["GU"] = "关岛",
        ["PF"] = "法属波利尼西亚",
        ["IO"] = "英属印度洋领地",
    };

    /// <summary>IPv4 整数 → 归属地显示名；未加载/未命中返回空串</summary>
    public static string Lookup(uint address)
    {
        lock (Lock)
        {
            var buffer = GetBuffer();
            if (buffer == null)
                return "";

            try
            {
                return FormatRegion(Search(buffer, address));
            }
            catch
            {
                // 库坏/越界：按未命中处理（外层 GeoLookup 缓存兜住，避免同 IP 每秒重查）
                return "";
            }
        }
    }

    /// <summary>
    ///     IPAddress → 归属地显示名（IPv4/IPv6/v4 映射地址自动分支）；
    ///     未加载/未命中返回空串
    /// </summary>
    public static string Lookup(IPAddress address)
    {
        return FormatRegion(SearchFor(address));
    }

    /// <summary>IPAddress → ISO2 国家码（region 第 5 段；未命中返回空串，GetCityCodeAsync 消费）</summary>
    public static string LookupIso2(IPAddress address)
    {
        return GetIso2(SearchFor(address));
    }

    /// <summary>按地址族路由到 v4/v6 库；::ffff:x.x.x.x 映射地址转 v4 查询（ProcessNetMonitor 同款）</summary>
    private static string SearchFor(IPAddress address)
    {
        lock (Lock)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
                return Search(GetBuffer(), ToUInt32(bytes));

            if (bytes.Length == 16)
            {
                return IsV4Mapped(bytes)
                    ? Search(GetBuffer(), ToUInt32(bytes.AsSpan(12).ToArray()))
                    : SearchV6(GetBuffer6(), bytes);
            }

            return "";
        }
    }

    private static bool IsV4Mapped(byte[] bytes)
    {
        for (var i = 0; i < 10; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        return bytes[10] == 0xFF && bytes[11] == 0xFF;
    }

    private static uint ToUInt32(byte[] bytes)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
    }

    /// <summary>重置加载态（外部资源页更换/删除库后强制重载）</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _buffer = null;
            _loadTried = false;
            _buffer6 = null;
            _loadTried6 = false;
        }
    }

    private static byte[]? GetBuffer()
    {
        if (_buffer != null || _loadTried)
            return _buffer;

        _loadTried = true;
        return _buffer = LoadXdb(@"bin\ip2region.xdb", isV6: false);
    }

    private static byte[]? GetBuffer6()
    {
        if (_buffer6 != null || _loadTried6)
            return _buffer6;

        _loadTried6 = true;
        return _buffer6 = LoadXdb(@"bin\ip2region_v6.xdb", isV6: true);
    }

    private static byte[]? LoadXdb(string path, bool isV6)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var buffer = File.ReadAllBytes(path);
            if (!IsHeaderValid(buffer, isV6))
            {
                Log.Warning("ip2region database rejected: {Path} ({Bytes})", path, buffer.Length);
                return null;
            }

            return buffer;
        }
        catch (Exception e)
        {
            Log.Warning(e, "ip2region database load failed: {Path}", path);
            return null;
        }
    }

    /// <summary>
    ///     头校验（下载管线与加载共用）：版本非零 + vIndex 边界自洽。
    ///     v6 库额外要求 v3 结构 + 头偏移 16 的 ipVersion==6（防 v4 文件误装进 v6 槽位，反之亦然）。
    /// </summary>
    internal static bool IsHeaderValid(byte[] buffer, bool isV6 = false)
    {
        if (buffer.Length < HeaderSize + VectorIndexSize)
            return false;

        var version = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var indexStart = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
        var indexEnd = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12));
        var valid = version is 2 or 3
                    && indexStart > 0
                    && indexEnd <= (uint)buffer.Length
                    && indexStart < indexEnd;

        if (valid && isV6)
        {
            // 旧 v2 结构无 ipVersion 字段（仅 IPv4）；v3 结构头偏移 16 为 ipVersion
            valid = version == 3
                    && BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(16)) == 6;
        }

        return valid;
    }

    /// <summary>vIndex 二分（ProcessNetMonitor ip_geo.cpp XdbSearch 同源，v4 段索引 IP 字段小端）</summary>
    private static string Search(byte[]? buffer, uint address)
    {
        if (buffer == null)
            return "";


        var index = ((address >> 16) & 0xFFFF) * 8;
        var startPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((int)(HeaderSize + index)));
        var endPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((int)(HeaderSize + index + 4)));
        if (startPtr == 0 || endPtr == 0 || endPtr > buffer.Length || startPtr >= endPtr)
            return "";

        uint lo = 0;
        uint hi = (endPtr - startPtr) / SegmentIndexSize;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var pos = (int)(startPtr + mid * SegmentIndexSize);
            var segStart = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
            var segEnd = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 4));
            if (address < segStart)
                hi = mid;
            else if (address > segEnd)
                lo = mid + 1;
            else
            {
                var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 8));
                var dataPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 10));
                if (dataLen == 0 || (ulong)dataPtr + dataLen > (ulong)buffer.Length)
                    return "";

                return System.Text.Encoding.UTF8.GetString(buffer, (int)dataPtr, dataLen);
            }
        }

        return "";
    }

    /// <summary>
    ///     v6 vIndex 二分（ProcessNetMonitor ip_geo.cpp XdbSearchV6 同源）：
    ///     38B 段索引 = [16B start_ip][16B end_ip][2B data_len][4B data_ptr]，IP 字段大端原样比较。
    /// </summary>
    private static string SearchV6(byte[]? buffer, byte[] address)
    {
        if (buffer == null)
            return "";


        var index = (address[0] * 256 + address[1]) * 8;
        var startPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(HeaderSize + index));
        var endPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(HeaderSize + index + 4));
        if (startPtr == 0 || endPtr == 0 || endPtr > (uint)buffer.Length || startPtr >= endPtr)
            return "";

        uint lo = 0;
        uint hi = (endPtr - startPtr) / SegmentIndexSize6;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var pos = (int)(startPtr + mid * SegmentIndexSize6);
            var cmpStart = address.AsSpan(0, 16).SequenceCompareTo(buffer.AsSpan(pos, 16));
            if (cmpStart < 0)
            {
                hi = mid;
            }
            else
            {
                var cmpEnd = address.AsSpan(0, 16).SequenceCompareTo(buffer.AsSpan(pos + 16, 16));
                if (cmpEnd > 0)
                    lo = mid + 1;
                else
                {
                    var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 32));
                    var dataPtr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 34));
                    if (dataLen == 0 || (ulong)dataPtr + dataLen > (ulong)buffer.Length)
                        return "";

                    return System.Text.Encoding.UTF8.GetString(buffer, (int)dataPtr, dataLen);
                }
            }
        }

        return "";
    }

    /// <summary>
    ///     五段 region → 「国家 省 市 ISP」（空段与字面 "0" 跳过）。
    ///     国家名：ISO2 命中映射表用中文，否则用原文（国内数据 country 本身即中文）。
    /// </summary>
    internal static string FormatRegion(string region)
    {
        if (string.IsNullOrEmpty(region))
            return "";

        var parts = region.Split('|');
        if (parts.Length < 5)
            return parts.Length > 0 && parts[0] is not ("0" or "") ? parts[0] : "";

        var country = Clean(parts[0]);
        if (country == "")
            return "";

        var name = IsoChineseNames.TryGetValue(parts[4], out var zh) ? zh : country;
        var prov = Clean(parts[1]);
        var city = Clean(parts[2]);
        var isp = Clean(parts[3]);

        if (prov != "")
            name += " " + prov;
        if (city != "")
            name += " " + city;
        if (isp != "")
            name += " " + isp;
        return name;
    }

    /// <summary>region 第 5 段 ISO2 码（不足五段/空返回空串）</summary>
    private static string GetIso2(string region)
    {
        if (string.IsNullOrEmpty(region))
            return "";

        var parts = region.Split('|');
        return parts.Length >= 5 ? parts[4] : "";
    }

    private static string Clean(string part)
    {
        return part is "" or "0" ? "" : part;
    }
}
