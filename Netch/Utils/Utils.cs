using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace Netch.Utils;

public static class Utils
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Open \"{Uri}\" failed", path);
        }
    }

    public static async Task<int> TCPingAsync(IPAddress ip, int port, int timeout = 1000, CancellationToken ct = default)
    {
        using var client = new TcpClient(ip.AddressFamily);

        var stopwatch = Stopwatch.StartNew();

        var task = client.ConnectAsync(ip, port);

        var resTask = await Task.WhenAny(task, Task.Delay(timeout, ct));

        stopwatch.Stop();
        if (resTask == task && client.Connected)
        {
            var t = Convert.ToInt32(stopwatch.Elapsed.TotalMilliseconds);
            return t;
        }

        return timeout;
    }

    public static async Task<int> ICMPingAsync(IPAddress ip, int timeout = 1000)
    {
        // P-6⑤：Ping 持有非托管句柄，批量测速（每节点 3 连发）下不释放会累积
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(ip, timeout);

        if (reply.Status == IPStatus.Success)
            return Convert.ToInt32(reply.RoundtripTime);

        return timeout;
    }

    public static async Task<string> GetCityCodeAsync(string address)
    {
        // ip:port / [v6]:port / 裸 IP 统一提取（GeoLookup.TryExtractIp 同源，旧 IndexOf(':') 会截坏 v6）
        string? country = null;
        try
        {
            // country.mmdb 已退役（2026-10-30）：国家码统一走 ip2region（ISO2 段，v4/v6 双库全内存）
            if (GeoLookup.TryExtractIp(address, out var ipText) && IPAddress.TryParse(ipText, out var ip))
            {
                country = Ip2Region.LookupIso2(ip);
            }
            else
            {
                var dnsResult = await DnsUtils.LookupAsync(address);

                if (dnsResult != null)
                    country = Ip2Region.LookupIso2(dnsResult);
            }
        }
        catch
        {
            // ignored
        }

        return string.IsNullOrEmpty(country) ? "Unknown" : country;
    }

    public static async Task<string> Sha256CheckSumAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return "";

        try
        {
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await Sha256ComputeCoreAsync(fileStream);
        }
        catch (Exception e)
        {
            Log.Warning(e, $"Compute file \"{filePath}\" sha256 failed");
            return "";
        }
    }

    private static async Task<string> Sha256ComputeCoreAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    public static string GetFileVersion(string file)
    {
        if (File.Exists(file))
            return FileVersionInfo.GetVersionInfo(file).FileVersion ?? "";

        return "";
    }



    public static void RegisterNetchStartupItem()
    {
        const string TaskName = "Netch Startup";
        var folder = TaskService.Instance.GetFolder("\\");
        var taskIsExists = folder.Tasks.Any(task => task.Name == TaskName);

        if (Global.Settings.RunAtStartup)
        {
            if (taskIsExists)
                folder.DeleteTask(TaskName, false);

            var td = TaskService.Instance.NewTask();

            td.RegistrationInfo.Author = "Netch";
            td.RegistrationInfo.Description = "Netch run at startup.";
            td.Principal.RunLevel = TaskRunLevel.Highest;

            td.Triggers.Add(new LogonTrigger());
            td.Actions.Add(new ExecAction(Global.NetchExecutable));

            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.IdleSettings.StopOnIdleEnd = false;
            td.Settings.IdleSettings.RestartOnIdle = false;
            td.Settings.RunOnlyIfIdle = false;
            td.Settings.Compatibility = TaskCompatibility.V2_1;

            TaskService.Instance.RootFolder.RegisterTaskDefinition("Netch Startup", td);
        }
        else
        {
            if (taskIsExists)
                folder.DeleteTask(TaskName, false);
        }
    }


    public static int SubnetToCidr(string value)
    {
        var subnet = IPAddress.Parse(value);
        return SubnetToCidr(subnet);
    }

    public static int SubnetToCidr(IPAddress subnet)
    {
        return subnet.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
    }

    public static string GetHostFromUri(string str)
    {
        var startIndex = str.LastIndexOf('/');
        if (startIndex != -1)
            str = str[(startIndex + 1)..];

        // 低危⑬（2026-11-04 审查）：v6 字面量按第一个冒号截断会只剩开头段；
        // 复用 SplitHostPort 的端口拆分语义（多冒号整体视为主机名）
        var (host, _) = DnsUtils.SplitHostPort(str, 0);
        return host;
    }

    /// <summary>
    ///     字节数人类可读（原始值语义保留：仅换算显示，不改变数据本身）
    /// </summary>
    public static string FormatBytes(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

}