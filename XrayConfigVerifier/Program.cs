// Xray 辅内核配置生成验证工具：对 xhttp 传输与内核路由生成样例配置，
// 供 xray run -test 校验字段合法性。用法：
//   dotnet run --project XrayConfigVerifier -c Release -- <输出目录> <xray.exe 路径> [sing-box.exe 路径]
using System.Diagnostics;
using System.Text.Json;
using Netch;
using Netch.Controllers;
using Netch.Models;
using Netch.Servers;
using Netch.Utils;

var outDir = args.Length > 0 ? args[0] : "configs";
Directory.CreateDirectory(outDir);

Global.Settings.V2RayConfig.AllowInsecure = false;

// 相对入参必须在切换 CWD 前锚定为绝对路径：切换后相对路径改以程序目录解析，
// File.Copy 源不存在直接炸、产物写歪目录（CI 首跑 2026-11-12 实证）
outDir = Path.GetFullPath(outDir);
var xrayPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
var singboxPath = args.Length > 2 ? Path.GetFullPath(args[2]) : null;

// Guard 以 CWD 解析 bin\xray.exe，dotnet run 下 CWD=仓库根；对齐到程序目录（Global.NetchDir 同源）
Environment.CurrentDirectory = AppContext.BaseDirectory;

// 路由断言会真实构造 XrayController/SingboxController（Guard 检查 bin\ 内核存在），把传入的内核复制到本进程 bin 下。
// sing-box 必须同传：常规协议「路由不变」断言构造 SingboxController 同样过 Guard，
// 本地程序 bin 的历史残留曾掩盖该依赖（CI 干净环境二跑实证）
if (xrayPath != null || singboxPath != null)
{
    var binDir = Path.Combine(AppContext.BaseDirectory, "bin");
    Directory.CreateDirectory(binDir);
    if (xrayPath != null)
        File.Copy(xrayPath, Path.Combine(binDir, "xray.exe"), true);
    if (singboxPath != null)
        File.Copy(singboxPath, Path.Combine(binDir, "sing-box.exe"), true);
}

var failed = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
    if (!ok)
        failed.Add(name);
}

// ── 分享链接 xhttp 解析与生成往返 ──
var xhttpLink = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@example.com:443" +
                "?security=tls&sni=sni.example.com&type=xhttp&path=%2Fxhttppath&host=cdn.example.com&encryption=none#xhttp-node";
var xhttpParsed = (VLESSServer)V2rayUtils.ParseVUri(xhttpLink).Single();
Check("vless-xhttp link parse",
    xhttpParsed.TransferProtocol == "xhttp" &&
    xhttpParsed.Path == "/xhttppath" &&
    xhttpParsed.Host == "cdn.example.com" &&
    xhttpParsed.TLSSecureType == "tls" &&
    xhttpParsed.ServerName == "sni.example.com" &&
    xhttpParsed.Hostname == "example.com" && xhttpParsed.Port == 443,
    $"type={xhttpParsed.TransferProtocol} path={xhttpParsed.Path} host={xhttpParsed.Host}");

var xhttpRoundTrip = (VLESSServer)V2rayUtils.ParseVUri(V2rayUtils.GetVShareLink(xhttpParsed, "vless")).Single();
Check("vless-xhttp link roundtrip",
    xhttpRoundTrip.TransferProtocol == "xhttp" &&
    xhttpRoundTrip.Path == xhttpParsed.Path &&
    xhttpRoundTrip.Host == xhttpParsed.Host,
    V2rayUtils.GetVShareLink(xhttpParsed, "vless"));

// ── 内核路由（全自动，2026-10-31 双核收敛）：xhttp/kcp/tcp+http 伪装/明文 Trojan → Xray 兜底，
//    SSR 与旧 XTLS flow 与裸 QUIC 传输（随包 Xray 26.10.21 已删）明确拒绝（拍板 A），其余常规协议恒路由 sing-box ──
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "bin", "xray.exe")))
{
    Check("route xhttp to Xray kernel", MainController.CreateServerController(xhttpParsed) is XrayController);
    Check("route vmess xhttp to Xray kernel",
        MainController.CreateServerController(new VMessServer { TransferProtocol = "xhttp" }) is XrayController);
    Check("route non-xhttp unchanged",
        MainController.CreateServerController(new VMessServer { TransferProtocol = "ws" }) is SingboxController);

    Check("route vmess kcp to Xray kernel",
        MainController.CreateServerController(new VMessServer { TransferProtocol = "kcp" }) is XrayController);
    Check("route vmess tcp+http(fake) to Xray kernel",
        MainController.CreateServerController(new VMessServer { TransferProtocol = "tcp", FakeType = "http" }) is XrayController);
    Check("route trojan none to Xray kernel",
        MainController.CreateServerController(new TrojanServer { TLSSecureType = "none" }) is XrayController);

    bool Rejected(Server server)
    {
        try
        {
            MainController.CreateServerController(server);
            return false;
        }
        catch (MessageException)
        {
            return true;
        }
    }

    Check("route ssr rejected", Rejected(new ShadowsocksRServer()));
    Check("route vless xtls rejected", Rejected(new VLESSServer { TLSSecureType = "xtls" }));
    Check("route trojan xtls rejected", Rejected(new TrojanServer { TLSSecureType = "xtls" }));
    Check("route vmess quic rejected", Rejected(new VMessServer { TransferProtocol = "quic" }));
}

// ── 配置样例生成 ──
// 样例关键字段断言表：outbounds[0].streamSettings 的传输/安全层落位检查
static JsonElement StreamOf(JsonDocument doc) =>
    doc.RootElement.GetProperty("outbounds")[0].GetProperty("streamSettings");

var transportFieldChecks = new Dictionary<string, Func<JsonDocument, bool>>
{
    ["vmess-kcp"] = doc =>
    {
        var stream = StreamOf(doc);
        return stream.TryGetProperty("network", out var network) && network.GetString() == "kcp" &&
               stream.TryGetProperty("kcpSettings", out _);
    },
    ["vmess-tcp-httpfake"] = doc =>
    {
        var stream = StreamOf(doc);
        return stream.TryGetProperty("network", out var network) && network.GetString() == "tcp" &&
               stream.TryGetProperty("tcpSettings", out var tcp) &&
               tcp.TryGetProperty("header", out var header) &&
               header.TryGetProperty("type", out var type) && type.GetString() == "http";
    },
    ["trojan-plain"] = doc =>
    {
        var stream = StreamOf(doc);
        return stream.TryGetProperty("security", out var security) && security.GetString() == "none";
    }
};
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

var samples = new Dictionary<string, Server>
{
    ["vless-xhttp-tls"] = new VLESSServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "xhttp",
        TLSSecureType = "tls",
        ServerName = "sni.example.com",
        Path = "/xhttppath",
        Host = "cdn.example.com"
    },
    ["vless-xhttp-plain"] = new VLESSServer
    {
        Hostname = "localhost",
        Port = 80,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "xhttp",
        TLSSecureType = "none",
        Path = "/xhttppath",
        Host = "cdn.example.com"
    },
    ["vmess-xhttp-tls"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        EncryptMethod = "auto",
        TransferProtocol = "xhttp",
        TLSSecureType = "tls",
        ServerName = "sni.example.com",
        Path = "/xhttppath",
        Host = "cdn.example.com"
    },
    ["vless-xhttp-reality"] = new VLESSServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        TransferProtocol = "xhttp",
        TLSSecureType = "reality",
        ServerName = "sni.example.com",
        Fingerprint = "chrome",
        RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
        RealityShortId = "0123456789abcdef",
        Path = "/xhttppath"
    },
    // 非 xhttp 样例：确认 V2rayConfigUtils 常规路径未被改坏（Xray 同样能解析）
    ["vmess-ws-tls"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        EncryptMethod = "auto",
        TransferProtocol = "ws",
        TLSSecureType = "tls",
        ServerName = "sni.example.com",
        Path = "/v2ray",
        Host = "ws.example.com"
    },
    // Xray 兜底传输样例（2026-10-31 双核收敛后由 Xray 承载的协议面）：
    // kcp / tcp+http 伪装 / 明文 Trojan——防生成器回归。
    // 裸 QUIC 传输样例撤下：随包 Xray 26.10.21 已删除该传输（xray run -test 实测拒绝），路由层已明示不再支持
    ["vmess-kcp"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 443,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        EncryptMethod = "auto",
        TransferProtocol = "kcp"
    },
    ["vmess-tcp-httpfake"] = new VMessServer
    {
        Hostname = "localhost",
        Port = 80,
        UserID = "b831381d-6324-4d53-ad4f-8cda48b30811",
        EncryptMethod = "auto",
        TransferProtocol = "tcp",
        FakeType = "http",
        TLSSecureType = "none",
        Path = "/",
        Host = "fake.example.com"
    },
    ["trojan-plain"] = new TrojanServer
    {
        Hostname = "localhost",
        Port = 443,
        Password = "trojan-password-example",
        TLSSecureType = "none"
    }
};

foreach (var (name, server) in samples)
{
    try
    {
        var config = await V2rayConfigUtils.GenerateClientConfigAsync(server);
        var json = JsonSerializer.Serialize(config, jsonOptions);
        var path = Path.Combine(outDir, $"{name}.json");
        await File.WriteAllTextAsync(path, json);

        // 关键字段断言（防生成器回归）：样例 JSON 的传输/安全层字段必须正确落位
        if (transportFieldChecks.TryGetValue(name, out var check))
            Check($"fields {name}", check(JsonDocument.Parse(json)));

        Console.WriteLine($"OK   {name} -> {path}");
    }
    catch (Exception e)
    {
        failed.Add(name);
        Console.WriteLine($"FAIL {name}: {e.Message}");
    }
}

// ── xray run -test 校验 ──
if (args.Length > 1)
{
    foreach (var json in Directory.GetFiles(outDir, "*.json"))
    {
        var name = Path.GetFileNameWithoutExtension(json);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = xrayPath!,
            Arguments = $"run -test -c \"{json}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check($"xray check {name}", process.ExitCode == 0, process.ExitCode == 0 ? "" : error.Trim());
    }
}

Console.WriteLine(failed.Any() ? $"{failed.Count} FAILURES: {string.Join(", ", failed)}" : "ALL PASS");
return failed.Any() ? 1 : 0;
