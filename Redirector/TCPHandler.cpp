#include "TCPHandler.h"
#include "ConnTable.h"

SOCKET tcpSocket = INVALID_SOCKET;
USHORT tcpListen = 0;

// M3-1 A2: endpoint_id-keyed connection table with a source-port lookup index.
// The accept path can only recover the peer's source port, so the port index bridges
// accept -> endpoint_id -> original target. Source ports are unique within a
// connection's lifetime and cleaned up on tcpClosed, making reuse safe.
// (port-only key: the NF callback's localAddress family field is not reliable,
//  and v4/v6 source-port collision within one connection lifetime is not real)
struct TcpEntry
{
	SOCKADDR_IN6 target;
	USHORT port;
};

mutex tcpLock;
map<ENDPOINT_ID, TcpEntry> tcpContext;
map<USHORT, ENDPOINT_ID> tcpPortIndex;

// M3-1 A3: consecutive accept failures (fatal threshold guards a spinning listener)
static atomic<int> acceptFailures{ 0 };

extern bool useIocpPump;

// Step B pump lifecycle (defined below; INIT/FREE are declared first)
static bool IocpStart();
static void IocpStop();

static USHORT PortOf(const SOCKADDR_IN6& addr)
{
	return ntohs((addr.sin6_family == AF_INET) ? ((PSOCKADDR_IN)&addr)->sin_port : addr.sin6_port);
}

bool TCPHandler::INIT()
{
	auto lg = lock_guard<mutex>(tcpLock);

	if (tcpSocket != INVALID_SOCKET)
	{
		closesocket(tcpSocket);

		tcpSocket = INVALID_SOCKET;
	}

	auto client = socket(AF_INET6, SOCK_STREAM, IPPROTO_TCP);
	if (client == INVALID_SOCKET)
	{
		printf("[Redirector][TCPHandler::INIT] Create socket failed: %d\n", WSAGetLastError());
		return false;
	}

	{
		int v6only = 0;
		if (setsockopt(client, IPPROTO_IPV6, IPV6_V6ONLY, (char*)&v6only, sizeof(v6only)) == SOCKET_ERROR)
		{
			printf("[Redirector][TCPHandler::INIT] Set socket option failed: %d\n", WSAGetLastError());

			closesocket(client);
			return false;
		}
	}

	{
		SOCKADDR_IN6 addr;
		IN6ADDR_SETANY(&addr);

		if (bind(client, (PSOCKADDR)&addr, sizeof(SOCKADDR_IN6)) == SOCKET_ERROR)
		{
			printf("[Redirector][TCPHandler::INIT] Bind socket failed: %d\n", WSAGetLastError());

			closesocket(client);
			return false;
		}
	}

	if (listen(client, 1024) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Listen socket failed: %d\n", WSAGetLastError());

		closesocket(client);
		return false;
	}

	{
		SOCKADDR_IN6 addr;
		int addrLength = sizeof(SOCKADDR_IN6);
		if (getsockname(client, (PSOCKADDR)&addr, &addrLength) == SOCKET_ERROR)
		{
			printf("[Redirector][TCPHandler::INIT] Get listen address failed: %d\n", WSAGetLastError());

			closesocket(client);
			return false;
		}

		tcpListen = (addr.sin6_family == AF_INET6) ? addr.sin6_port : ((PSOCKADDR_IN)&addr)->sin_port;
	}

	tcpSocket = client;

	// M3-1 Step B: the forwarding pump runs on a shared IOCP; the listener itself keeps
	// a blocking accept thread (accept rate is low, AcceptEx buys nothing here).
	// AIO_PUMP=legacy falls back to the Step A thread-pair pump without a rebuild.
	if (useIocpPump && !IocpStart())
	{
		closesocket(client);
		tcpSocket = INVALID_SOCKET;
		return false;
	}

	thread(TCPHandler::Accept).detach();
	return true;
}

void TCPHandler::FREE()
{
	IocpStop();

	auto lg = lock_guard<mutex>(tcpLock);

	if (tcpSocket != INVALID_SOCKET)
	{
		closesocket(tcpSocket);

		tcpSocket = INVALID_SOCKET;
	}
	tcpListen = 0;

	tcpContext.clear();
	tcpPortIndex.clear();
}

void TCPHandler::CreateHandler(ENDPOINT_ID id, SOCKADDR_IN6 client, SOCKADDR_IN6 remote)
{
	auto lg = lock_guard<mutex>(tcpLock);

	auto key = PortOf(client);

	// a stale index entry for the same port points to a dead endpoint: drop it
	if (auto it = tcpPortIndex.find(key); it != tcpPortIndex.end())
	{
		tcpContext.erase(it->second);
		tcpPortIndex.erase(it);
	}

	tcpContext[id] = { remote, key };
	tcpPortIndex[key] = id;
}

void TCPHandler::DeleteHandler(ENDPOINT_ID id)
{
	auto lg = lock_guard<mutex>(tcpLock);

	if (auto it = tcpContext.find(id); it != tcpContext.end())
	{
		// Only drop the port index entry when it still points at this endpoint.
		// A late tcpClosed for a previous connection that used the same source port
		// must not erase the mapping of the connection that reused it (T1/T5 root cause).
		if (auto indexIt = tcpPortIndex.find(it->second.port); indexIt != tcpPortIndex.end() && indexIt->second == id)
			tcpPortIndex.erase(indexIt);

		tcpContext.erase(it);
	}
}

/// Abortive close: sends RST so the application sees WSAECONNRESET immediately
/// instead of a silent FIN that reads as "server never responded" (#582 / D4)
static void CloseWithReset(SOCKET s)
{
	LINGER lg{ 1, 0 };
	setsockopt(s, SOL_SOCKET, SO_LINGER, (char*)&lg, sizeof(lg));
	closesocket(s);
}

// =====================================================================================
// M3-1 Step B: shared IOCP forwarding pump
//
// One completion port for every client/upstream socket; N workers (one per CPU, 2..8)
// dispatch completions. Each direction of a connection owns two 64KiB slots: while one
// slot's payload is being sent, the other slot immediately re-arms a receive, so the two
// directions pipeline instead of running stop-and-wait (that cost ~30% against the
// thread-pair model). Partial sends resume from the recorded offset.
//
// Half-close is propagated: recv==0 shuts the destination's send side down and marks the
// direction read-closed; the connection is torn down only when both directions are done.
// Reference counting keeps the object alive while completions are in flight.
// =====================================================================================

static const int IO_SLOT_SIZE = 64 * 1024;
// One slot per direction. Two slots would let a partially-sent chunk be overtaken by the
// sibling slot's chunk (TCP orders by issue order, not completion), corrupting the stream;
// an ordered send queue measured no faster than this in the loopback harness, so the
// simpler model stays.
static const int IO_SLOTS = 1;

struct TcpConn;
struct IoDirection;

struct IoSlot
{
	IoDirection* dir = nullptr;
	OVERLAPPED recvOv{};
	OVERLAPPED sendOv{};
	WSABUF wsabuf{};
	unique_ptr<char[]> buffer;
	int sendOffset = 0;
	int sendLength = 0;
	bool recvPending = false;
	bool sendPending = false;
};

struct IoDirection
{
	TcpConn* conn = nullptr;
	const char* name = "?";
	SOCKET src = INVALID_SOCKET;
	SOCKET dst = INVALID_SOCKET;
	IoSlot slots[IO_SLOTS];
	atomic<bool> readClosed{ false };
};

struct TcpConn
{
	SOCKET client = INVALID_SOCKET;
	SocksHelper::TCP* remote = nullptr;
	// App-side connection's driver endpoint id (recovered from tcpPortIndex at accept).
	// Downstream bytes are written back to the app socket by this pump; the driver never
	// filters Redirector's own sockets, so tcpReceive never fires and the Down counter
	// can only be recorded here on send completion (same pattern as the UDP worker's
	// Read-side AddDl).
	ENDPOINT_ID id = 0;
	IoDirection up;   // client -> upstream
	IoDirection down; // upstream -> client
	atomic<int> refs{ 1 };
	atomic<bool> closed{ false };
};

extern bool verboseLog;

static HANDLE g_iocp = NULL;
static vector<thread> g_iocpWorkers;
static vector<thread> g_connectors;
static atomic<bool> g_stopWorkers{ false };

static mutex g_connLock;
static set<TcpConn*> g_conns;

struct ConnectTask
{
	SOCKET client;
	SOCKADDR_IN6 target;
	ENDPOINT_ID id;
};

static mutex g_connectLock;
static deque<ConnectTask> g_connectQueue;
static condition_variable g_connectCv;
static const size_t ConnectQueueMax = 4096;

static void PostRecv(IoSlot* slot);
static void PostSend(IoSlot* slot);

static void ConnRelease(TcpConn* conn)
{
	if (--conn->refs == 0)
	{
		if (conn->client != INVALID_SOCKET)
		{
			closesocket(conn->client);
			conn->client = INVALID_SOCKET;
		}

		delete conn->remote;
		conn->remote = nullptr;

		{
			lock_guard<mutex> lg(g_connLock);
			g_conns.erase(conn);
		}

		delete conn;
	}
}

static void ConnRetain(TcpConn* conn)
{
	conn->refs++;
}

/// Tear both directions down and drop the lifecycle reference. Idempotent: the first
/// caller wins, later callers (pending completions) just fall through.
static void ConnClose(TcpConn* conn)
{
	if (conn->closed.exchange(true))
		return;

	if (verboseLog)
		printf("[pump] close conn=%p\n", (void*)conn);

	if (conn->client != INVALID_SOCKET)
		shutdown(conn->client, SD_BOTH);

	if (conn->remote != nullptr && conn->remote->tcpSocket != INVALID_SOCKET)
		shutdown(conn->remote->tcpSocket, SD_BOTH);

	ConnRelease(conn);
}

static void OnReadClosed(TcpConn* conn)
{
	if (conn->up.readClosed && conn->down.readClosed)
		ConnClose(conn);
}

static void PostRecv(IoSlot* slot)
{
	auto dir = slot->dir;
	auto conn = dir->conn;

	if (conn->closed || dir->readClosed || slot->recvPending || slot->sendPending)
		return;

	memset(&slot->recvOv, 0, sizeof(OVERLAPPED));
	slot->wsabuf.buf = slot->buffer.get();
	slot->wsabuf.len = IO_SLOT_SIZE;
	slot->recvPending = true;

	DWORD flags = 0;
	DWORD bytes = 0;

	ConnRetain(conn);

	if (WSARecv(dir->src, &slot->wsabuf, 1, &bytes, &flags, &slot->recvOv, NULL) == SOCKET_ERROR)
	{
		auto err = WSAGetLastError();
		if (err != WSA_IO_PENDING)
		{
			slot->recvPending = false;
			ConnRelease(conn);

			dir->readClosed = true;
			if (dir->dst != INVALID_SOCKET)
				shutdown(dir->dst, SD_SEND);

			OnReadClosed(conn);
		}
	}
}

static void PostSend(IoSlot* slot)
{
	auto dir = slot->dir;
	auto conn = dir->conn;

	if (conn->closed)
	{
		slot->sendPending = false;
		return;
	}

	memset(&slot->sendOv, 0, sizeof(OVERLAPPED));
	slot->wsabuf.buf = slot->buffer.get() + slot->sendOffset;
	slot->wsabuf.len = (ULONG)(slot->sendLength - slot->sendOffset);
	slot->sendPending = true;

	DWORD bytes = 0;

	ConnRetain(conn);

	if (WSASend(dir->dst, &slot->wsabuf, 1, &bytes, 0, &slot->sendOv, NULL) == SOCKET_ERROR)
	{
		auto err = WSAGetLastError();
		if (err != WSA_IO_PENDING)
		{
			slot->sendPending = false;
			ConnRelease(conn);
			ConnClose(conn);
		}
	}
}

/// re-arm every idle slot of a direction (pipelining: the other slot keeps receiving
/// while this one is being sent)
static void RearmDirection(IoDirection* dir)
{
	if (dir->conn->closed || dir->readClosed)
		return;

	for (int i = 0; i < IO_SLOTS; i++)
		PostRecv(&dir->slots[i]);
}

static void IocpWorker()
{
	while (!g_stopWorkers)
	{
		DWORD bytes = 0;
		ULONG_PTR key = 0;
		OVERLAPPED* ov = nullptr;

		auto ok = GetQueuedCompletionStatus(g_iocp, &bytes, &key, &ov, 500);

		if (g_stopWorkers)
			break;

		if (ov == nullptr)
			continue;

		auto conn = (TcpConn*)key;

		IoSlot* slot = nullptr;
		bool isSend = false;

		for (auto dir : { &conn->up, &conn->down })
		{
			for (int i = 0; i < IO_SLOTS && slot == nullptr; i++)
			{
				if (ov == &dir->slots[i].recvOv)
					slot = &dir->slots[i];
				else if (ov == &dir->slots[i].sendOv)
				{
					slot = &dir->slots[i];
					isSend = true;
				}
			}

			if (slot != nullptr)
				break;
		}

		if (verboseLog && slot != nullptr)
			printf("[pump] complete conn=%p dir=%s send=%d ok=%d bytes=%lu err=%d\n",
				(void*)conn, slot->dir->name, (int)isSend, (int)ok, (unsigned long)bytes, (int)(ok ? 0 : WSAGetLastError()));

		if (slot != nullptr)
		{
			auto dir = slot->dir;

			if (isSend)
			{
				slot->sendPending = false;

				if (!ok)
				{
					ConnClose(conn);
				}
				else
				{
					// down-direction send completed = bytes actually handed to the app;
					// tcpReceive never fires under the pump architecture, so Down is
					// accounted here (up-direction is already covered by tcpSend)
					if (dir == &conn->down)
						ConnTable::AddDl(conn->id, bytes);

					slot->sendOffset += (int)bytes;

					if (slot->sendOffset < slot->sendLength)
						PostSend(slot); // partial write: resume from the offset
					else
						PostRecv(slot); // slot is free again
				}
			}
			else
			{
				slot->recvPending = false;

				if (!ok || bytes == 0)
				{
					// upstream/app FIN or read error: propagate half-close, keep the
					// opposite direction alive until it finishes on its own
					if (!dir->readClosed)
					{
						dir->readClosed = true;

						if (dir->dst != INVALID_SOCKET)
							shutdown(dir->dst, SD_SEND);

						OnReadClosed(conn);
					}
				}
				else
				{
					slot->sendOffset = 0;
					slot->sendLength = (int)bytes;
					PostSend(slot);

					// keep the pipe full: the sibling slot takes the next receive
					for (int i = 0; i < IO_SLOTS; i++)
						if (&dir->slots[i] != slot)
							PostRecv(&dir->slots[i]);
				}
			}
		}

		ConnRelease(conn); // the reference taken when the operation was posted
	}
}

static void ConnectorWorker()
{
	while (!g_stopWorkers)
	{
		ConnectTask task;

		{
			unique_lock<mutex> lg(g_connectLock);
			g_connectCv.wait_for(lg, chrono::milliseconds(500), [] { return !g_connectQueue.empty() || g_stopWorkers.load(); });

			if (g_stopWorkers)
				break;

			if (g_connectQueue.empty())
				continue;

			task = g_connectQueue.front();
			g_connectQueue.pop_front();
		}

		auto remote = new SocksHelper::TCP();
		if (!remote->Connect(&task.target))
		{
			if (verboseLog)
				printf("[pump] handshake failed for client %llu\n", (unsigned long long)task.client);

			// upstream unreachable: RST the application instead of leaving it hanging
			CloseWithReset(task.client);
			delete remote;
			continue;
		}

		if (g_stopWorkers)
		{
			CloseWithReset(task.client);
			delete remote;
			break;
		}

		auto conn = new TcpConn();
		conn->client = task.client;
		conn->remote = remote;
		conn->id = task.id;

		conn->up.conn = conn;
		conn->up.name = "up";
		conn->up.src = conn->client;
		conn->up.dst = conn->remote->tcpSocket;
		conn->down.conn = conn;
		conn->down.name = "down";
		conn->down.src = conn->remote->tcpSocket;
		conn->down.dst = conn->client;

		for (auto dir : { &conn->up, &conn->down })
		{
			for (int i = 0; i < IO_SLOTS; i++)
			{
				dir->slots[i].dir = dir;
				dir->slots[i].buffer.reset(new char[IO_SLOT_SIZE]);
			}
		}

		{
			lock_guard<mutex> lg(g_connLock);
			g_conns.insert(conn);
		}

		if (CreateIoCompletionPort((HANDLE)conn->client, g_iocp, (ULONG_PTR)conn, 0) == NULL ||
			CreateIoCompletionPort((HANDLE)conn->remote->tcpSocket, g_iocp, (ULONG_PTR)conn, 0) == NULL)
		{
			printf("[Redirector][TCPHandler] associate with IOCP failed: %d\n", GetLastError());
			CloseWithReset(conn->client);
			ConnClose(conn);
			continue;
		}

		RearmDirection(&conn->up);
		RearmDirection(&conn->down);
	}
}

static bool IocpStart()
{
	if (g_iocp != NULL)
		return true;

	g_stopWorkers = false;
	g_iocp = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0, 0);
	if (g_iocp == NULL)
	{
		printf("[Redirector][TCPHandler] CreateIoCompletionPort failed: %d\n", GetLastError());
		return false;
	}

	auto cpus = thread::hardware_concurrency();
	if (cpus < 2)
		cpus = 2;
	if (cpus > 8)
		cpus = 8;

	for (unsigned i = 0; i < cpus; i++)
		g_iocpWorkers.emplace_back(IocpWorker);

	auto connectors = cpus / 2;
	if (connectors < 2)
		connectors = 2;

	for (unsigned i = 0; i < connectors; i++)
		g_connectors.emplace_back(ConnectorWorker);

	return true;
}

static void IocpStop()
{
	if (g_iocp == NULL)
		return;

	// cancel every pending operation, then close the connections (their completions
	// still hold references and are reaped by the workers below)
	vector<TcpConn*> conns;
	{
		lock_guard<mutex> lg(g_connLock);
		conns.assign(g_conns.begin(), g_conns.end());
	}

	for (auto conn : conns)
	{
		if (conn->client != INVALID_SOCKET)
			CancelIoEx((HANDLE)conn->client, NULL);

		if (conn->remote != nullptr && conn->remote->tcpSocket != INVALID_SOCKET)
			CancelIoEx((HANDLE)conn->remote->tcpSocket, NULL);

		ConnClose(conn);
	}

	{
		lock_guard<mutex> lg(g_connectLock);
		for (auto& task : g_connectQueue)
			CloseWithReset(task.client);
		g_connectQueue.clear();
	}

	// let the cancelled completions drain before the workers exit
	Sleep(50);

	g_stopWorkers = true;
	g_connectCv.notify_all();

	for (auto& worker : g_iocpWorkers)
		if (worker.joinable())
			worker.join();
	g_iocpWorkers.clear();

	for (auto& worker : g_connectors)
		if (worker.joinable())
			worker.join();
	g_connectors.clear();

	CloseHandle(g_iocp);
	g_iocp = NULL;
}


// =====================================================================================
// Legacy thread-pair pump (AIO_PUMP=legacy): kept as the runtime rollback path for the
// IOCP rewrite. Same semantics as Step A (64KiB heap buffers, half-close propagation,
// SendAll partial-write handling), two threads per connection.
// =====================================================================================

static int LegacySendAll(SOCKET s, const char* buffer, int length)
{
	int sent = 0;
	while (sent < length)
	{
		auto n = send(s, buffer + sent, length - sent, 0);
		if (n == SOCKET_ERROR)
		{
			auto err = WSAGetLastError();
			if (err != WSAEWOULDBLOCK)
				return SOCKET_ERROR;

			fd_set writeSet;
			FD_ZERO(&writeSet);
			FD_SET(s, &writeSet);

			timeval timeout{};
			timeout.tv_sec = 5;

			if (select(0, NULL, &writeSet, NULL, &timeout) <= 0)
				return SOCKET_ERROR;

			continue;
		}

		sent += n;
	}

	return sent;
}

static void LegacyRead(SOCKET client, SocksHelper::PTCP remote, ENDPOINT_ID id)
{
	auto buffer = make_unique<char[]>(64 * 1024);

	while (tcpSocket != INVALID_SOCKET)
	{
		int length = remote->Read(buffer.get(), 64 * 1024);
		if (length == 0 || length == SOCKET_ERROR)
			break;

		if (LegacySendAll(client, buffer.get(), length) == SOCKET_ERROR)
			break;

		// same semantics as the IOCP pump: bytes handed to the app are counted here
		// (tcpReceive never fires under the pump architecture)
		ConnTable::AddDl(id, (unsigned long long)length);
	}

	shutdown(client, SD_SEND);
}

static void LegacySend(SOCKET client, SocksHelper::PTCP remote)
{
	auto buffer = make_unique<char[]>(64 * 1024);

	while (tcpSocket != INVALID_SOCKET)
	{
		int length = recv(client, buffer.get(), 64 * 1024, 0);
		if (length == 0)
		{
			if (remote->tcpSocket != INVALID_SOCKET)
				shutdown(remote->tcpSocket, SD_SEND);
			return;
		}
		if (length == SOCKET_ERROR)
			return;

		if (remote->Send(buffer.get(), length) != length)
			return;
	}
}

static void LegacyHandle(SOCKET client, SOCKADDR_IN6 target, ENDPOINT_ID id)
{
	auto remote = new SocksHelper::TCP();
	if (!remote->Connect(&target))
	{
		CloseWithReset(client);
		delete remote;
		return;
	}

	auto sendThread = thread(LegacySend, client, remote);
	LegacyRead(client, remote, id);

	shutdown(client, SD_BOTH);
	sendThread.join();

	closesocket(client);
	delete remote;
}

void TCPHandler::Accept()
{
	while (tcpSocket != INVALID_SOCKET && !g_stopWorkers)
	{
		auto client = accept(tcpSocket, NULL, NULL);
		if (client == INVALID_SOCKET)
		{
			int lasterr = WSAGetLastError();
			if (lasterr == WSAEINTR || lasterr == 10004)
				return;

			// transient errors (fd exhaustion, interruption) must not kill the
			// listener thread; a burst beyond the threshold means the listener is broken
			if (++acceptFailures > 32)
			{
				printf("[Redirector][TCPHandler::Accept] fatal: %d consecutive accept failures (%d)\n", acceptFailures.load(), lasterr);
				return;
			}

			Sleep(10);
			continue;
		}

		acceptFailures = 0;

		SOCKADDR_IN6 target{};
		ENDPOINT_ID endpointId = 0;
		{
			SOCKADDR_IN6 peer;
			int addrLength = sizeof(SOCKADDR_IN6);

			if (getpeername(client, (PSOCKADDR)&peer, &addrLength) == SOCKET_ERROR)
			{
				if (verboseLog)
					printf("[pump] getpeername failed: %d\n", WSAGetLastError());
				CloseWithReset(client);
				continue;
			}

			auto key = PortOf(peer);

			// Registration (tcpConnectRequest) normally lands before accept, but the NF
			// driver's completion ordering is not a hard guarantee. Retry briefly before
			// giving up so a connection is not reset for a sub-millisecond race.
			bool found = false;
			for (int attempt = 0; attempt < 20 && !found; attempt++)
			{
				tcpLock.lock();
				auto indexIt = tcpPortIndex.find(key);
				if (indexIt != tcpPortIndex.end())
				{
					auto entryIt = tcpContext.find(indexIt->second);
					if (entryIt != tcpContext.end())
					{
						target = entryIt->second.target;
						endpointId = indexIt->second;
						found = true;
					}
				}
				tcpLock.unlock();

				if (!found)
					Sleep(2);
			}

			if (!found)
			{
				// accept raced ahead of tcpConnectRequest registration: the client retries
				if (verboseLog)
					printf("[pump] no target for source port %u\n", (unsigned)key);
				CloseWithReset(client);
				continue;
			}
		}

		if (!useIocpPump)
		{
			thread(LegacyHandle, client, target, endpointId).detach();
			continue;
		}

		// the SOCKS handshake is blocking (<=1.5s): keep it off the accept thread, but
		// bound it with a small pool instead of a thread per connection
		{
			lock_guard<mutex> lg(g_connectLock);
			if (g_connectQueue.size() >= ConnectQueueMax)
			{
				CloseWithReset(client);
				continue;
			}

			if (verboseLog)
				printf("[pump] accept client=%llu port=%u\n", (unsigned long long)client, (unsigned)PortOf((SOCKADDR_IN6&)target));
			g_connectQueue.push_back({ client, target, endpointId });
		}
		g_connectCv.notify_one();
	}
}
