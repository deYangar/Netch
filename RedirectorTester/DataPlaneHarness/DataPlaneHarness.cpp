// M3-1 Step B · IOCP 数据面用户态验收（无需 NF 驱动、无需管理员）
//
// 目的：在无法加载 netfilter2 驱动的环境里，仍然对 TCPHandler 的转发泵做端到端验证。
// 做法：连接注册改用 TCPHandler::CreateHandler —— 与 NF 回调 tcpConnectRequest 写的是
//       同一张表（endpoint_id 主表 + 源端口索引），随后客户端直接连监听端口，走完整
//       accept -> 有界握手池 -> SOCKS5 CONNECT -> IOCP 双向泵 链路。
//
// 用例：
//   B1 128 并发 × 4 轮随机长度（1B~256KiB）双向字节严格一致
//   B2 半关闭：应用 shutdown(SD_SEND) 后仍收完 512KiB 回包
//   B3 同源端口快速复用 ×1000（A2 索引回归，含迟到 tcpClosed 的误删场景）
//   B4 吞吐：单连接 64MiB 回环，报告 MB/s（Step B 门槛：不低于 A 版 90%）
#include "Based.h"
#include "TCPHandler.h"

#include <random>
#include <numeric>
#include <psapi.h>

extern USHORT tcpListen;
extern wstring tgtHost;
extern wstring tgtPort;
extern bool verboseLog;
extern bool useIocpPump;

static int g_failures = 0;
static double g_idleBaselineMb = 0;

static void Check(const char* name, bool ok, const string& detail)
{
	printf("%s %s  %s\n", ok ? "PASS" : "FAIL", name, detail.c_str());
	if (!ok)
		g_failures++;
}

static bool SendAll(SOCKET s, const char* data, int length)
{
	int sent = 0;
	while (sent < length)
	{
		auto n = send(s, data + sent, length - sent, 0);
		if (n <= 0)
			return false;
		sent += n;
	}
	return true;
}

static bool RecvAll(SOCKET s, char* data, int length)
{
	int received = 0;
	while (received < length)
	{
		auto n = recv(s, data + received, length - received, 0);
		if (n <= 0)
			return false;
		received += n;
	}
	return true;
}

// ---------------------------------------------------------------- echo server
static void EchoSession(SOCKET client)
{
	auto buffer = make_unique<char[]>(256 * 1024);

	while (true)
	{
		auto n = recv(client, buffer.get(), 256 * 1024, 0);
		if (n <= 0)
			break;

		if (!SendAll(client, buffer.get(), n))
			break;
	}

	// echo semantics: after the peer's FIN, finish writing then close
	shutdown(client, SD_SEND);
	closesocket(client);
}

static void StartEchoServer(USHORT port)
{
	thread([port]
	{
		auto listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		SOCKADDR_IN addr{};
		addr.sin_family = AF_INET;
		addr.sin_port = htons(port);
		addr.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);

		if (bind(listener, (PSOCKADDR)&addr, sizeof(addr)) == SOCKET_ERROR ||
			listen(listener, 512) == SOCKET_ERROR)
		{
			printf("[harness] echo server bind/listen failed: %d\n", WSAGetLastError());
			return;
		}

		while (true)
		{
			auto client = accept(listener, NULL, NULL);
			if (client == INVALID_SOCKET)
				return;

			thread(EchoSession, client).detach();
		}
	}).detach();
}

// ---------------------------------------------------------------- SOCKS5 stub
static bool SocksRecvAll(SOCKET s, char* data, int length)
{
	return RecvAll(s, data, length);
}

static void SocksRelay(SOCKET a, SOCKET b)
{
	// one buffer per direction: the two pumps run concurrently
	auto up = make_unique<char[]>(64 * 1024);
	auto down = make_unique<char[]>(64 * 1024);

	thread([a, b, buf = up.get()]
	{
		while (true)
		{
			auto n = recv(a, buf, 64 * 1024, 0);
			if (n <= 0)
				break;
			if (!SendAll(b, buf, n))
				break;
		}
		shutdown(b, SD_SEND);
	}).detach();

	while (true)
	{
		auto n = recv(b, down.get(), 64 * 1024, 0);
		if (n <= 0)
			break;
		if (!SendAll(a, down.get(), n))
			break;
	}

	shutdown(a, SD_SEND);
}

static void SocksSession(SOCKET client)
{
	char header[2];
	if (!SocksRecvAll(client, header, 2))
		goto done;

	{
		auto methods = make_unique<char[]>(header[1]);
		if (!SocksRecvAll(client, methods.get(), header[1]))
			goto done;
	}

	{
		char choice[2] = { 5, 0 };
		if (send(client, choice, 2, 0) != 2)
			goto done;
	}

	{
		char request[4];
		if (!SocksRecvAll(client, request, 4))
			goto done;

		if (request[1] != 1)
		{
			char reply[10] = { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 };
			send(client, reply, 10, 0);
			goto done;
		}

		SOCKADDR_IN target{};
		target.sin_family = AF_INET;

		if (request[3] == 1)
		{
			if (!SocksRecvAll(client, (char*)&target.sin_addr, 4))
				goto done;
		}
		else if (request[3] == 4)
		{
			char v6[16];
			if (!SocksRecvAll(client, v6, 16))
				goto done;
		}
		else
		{
			goto done;
		}

		if (!SocksRecvAll(client, (char*)&target.sin_port, 2))
			goto done;

		auto upstream = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		if (upstream == INVALID_SOCKET || connect(upstream, (PSOCKADDR)&target, sizeof(target)) == SOCKET_ERROR)
		{
			char reply[10] = { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 };
			send(client, reply, 10, 0);
			if (upstream != INVALID_SOCKET)
				closesocket(upstream);
			goto done;
		}

		char reply[10] = { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 };
		if (send(client, reply, 10, 0) != 10)
		{
			closesocket(upstream);
			goto done;
		}

		SocksRelay(client, upstream);
		closesocket(upstream);
	}

done:
	closesocket(client);
}

static void StartSocksStub(USHORT port)
{
	thread([port]
	{
		auto listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		SOCKADDR_IN addr{};
		addr.sin_family = AF_INET;
		addr.sin_port = htons(port);
		addr.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);

		if (bind(listener, (PSOCKADDR)&addr, sizeof(addr)) == SOCKET_ERROR ||
			listen(listener, 512) == SOCKET_ERROR)
		{
			printf("[harness] socks stub bind/listen failed: %d\n", WSAGetLastError());
			return;
		}

		while (true)
		{
			auto client = accept(listener, NULL, NULL);
			if (client == INVALID_SOCKET)
				return;

			thread(SocksSession, client).detach();
		}
	}).detach();
}

// ---------------------------------------------------------------- helpers
static USHORT FreePort()
{
	auto s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	SOCKADDR_IN addr{};
	addr.sin_family = AF_INET;
	addr.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);
	bind(s, (PSOCKADDR)&addr, sizeof(addr));

	int length = sizeof(addr);
	getsockname(s, (PSOCKADDR)&addr, &length);
	closesocket(s);

	return ntohs(addr.sin_port);
}

static SOCKADDR_IN6 MakeAddr(USHORT port)
{
	SOCKADDR_IN6 addr{};
	addr.sin6_family = AF_INET;
	auto v4 = (PSOCKADDR_IN)&addr;
	v4->sin_family = AF_INET;
	v4->sin_port = htons(port);
	v4->sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);
	return addr;
}

/// register + connect: the app side does exactly this in production via the NF callback
static SOCKET ConnectThroughRedirector(USHORT echoPort, USHORT localPort, ENDPOINT_ID id)
{
	auto client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);

	SOCKADDR_IN local{};
	local.sin_family = AF_INET;
	local.sin_port = htons(localPort);
	local.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);

	// bind always: with port 0 the kernel assigns the ephemeral port here, and the
	// registration below must use exactly that port (an unbound socket reports port 0)
	if (bind(client, (PSOCKADDR)&local, sizeof(local)) == SOCKET_ERROR)
	{
		closesocket(client);
		return INVALID_SOCKET;
	}

	int length = sizeof(local);
	getsockname(client, (PSOCKADDR)&local, &length);

	SOCKADDR_IN6 clientAddr = MakeAddr(ntohs(local.sin_port));
	SOCKADDR_IN6 target = MakeAddr(echoPort);
	TCPHandler::CreateHandler(id, clientAddr, target);

	SOCKADDR_IN listen{};
	listen.sin_family = AF_INET;
	listen.sin_port = tcpListen;
	listen.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);

	if (connect(client, (PSOCKADDR)&listen, sizeof(listen)) == SOCKET_ERROR)
	{
		closesocket(client);
		return INVALID_SOCKET;
	}

	return client;
}


static void CaseDebug(USHORT echoPort)
{
	auto client = ConnectThroughRedirector(echoPort, 0, 900001);
	if (client == INVALID_SOCKET)
	{
		printf("[debug] connect failed %d\n", WSAGetLastError());
		return;
	}

	DWORD timeout = 10000;
	setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));

	const int size = 200000;
	auto payload = make_unique<char[]>(size);
	for (int i = 0; i < size; i++)
		payload[i] = (char)(i & 0xff);

	auto sent = SendAll(client, payload.get(), size);
	printf("[debug] sent=%d\n", sent);

	auto echo = make_unique<char[]>(size);
	int received = 0;
	while (received < size)
	{
		auto n = recv(client, echo.get() + received, size - received, 0);
		if (n <= 0)
		{
			printf("[debug] recv stopped at %d err=%d\n", received, WSAGetLastError());
			break;
		}
		received += n;
	}

	int mismatch = -1;
	for (int i = 0; i < received; i++)
		if (payload[i] != echo[i]) { mismatch = i; break; }

	printf("[debug] received=%d mismatch=%d first=%02x/%02x\n", received, mismatch,
		received > 0 ? (unsigned char)payload[0] : 0, received > 0 ? (unsigned char)echo[0] : 0);
	closesocket(client);
}


/// B5: aggregate throughput at 128 concurrent connections (the Step B gate compares
/// this number against the Step A thread-pair baseline)
static void CaseAggregate(USHORT echoPort, int connections, int mibPerConnection)
{
	const int chunk = 64 * 1024;
	const int total = mibPerConnection * 1024 * 1024;
	const int chunks = total / chunk;

	vector<thread> workers;
	atomic<int> ready{ 0 };
	atomic<bool> go{ false };
	atomic<long long> moved{ 0 };
	atomic<int> failures{ 0 };

	for (int i = 0; i < connections; i++)
	{
		workers.emplace_back([&, i]
		{
			auto client = ConnectThroughRedirector(echoPort, 0, 500000 + i);
			if (client == INVALID_SOCKET)
			{
				failures++;
				ready++;
				return;
			}

			DWORD timeout = 120000;
			setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
			setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));

			auto payload = make_unique<char[]>(chunk);
			memset(payload.get(), (char)i, chunk);
			auto echo = make_unique<char[]>(chunk);

			ready++;
			while (!go)
				Sleep(1);

			for (int c = 0; c < chunks; c++)
			{
				if (!SendAll(client, payload.get(), chunk) || !RecvAll(client, echo.get(), chunk) ||
					memcmp(payload.get(), echo.get(), chunk) != 0)
				{
					failures++;
					break;
				}
				moved += chunk;
			}

			closesocket(client);
		});
	}

	while (ready < connections)
		Sleep(5);

	auto start = chrono::steady_clock::now();
	go = true;

	for (auto& worker : workers)
		worker.join();

	auto elapsed = chrono::duration_cast<chrono::milliseconds>(chrono::steady_clock::now() - start).count();
	auto mbps = elapsed > 0 ? (moved / 1024.0 / 1024.0) / (elapsed / 1000.0) : 0;

	Check("B5 aggregate throughput @128 concurrent", failures == 0,
		to_string((long long)mbps) + " MB/s, moved=" + to_string((long long)moved) + "B in " + to_string(elapsed) + "ms, failures=" + to_string(failures.load()));
}

static double PrivateMegabytes();

static double PrivateMegabytes()
{
	PROCESS_MEMORY_COUNTERS_EX counters{};
	counters.cb = sizeof(counters);
	GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&counters, sizeof(counters));
	return counters.PrivateUsage / 1024.0 / 1024.0;
}

/// B6: private bytes with 500 idle connections (memory gate: < 70% of Step A).
/// The delta against the idle baseline is the number that reflects the redirector;
/// the absolute figure is dominated by the harness's three threads per connection.
static void CaseMemory(USHORT echoPort, int connections)
{
	vector<SOCKET> sockets;
	for (int i = 0; i < connections; i++)
	{
		auto client = ConnectThroughRedirector(echoPort, 0, 600000 + i);
		if (client != INVALID_SOCKET)
			sockets.push_back(client);
	}

	Sleep(500);

	auto privateMb = PrivateMegabytes();
	auto baselineMb = g_idleBaselineMb;
	auto deltaKb = sockets.empty() ? 0 : (privateMb - baselineMb) * 1024 / sockets.size();

	for (auto client : sockets)
		closesocket(client);

	printf("INFO B6 %zu idle connections: private=%.1f MB (idle baseline %.1f MB, delta %.1f KiB/conn)\n",
		sockets.size(), privateMb, baselineMb, deltaKb);
}

// ---------------------------------------------------------------- cases
static void CaseConcurrency(USHORT echoPort, int concurrency)
{
	auto rng = mt19937(42);
	vector<SOCKET> sockets;
	int failures = 0;
	string firstError;

	for (int i = 0; i < concurrency; i++)
	{
		auto client = ConnectThroughRedirector(echoPort, 0, 100000 + i);
		if (client == INVALID_SOCKET)
		{
			failures++;
			if (firstError.empty())
				firstError = "connect#" + to_string(i) + " err=" + to_string(WSAGetLastError());
			continue;
		}

		sockets.push_back(client);
	}

	for (auto client : sockets)
	{
		DWORD timeout = 60000;
		setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
		setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));
	}

	for (int round = 0; round < 4; round++)
	{
		for (auto client : sockets)
		{
			int size = 1 + (int)(rng() % (256 * 1024));
			auto payload = make_unique<char[]>(size);
			for (int i = 0; i < size; i++)
				payload[i] = (char)(rng() & 0xff);

			auto echo = make_unique<char[]>(size);

			if (!SendAll(client, payload.get(), size) || !RecvAll(client, echo.get(), size) ||
				memcmp(payload.get(), echo.get(), size) != 0)
			{
				failures++;
				if (firstError.empty())
					firstError = "round" + to_string(round) + " mismatch size=" + to_string(size);
			}
		}
	}

	for (auto client : sockets)
		closesocket(client);

	Check("B1 128-concurrent strict byte echo", failures == 0,
		"failures=" + to_string(failures) + "/" + to_string(concurrency) + " " + firstError);
}

static void CaseHalfClose(USHORT echoPort)
{
	const int size = 512 * 1024;
	auto payload = make_unique<char[]>(size);
	for (int i = 0; i < size; i++)
		payload[i] = (char)(i * 31);

	auto client = ConnectThroughRedirector(echoPort, 0, 200001);
	if (client == INVALID_SOCKET)
	{
		Check("B2 half-close", false, "connect failed");
		return;
	}

	DWORD timeout = 30000;
	setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));

	auto ok = SendAll(client, payload.get(), size);
	shutdown(client, SD_SEND);

	auto echo = make_unique<char[]>(size);
	ok = ok && RecvAll(client, echo.get(), size) && memcmp(payload.get(), echo.get(), size) == 0;

	closesocket(client);
	Check("B2 half-close: 512KiB echoed after shutdown(SD_SEND)", ok, ok ? "512KiB" : "short read/mismatch");
}

static void CasePortReuse(USHORT echoPort, int count)
{
	int failures = 0;
	USHORT localPort = 0;

	for (int i = 0; i < count; i++)
	{
		auto client = ConnectThroughRedirector(echoPort, localPort, 300000 + i);
		if (client == INVALID_SOCKET)
		{
			failures++;
			continue;
		}

		if (localPort == 0)
		{
			SOCKADDR_IN local{};
			int length = sizeof(local);
			getsockname(client, (PSOCKADDR)&local, &length);
			localPort = ntohs(local.sin_port);
		}

		char probe[32];
		snprintf(probe, sizeof(probe), "reuse-%06d", i);

		char echo[32];
		auto ok = SendAll(client, probe, (int)strlen(probe) + 1) && RecvAll(client, echo, (int)strlen(probe) + 1) &&
			memcmp(probe, echo, strlen(probe) + 1) == 0;

		if (!ok)
			failures++;

		// hard close (RST) so the source port is immediately reusable
		LINGER lg{ 1, 0 };
		setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&lg, sizeof(lg));
		closesocket(client);
	}

	Check("B3 source-port fast reuse x1000", failures == 0, "failures=" + to_string(failures) + "/" + to_string(count));
}

static void CaseThroughput(USHORT echoPort)
{
	const int total = 64 * 1024 * 1024;
	const int chunk = 64 * 1024;

	auto client = ConnectThroughRedirector(echoPort, 0, 400001);
	if (client == INVALID_SOCKET)
	{
		Check("B4 throughput", false, "connect failed");
		return;
	}

	DWORD timeout = 120000;
	setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
	setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));

	auto payload = make_unique<char[]>(chunk);
	memset(payload.get(), 0x5a, chunk);

	atomic<bool> sending{ true };
	thread sender([&]
	{
		int sent = 0;
		while (sent < total)
		{
			if (!SendAll(client, payload.get(), chunk))
				break;
			sent += chunk;
		}
		shutdown(client, SD_SEND);
		sending = false;
	});

	auto echo = make_unique<char[]>(chunk);
	auto start = chrono::steady_clock::now();
	int received = 0;

	while (received < total)
	{
		auto n = recv(client, echo.get(), chunk, 0);
		if (n <= 0)
			break;
		received += n;
	}

	auto elapsed = chrono::duration_cast<chrono::milliseconds>(chrono::steady_clock::now() - start).count();
	sender.join();
	closesocket(client);

	auto mbps = elapsed > 0 ? (total / 1024.0 / 1024.0) / (elapsed / 1000.0) : 0;
	auto ok = received == total;
	Check("B4 throughput 64MiB loopback", ok,
		"received=" + to_string(received) + " in " + to_string(elapsed) + "ms (" + to_string((int)mbps) + " MB/s)");
}

int main(int argc, char** argv)
{
	setvbuf(stdout, NULL, _IONBF, 0);
	printf("[harness] start\n");
	fflush(stdout);

	WSADATA data;
	WSAStartup(MAKEWORD(2, 2), &data);

	auto echoPort = FreePort();
	auto socksPort = FreePort();

	printf("[harness] echo=%u socks=%u\n", echoPort, socksPort);
	fflush(stdout);
	StartEchoServer(echoPort);
	StartSocksStub(socksPort);

	// readiness probe: the stub listener must be up before the first handshake
	for (int i = 0; i < 50; i++)
	{
		auto probe = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		SOCKADDR_IN addr{};
		addr.sin_family = AF_INET;
		addr.sin_port = htons(socksPort);
		addr.sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);

		if (connect(probe, (PSOCKADDR)&addr, sizeof(addr)) == 0)
		{
			closesocket(probe);
			printf("[harness] socks stub ready after %dms\n", i * 20);
			break;
		}

		closesocket(probe);
		Sleep(20);
	}

	verboseLog = true;

	// --legacy exercises the AIO_PUMP=legacy rollback path with the same cases
	useIocpPump = !(argc > 1 && string(argv[1]) == "--legacy");
	printf("[harness] pump=%s\n", useIocpPump ? "iocp" : "legacy");

	tgtHost = L"127.0.0.1";
	tgtPort = to_wstring(socksPort);

	printf("[harness] init...\n");
	fflush(stdout);
	if (!TCPHandler::INIT())
	{
		printf("TCPHandler::INIT failed\n");
		return 2;
	}

	printf("[harness] listen=%u\n", tcpListen);
	fflush(stdout);
	printf("== Redirector Step B IOCP pump harness (listen=%u echo=%u socks=%u) ==\n", tcpListen, echoPort, socksPort);

	g_idleBaselineMb = PrivateMegabytes();
	CaseDebug(echoPort);
	verboseLog = false; // perf cases must not pay for a printf per completion
	fflush(stdout);
	printf("[harness] B1\n");
	fflush(stdout);
	CaseConcurrency(echoPort, 128);
	fflush(stdout);
	CaseHalfClose(echoPort);
	CasePortReuse(echoPort, 1000);
	CaseThroughput(echoPort);
	CaseAggregate(echoPort, 128, 4);
	CaseMemory(echoPort, 500);

	TCPHandler::FREE();
	printf("== %d failed ==\n", g_failures);
	return g_failures == 0 ? 0 : 1;
}
