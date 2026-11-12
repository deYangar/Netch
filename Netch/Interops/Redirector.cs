using System.Runtime.InteropServices;
using Netch.Models;

namespace Netch.Interops;

public static class Redirector
{
    public enum NameList
    {
        AIO_FILTERLOOPBACK,
        AIO_FILTERINTRANET, // LAN
        AIO_FILTERPARENT,
        AIO_FILTERICMP,
        AIO_FILTERTCP,
        AIO_FILTERUDP,
        AIO_FILTERDNS,

        AIO_ICMPING,

        AIO_DNSONLY,
        AIO_DNSPROX,
        AIO_DNSHOST,
        AIO_DNSPORT,

        AIO_TGTHOST,
        AIO_TGTPORT,
        AIO_TGTUSER,
        AIO_TGTPASS,

        AIO_CLRNAME,
        AIO_ADDNAME,
        AIO_BYPNAME,

        // 新增常量必须追加在枚举尾部（与 Redirector/Based.h 的 AIO_TYPE 序号一一对应）
        AIO_DNSKEEP,
        AIO_VERBOSE,

        /// <summary>"iocp"（默认）或 "legacy"：转发泵选择，运行期回退开关</summary>
        AIO_PUMP
    }

    public static bool Dial(NameList name, bool value)
    {
        Log.Verbose($"[Redirector] Dial {name}: {value}");
        return aio_dial(name, value.ToString().ToLower());
    }

    public static bool Dial(NameList name, string value)
    {
        Log.Verbose($"[Redirector] Dial {name}: {value}");
        return aio_dial(name, value);
    }

    public static Task<bool> InitAsync()
    {
        return Task.Run(aio_init);
    }

    public static Task<bool> FreeAsync()
    {
        return Task.Run(aio_free);
    }

    private const string Redirector_bin = "Redirector.bin";

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool aio_register([MarshalAs(UnmanagedType.LPWStr)] string value);

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool aio_unregister([MarshalAs(UnmanagedType.LPWStr)] string value);

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool aio_dial(NameList name, [MarshalAs(UnmanagedType.LPWStr)] string value);

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool aio_init();

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool aio_free();

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong aio_getUP();

    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong aio_getDL();

    /// <summary>
    ///     连接表快照导出：行文本协议 "T|id|pid|process|target|up|dl|age_ms"，返回字节数；-1 = 缓冲不足需扩容重试
    /// </summary>
    [DllImport(Redirector_bin, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I4)]
    public static extern int aio_export_connections(byte[] buffer, int capacity);

    /// <summary>
    ///     连接表导出复用缓冲（P-2，2026-11-04 审查）：连接页 1Hz 轮询常驻，此前每秒新分配 64KB
    ///     （必要时 512KB 进 LOH）——纯 P/Invoke 写入缓冲，天然可复用；lock 防未来并发调用方
    /// </summary>
    private static readonly object ExportBufferLock = new();

    private static byte[] _exportBuffer = new byte[64 * 1024];

    /// <summary>
    ///     拉取 Redirector 连接表快照（解析行文本协议）；DLL 未加载/无数据返回空表
    /// </summary>
    public static List<ConnectionInfo> ExportConnections()
    {
        int size;
        lock (ExportBufferLock)
        {
            try
            {
                size = aio_export_connections(_exportBuffer, _exportBuffer.Length);
                if (size == -1)
                {
                    // 超容量：换大缓冲重试一次并保留复用（后续轮询不再重新扩容）
                    _exportBuffer = new byte[512 * 1024];
                    size = aio_export_connections(_exportBuffer, _exportBuffer.Length);
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Export connections failed (Redirector not loaded?)");
                return new List<ConnectionInfo>();
            }

            if (size <= 0)
                return new List<ConnectionInfo>();

            return ParseConnections(_exportBuffer, size);
        }
    }

    private static List<ConnectionInfo> ParseConnections(byte[] buffer, int size)
    {
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, size);
        var result = new List<ConnectionInfo>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 8)
                continue;

            result.Add(new ConnectionInfo
            {
                Id = parts[1],
                Pid = parts[2],
                Process = parts[3],
                Target = parts[4],
                RemoteText = parts[4],
                Up = ulong.Parse(parts[5]),
                Down = ulong.Parse(parts[6]),
                AgeMs = ulong.Parse(parts[7]),
                IsUdp = parts[0] == "U"
            });
        }

        return result;
    }
}