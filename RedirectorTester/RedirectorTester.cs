// Redirector 数据面自动验收（M3-1 基线）：
// 本进程发起真实连接 -> NF 驱动改写 -> Redirector 数据面 -> 本地 SOCKS5 stub -> 本地落地服务。
// 前置：管理员运行（装驱动 netfilter2.sys 与 Redirector.bin 同目录）。
// 用例与断言对应 技术改造方案/M3-1 §3：
//   T1 128 并发 TCP 随机 payload 双向字节严格一致
//   T2 上游拒绝（SOCKS 目标无监听）→ 应用 ≤1.6s 得 WSAECONNRESET（#582）
//   T3 半关闭：应用 shutdown(SD_SEND) 后仍能收完上游回包
//   T4 UDP 双向 1024 pps × 20s 无丢失回包端点正确
//   T5 同源端口快速复用 ×1000 无错乱（A2 索引回归）
//   T6 DNS 上游无响应 → ≤3s 内 SERVFAIL 回包（A6）
//   T7 超长凭据（>255）连接被拒非截断（A6）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RedirectorTester
{
    public class RedirectorTester
    {
        // 与 Redirector/Based.h AIO_TYPE 序号一一对应（20 项）
        public enum NameList : int
        {
            AIO_FILTERLOOPBACK,
            AIO_FILTERINTRANET,
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
            AIO_DNSKEEP,
            AIO_VERBOSE,
            AIO_PUMP
        }

        [DllImport("Redirector.bin", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool aio_register([MarshalAs(UnmanagedType.LPWStr)] string value);

        [DllImport("Redirector.bin", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool aio_unregister([MarshalAs(UnmanagedType.LPWStr)] string value);

        [DllImport("Redirector.bin", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool aio_dial(NameList name, [MarshalAs(UnmanagedType.LPWStr)] string value);

        [DllImport("Redirector.bin", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool aio_init();

        [DllImport("Redirector.bin", CallingConvention = CallingConvention.Cdecl)]
        public static extern void aio_free();

        private static readonly List<string> Results = new List<string>();
        private static int _failed;

        private static void Check(string name, bool ok, string detail = "")
        {
            Results.Add((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok)
                _failed++;
            Console.WriteLine(Results[Results.Count - 1]);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // ---- 本地落地 echo server（TCP：收啥回啥；半关闭感知） ----
        private static void StartEchoServer(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    var stream = client.GetStream();
                    var buffer = new byte[256 * 1024];
                    try
                    {
                        int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                            stream.Write(buffer, 0, n); // echo（Read 返回 0 = 对端 FIN，半关闭下回写完自动结束）
                    }
                    catch
                    {
                        // ignored
                    }
                    finally
                    {
                        client.Close();
                    }
                }
            }) { IsBackground = true }.Start();
        }

        // ---- SOCKS5 stub：无认证 + CONNECT 转发到落地；demandAuth=true 时强制要求认证 ----
        private static void StartSocksStub(int port, bool demandAuth = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    var client = listener.AcceptSocket();
                    new Thread(() => SocksSession(client, demandAuth)) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        ///     UDP ASSOCIATE 会话（与 CONNECT 同端口）：回 relay 端口并双向泵（RQ 头剥/加），落地到 udpEcho
        /// </summary>
        private static int _udpEchoPort;

        private static void SocksUdpAssociate(Socket client)
        {
            try
            {
                var req = new byte[4];
                RecvExactly(client, req, 4);
                if (req[3] == 1)
                {
                    RecvExactly(client, new byte[4], 4);
                }
                else if (req[3] == 3)
                {
                    var ln = new byte[1];
                    RecvExactly(client, ln, 1);
                    RecvExactly(client, new byte[ln[0]], ln[0]);
                }
                else
                {
                    RecvExactly(client, new byte[16], 16);
                }
                RecvExactly(client, new byte[2], 2);

                var relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var relayPort = ((IPEndPoint)relay.LocalEndPoint).Port;
                client.Send(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(relayPort >> 8), (byte)relayPort });

                // 每个关联独立的落地 socket（并发多客户端时互不串包），回包按最近一次 RQ 头封装
                var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                upstream.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                upstream.Connect(new IPEndPoint(IPAddress.Loopback, _udpEchoPort));

                var header = new byte[10];
                var replyTo = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                var headerLock = new object();

                new Thread(() =>
                {
                    var buf = new byte[65535];
                    while (true)
                    {
                        try
                        {
                            var n = upstream.Receive(buf);
                            byte[] reply;
                            EndPoint dest;
                            lock (headerLock)
                            {
                                reply = new byte[10 + n];
                                Array.Copy(header, 0, reply, 0, 10);
                                Array.Copy(buf, 0, reply, 10, n);
                                dest = replyTo;
                            }

                            relay.SendTo(reply, dest);
                        }
                        catch
                        {
                            return;
                        }
                    }
                }) { IsBackground = true }.Start();

                // 关联通道只做保活：握手阶段的 8s 收超时必须解除，否则静默 8s 就会拆掉整个关联
                client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 0);

                new Thread(() =>
                {
                    var buf = new byte[65535];
                    var remote = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        try
                        {
                            var n = relay.ReceiveFrom(buf, ref remote);
                            if (n < 10)
                                continue;

                            var payload = new byte[n - 10];
                            Array.Copy(buf, 10, payload, 0, payload.Length);

                            lock (headerLock)
                            {
                                Array.Copy(buf, 0, header, 0, 10);
                                replyTo = remote;
                            }

                            upstream.Send(payload);
                        }
                        catch
                        {
                            return;
                        }
                    }
                }) { IsBackground = true }.Start();

                var keep = new byte[1];
                while (true)
                {
                    if (client.Receive(keep) == 0)
                        return;
                }
            }
            catch
            {
                try { client.Close(); } catch { }
            }
        }

        private static void SocksSession(Socket client, bool demandAuth)
        {
            try
            {
                client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 8000);
                var header = new byte[2];
                RecvExactly(client, header, 2);
                var methods = new byte[header[1]];
                RecvExactly(client, methods, methods.Length);
                client.Send(new byte[] { 5, demandAuth ? (byte)0x02 : (byte)0x00 });

                if (demandAuth)
                {
                    // 认证子协商（Redirector 发用户名/密码）——超长凭据会被拒绝、这里只会收到合法长度
                    var ver = new byte[1];
                    RecvExactly(client, ver, 1);
                    var ulen = new byte[1];
                    RecvExactly(client, ulen, 1);
                    var user = new byte[ulen[0]];
                    RecvExactly(client, user, user.Length);
                    var plen = new byte[1];
                    RecvExactly(client, plen, 1);
                    var pass = new byte[plen[0]];
                    RecvExactly(client, pass, pass.Length);
                    client.Send(new byte[] { 1, 0 });
                }

                var req = new byte[4];
                RecvExactly(client, req, 4);
                var command = req[1];
                if (command == 3)
                {
                    // UDP ASSOCIATE: hand the session to the UDP relay on this same port
                    SocksUdpAssociate(client);
                    return;
                }

                string host;
                var atyp = req[3];
                if (atyp == 1)
                {
                    var addr = new byte[4];
                    RecvExactly(client, addr, 4);
                    host = new IPAddress(addr).ToString();
                }
                else if (atyp == 3)
                {
                    var ln = new byte[1];
                    RecvExactly(client, ln, 1);
                    var addr = new byte[ln[0]];
                    RecvExactly(client, addr, addr.Length);
                    host = Encoding.ASCII.GetString(addr);
                }
                else
                {
                    client.Close();
                    return;
                }

                var portBytes = new byte[2];
                RecvExactly(client, portBytes, 2);
                var targetPort = (portBytes[0] << 8) | portBytes[1];

                Socket upstream = null;
                try
                {
                    upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    upstream.Connect(IPAddress.Loopback, targetPort);
                    client.Send(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                }
                catch
                {
                    client.Send(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }); // SOCKS 拒绝 → Redirector RST 应用
                    client.Close();
                    return;
                }

                // 双向泵
                var upBuf = new byte[64 * 1024];
                var downBuf = new byte[64 * 1024];
                var upDone = new AutoResetEvent(false);
                var downDone = new AutoResetEvent(false);
                new Thread(() =>
                {
                    try
                    {
                        int n;
                        while ((n = upstream.Receive(upBuf)) > 0)
                            client.Send(upBuf, n, SocketFlags.None);
                    }
                    catch
                    {
                        // ignored
                    }
                    try { client.Shutdown(SocketShutdown.Send); } catch { }
                    upDone.Set();
                }) { IsBackground = true }.Start();
                new Thread(() =>
                {
                    try
                    {
                        int n;
                        while ((n = client.Receive(downBuf)) > 0)
                            upstream.Send(downBuf, n, SocketFlags.None);
                    }
                    catch
                    {
                        // ignored
                    }
                    try { upstream.Shutdown(SocketShutdown.Send); } catch { }
                    downDone.Set();
                }) { IsBackground = true }.Start();

                WaitHandle.WaitAll(new[] { upDone, downDone }, 20000);
                client.Close();
                upstream.Close();
            }
            catch
            {
                try { client.Close(); } catch { }
            }
        }

        private static void RecvExactly(Socket s, byte[] buffer, int length)
        {
            var offset = 0;
            while (offset < length)
            {
                var n = s.Receive(buffer, offset, length - offset, SocketFlags.None);
                if (n == 0)
                    throw new SocketException((int)SocketError.ConnectionAborted);
                offset += n;
            }
        }

        private static void StartUdpEcho(int port)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Loopback, port));
            new Thread(() =>
            {
                var remote = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                var buf = new byte[65535];
                while (true)
                {
                    var n = sock.ReceiveFrom(buf, ref remote);
                    sock.SendTo(buf, n, SocketFlags.None, remote);
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        ///     客户端用例跑在独立 python 子进程（本测试进程被 CurrentID 排除，自身连接不被过滤）
        /// </summary>
        private static (bool ok, string detail) RunClientCase(string python, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = arguments,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(180000))
            {
                process.Kill();
                return (false, "client case timed out");
            }

            if (stdout.Trim().Length == 0)
                return (false, "client crashed: " + (stderr.Trim().Length > 160 ? stderr.Trim()[..160] : stderr.Trim()));

            try
            {
                var json = System.Text.Json.JsonDocument.Parse(stdout.Trim());
                return (json.RootElement.GetProperty("ok").GetBoolean(), json.RootElement.GetProperty("detail").GetString() ?? "");
            }
            catch (Exception)
            {
                var trimmed = stdout.Trim();
                return (false, "bad client output: " + (trimmed.Length > 120 ? trimmed[..120] : trimmed));
            }
        }

        public static int Main(string[] args)
        {
            Console.WriteLine("== RedirectorTester (M3-1 dataplane acceptance) ==");
            Console.WriteLine("requires Administrator (driver install); args: --no-driver skips aio_register");

            var noDriver = Array.IndexOf(args, "--no-driver") >= 0;

            // 基础设施
            var echoPort = FreePort();
            StartEchoServer(echoPort);
            var socksPort = FreePort();
            StartSocksStub(socksPort);
            var udpEchoPort = FreePort();
            StartUdpEcho(udpEchoPort);
            _udpEchoPort = udpEchoPort;

            if (!noDriver && !aio_register("netfilter2"))
            {
                Console.WriteLine("driver register failed (run as Administrator, or pass --no-driver for a partial run)");
                return 2;
            }

            aio_dial(NameList.AIO_FILTERLOOPBACK, "false");
            aio_dial(NameList.AIO_FILTERINTRANET, "false"); // 测试目标含 127.0.0.1
            aio_dial(NameList.AIO_FILTERPARENT, "false");
            aio_dial(NameList.AIO_FILTERICMP, "false");
            aio_dial(NameList.AIO_FILTERTCP, "true");
            aio_dial(NameList.AIO_FILTERUDP, "true");
            aio_dial(NameList.AIO_FILTERDNS, "true");
            aio_dial(NameList.AIO_DNSPROX, "true");
            aio_dial(NameList.AIO_DNSKEEP, "true");
            aio_dial(NameList.AIO_VERBOSE, "true");
            aio_dial(NameList.AIO_TGTHOST, "127.0.0.1");
            aio_dial(NameList.AIO_TGTPORT, socksPort.ToString());
            aio_dial(NameList.AIO_TGTUSER, "");
            aio_dial(NameList.AIO_TGTPASS, "");
            aio_dial(NameList.AIO_CLRNAME, "");
            aio_dial(NameList.AIO_ADDNAME, "python"); // 客户端用例跑在 python 子进程（本进程被 CurrentID 排除）
            // T6 需要一个无响应上游；DNSHandler::INIT 只在 aio_init 时解析 dnsAddr，必须先下发
            aio_dial(NameList.AIO_DNSHOST, "127.0.0.1");
            aio_dial(NameList.AIO_DNSPORT, "59999");

            if (!aio_init())
            {
                Console.WriteLine("aio_init failed");
                return 2;
            }

            try
            {
                var python = Environment.GetEnvironmentVariable("NETCH_TEST_PYTHON");
                if (string.IsNullOrEmpty(python))
                    python = "python";

                (bool ok, string detail) r;

                r = RunClientCase(python, $"client_cases.py t2 65001");
                Check("T2 upstream refused -> WSAECONNRESET within 1.6s", r.ok, r.detail);

                r = RunClientCase(python, $"client_cases.py t1 {echoPort} 128");
                Check("T1 128-concurrent TCP strict byte echo", r.ok, r.detail);

                r = RunClientCase(python, $"client_cases.py t3 {echoPort}");
                Check("T3 half-close: payload fully echoed after shutdown(SD_SEND)", r.ok, r.detail);

                r = RunClientCase(python, $"client_cases.py t5 {echoPort} 1000");
                Check("T5 source-port fast reuse x1000 no cross-talk", r.ok, r.detail);

                r = RunClientCase(python, "client_cases.py t4 4096 1024");
                Check("T4 UDP bidirectional relay @1024pps", r.ok, r.detail);

                // T6: dnsProx=false 走 HandleClientDNS -> 死上游 127.0.0.1:59999（aio_init 前已下发）
                aio_dial(NameList.AIO_DNSPROX, "false");
                Thread.Sleep(300);
                r = RunClientCase(python, "client_cases.py t6");
                Check("T6 dead DNS upstream -> SERVFAIL within 3s (txn id kept)", r.ok, r.detail);
                aio_dial(NameList.AIO_DNSPROX, "true");

                // T7 超长凭据（>255 字节用户名）：stub 强制要求认证，数据面应拒绝并 RST
                {
                    var longPort = FreePort();
                    StartSocksStub(longPort, demandAuth: true);
                    aio_dial(NameList.AIO_TGTHOST, "127.0.0.1");
                    aio_dial(NameList.AIO_TGTPORT, longPort.ToString());
                    aio_dial(NameList.AIO_TGTUSER, new string('a', 300));
                    Thread.Sleep(300);
                    r = RunClientCase(python, $"client_cases.py t2 {echoPort}");
                    Check("T7 oversized credential rejected (reset, not truncated)", r.ok, r.detail);
                    aio_dial(NameList.AIO_TGTHOST, "127.0.0.1");
                    aio_dial(NameList.AIO_TGTPORT, socksPort.ToString());
                    aio_dial(NameList.AIO_TGTUSER, "");
                }
            }
            finally
            {
                aio_free();
            }

            Console.WriteLine($"== {Results.Count} checks, {_failed} failed ==");
            return _failed == 0 ? 0 : 1;
        }
    }
}
