using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Netch.Utils;

namespace Tests;

/// <summary>
///     ip2region v3 五段 region 格式化单测（纯字符串逻辑，不触库文件）。
///     数据样例来自官方库探针实测：88.221.123.186 → Germany|Hamburg|Hamburg|Akamai|DE。
/// </summary>
[TestClass]
public class Ip2RegionTests
{
    [TestMethod]
    public void FormatRegion_China_KeepsChineseSegments()
    {
        var result = Ip2Region.FormatRegion("中国|广东省|广州市|联通|CN");
        Assert.AreEqual("中国 广东省 广州市 联通", result);
    }

    [TestMethod]
    public void FormatRegion_Foreign_TranslatesCountryByIso()
    {
        var result = Ip2Region.FormatRegion("Germany|Hamburg|Hamburg|Akamai|DE");
        Assert.AreEqual("德国 Hamburg Hamburg Akamai", result);
    }

    [TestMethod]
    public void FormatRegion_Foreign_ZeroSegments_CountryOnly()
    {
        var result = Ip2Region.FormatRegion("India|0|0|0|IN");
        Assert.AreEqual("印度", result);
    }

    [TestMethod]
    public void FormatRegion_UnknownIso_FallsBackToRawCountry()
    {
        var result = Ip2Region.FormatRegion("Atlantis|0|0|0|XX");
        Assert.AreEqual("Atlantis", result);
    }

    [TestMethod]
    public void FormatRegion_EmptyAndShortInputs()
    {
        Assert.AreEqual("", Ip2Region.FormatRegion(""));
        Assert.AreEqual("", Ip2Region.FormatRegion("0|0|0|0|0"));
        // 少于五段：防御性兜底只返回国家原文（正常数据恒五段，此分支不翻译）
        Assert.AreEqual("Germany", Ip2Region.FormatRegion("Germany|Hamburg"));
        Assert.AreEqual("", Ip2Region.FormatRegion("0"));
    }

    [TestMethod]
    public void HeaderValid_RejectsTruncatedAndBadVersion()
    {
        Assert.IsFalse(Ip2Region.IsHeaderValid(new byte[256]));
        var header = new byte[256 + 512 * 1024 + 1024];
        // 版本 0 无效
        Assert.IsFalse(Ip2Region.IsHeaderValid(header));
        // 版本 3 + 合法 vIndex 边界（段索引起止落在文件范围内）
        header[0] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 256 + 512 * 1024);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 256 + 512 * 1024 + 14);
        Assert.IsTrue(Ip2Region.IsHeaderValid(header));
    }

    [TestMethod]
    public void HeaderValid_V6Slot_RequiresStructure3AndIpVersion6()
    {
        // v6 槽位强校验：防 v4 文件误装（头偏移 16 的 ipVersion 字段，ProcessNetMonitor ReadXdbFile 同款判定）
        var header = new byte[256 + 512 * 1024 + 1024];
        header[0] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 256 + 512 * 1024);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 256 + 512 * 1024 + 38);

        // 缺 ipVersion 字段 / 写成 4：v6 槽位拒绝（v4 槽位放行，旧 v2 结构无此字段）
        Assert.IsFalse(Ip2Region.IsHeaderValid(header, isV6: true));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 4);
        Assert.IsFalse(Ip2Region.IsHeaderValid(header, isV6: true));
        Assert.IsTrue(Ip2Region.IsHeaderValid(header, isV6: false));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 6);
        Assert.IsTrue(Ip2Region.IsHeaderValid(header, isV6: true));
        // v2 旧结构（无 ipVersion）即使伪造字段 6 也进不了 v6 槽位
        header[0] = 2;
        Assert.IsFalse(Ip2Region.IsHeaderValid(header, isV6: true));
    }

    [TestMethod]
    public void FormatRegion_V6Segments_SameFivePartContract()
    {
        // v6 与 v4 同构五段（探针实测 2400:3200::1 → 中国|浙江省|杭州市|阿里|CN，
        // 2606:4700:4700::1111 → United Kingdom|England|London|Cloudflare, Inc.|GB）
        Assert.AreEqual("中国 浙江省 杭州市 阿里", Ip2Region.FormatRegion("中国|浙江省|杭州市|阿里|CN"));
        Assert.AreEqual("英国 England London Cloudflare, Inc.", Ip2Region.FormatRegion("United Kingdom|England|London|Cloudflare, Inc.|GB"));
        Assert.AreEqual("日本 Tokyo Tokyo WIDE Project", Ip2Region.FormatRegion("Japan|Tokyo|Tokyo|WIDE Project|JP"));
    }
}
