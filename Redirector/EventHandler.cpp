#include "EventHandler.h"

#include "ConnTable.h"
#include "DNSHandler.h"
#include "TCPHandler.h"

extern bool filterParent;
extern bool filterTCP;
extern bool filterUDP;
extern bool filterDNS;

extern bool dnsOnly;

extern vector<wstring> bypassList;
extern vector<wstring> handleList;

// defined in Based.cpp (shared with the data plane)
extern bool verboseLog;

extern USHORT tcpListen;

DWORD CurrentID = 0;

mutex udpContextLock;
map<ENDPOINT_ID, SocksHelper::PUDP> udpContext;

// M3-1 A5: bounded UDP work queue - the NF callback only enqueues;
// two resident workers perform the (possibly slow) SOCKS Associate/CreateUDP/Send.
// Overflow drops packets and counts (game UDP self-heals; a crash does not).
struct UdpTask
{
	ENDPOINT_ID id;
	SocksHelper::PUDP remote;
	SOCKADDR_IN6 target;
	char* packet;
	int length;
	PNF_UDP_OPTIONS options; // deep-copied in the callback; the reader thread frees it
};

static mutex udpQueueLock;
static deque<UdpTask> udpQueue;
static condition_variable udpQueueCv;
static atomic<ULONGLONG> udpDropped{ 0 };
static const size_t UdpQueueMax = 4096;

// ===== M3-1 A1: hot-path slimming =====
// Compiled rule cache: rebuilt once per rule (re)dial, matched per connection without recompiling
struct CompiledRules
{
	vector<wregex> bypass;
	vector<wregex> handle;
};
static CompiledRules g_rules;
static mutex g_rulesLock;

// verbose logging switch (dial AIO_VERBOSE; defined in Based.cpp, default off -
// wcout in NF callbacks is a hot-path drag)

// pid -> process name cache with 30s expiry, capacity-guarded
static mutex g_nameCacheLock;
static map<DWORD, pair<wstring, ULONGLONG>> g_nameCache;

// process tree snapshot (pid -> ppid), refreshed at most every 5s for filterParent
static mutex g_ppidLock;
static map<DWORD, DWORD> g_ppidMap;
static ULONGLONG g_ppidTick = 0;

atomic_ullong UP = { 0 };
atomic_ullong DL = { 0 };

/// SOCKADDR -> "1.2.3.4:443" / "[::1]:443" (connection table target column)
static void AddrToString(const SOCKADDR* addr, char out[64])
{
	if (addr->sa_family == AF_INET)
	{
		auto a = (const SOCKADDR_IN*)addr;
		char ip[INET_ADDRSTRLEN] = { 0 };
		inet_ntop(AF_INET, &a->sin_addr, ip, sizeof(ip));
		snprintf(out, 64, "%s:%u", ip, ntohs(a->sin_port));
	}
	else
	{
		auto a = (const SOCKADDR_IN6*)addr;
		char ip[INET6_ADDRSTRLEN] = { 0 };
		inet_ntop(AF_INET6, &a->sin6_addr, ip, sizeof(ip));
		snprintf(out, 64, "[%s]:%u", ip, ntohs(a->sin6_port));
	}
}

wstring ConvertIP(PSOCKADDR addr)
{
	WCHAR buffer[MAX_PATH] = L"";
	DWORD bufferLength = MAX_PATH;

	if (addr->sa_family == AF_INET)
	{
		WSAAddressToStringW(addr, sizeof(SOCKADDR_IN), NULL, buffer, &bufferLength);
	}
	else
	{
		WSAAddressToStringW(addr, sizeof(SOCKADDR_IN6), NULL, buffer, &bufferLength);
	}

	return buffer;
}

wstring GetProcessName(DWORD id)
{
	if (id == 0)
	{
		return L"Idle";
	}

	if (id == 4)
	{
		return L"System";
	}

	{
		// hot path: cache hit (30s TTL) avoids the kernel query per connection
		auto now = GetTickCount64();
		{
			lock_guard<mutex> lg(g_nameCacheLock);
			auto it = g_nameCache.find(id);
			if (it != g_nameCache.end() && now - it->second.second < 30000)
				return it->second.first;
		}
	}

	wchar_t name[MAX_PATH];
	if (!nf_getProcessNameFromKernel(id, name, MAX_PATH))
	{
		if (!nf_getProcessNameW(id, name, MAX_PATH))
		{
			return L"Unknown";
		}
	}

	wchar_t data[MAX_PATH];
	wstring result;
	if (GetLongPathNameW(name, data, MAX_PATH))
		result = data;
	else
		result = name;

	{
		lock_guard<mutex> lg(g_nameCacheLock);
		if (g_nameCache.size() >= 1024)
		{
			// drop expired entries first; if still full, drop the oldest
			auto now = GetTickCount64();
			for (auto it = g_nameCache.begin(); it != g_nameCache.end();)
				it = (now - it->second.second >= 30000) ? g_nameCache.erase(it) : next(it);

			if (g_nameCache.size() >= 1024)
			{
				auto oldest = g_nameCache.begin();
				for (auto it = g_nameCache.begin(); it != g_nameCache.end(); ++it)
					if (it->second.second < oldest->second.second)
						oldest = it;
				if (oldest != g_nameCache.end())
					g_nameCache.erase(oldest);
			}
		}
		g_nameCache[id] = { result, GetTickCount64() };
	}

	return result;
}

/// Rebuild compiled rule cache after rules (re)dial (AIO_CLRNAME/ADDNAME/BYPNAME)
void RebuildCompiledRules()
{
	lock_guard<mutex> lg(g_rulesLock);

	g_rules.bypass.clear();
	g_rules.handle.clear();

	for (auto& rule : bypassList)
	{
		try { g_rules.bypass.emplace_back(rule); }
		catch (...) { printf("[Redirector][EventHandler] invalid bypass regex: %ls" "\n", rule.c_str()); }
	}

	for (auto& rule : handleList)
	{
		try { g_rules.handle.emplace_back(rule); }
		catch (...) { printf("[Redirector][EventHandler] invalid handle regex: %ls" "\n", rule.c_str()); }
	}
}

static bool MatchAny(const vector<wregex>& rules, const wstring& name)
{
	for (auto& rule : rules)
		if (regex_search(name, rule))
			return true;

	return false;
}

/// Refresh pid->ppid snapshot at most once per 5s (filterParent path used to take
/// a full process snapshot per connection)
static void RefreshPpidMap()
{
	auto now = GetTickCount64();
	{
		lock_guard<mutex> lg(g_ppidLock);
		if (g_ppidTick != 0 && now - g_ppidTick < 5000)
			return;
		g_ppidTick = now;
	}

	map<DWORD, DWORD> snapshot;

	PROCESSENTRY32W pe;
	memset(&pe, 0, sizeof(PROCESSENTRY32W));
	pe.dwSize = sizeof(PROCESSENTRY32W);

	auto h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
	if (h == INVALID_HANDLE_VALUE)
		return;

	if (Process32FirstW(h, &pe))
	{
		do {
			snapshot[pe.th32ProcessID] = pe.th32ParentProcessID;
		} while (Process32NextW(h, &pe));
	}
	CloseHandle(h);

	lock_guard<mutex> lg(g_ppidLock);
	g_ppidMap.swap(snapshot);
}

bool checkBypassName(DWORD id)
{
	auto name = GetProcessName(id);

	lock_guard<mutex> lg(g_rulesLock);
	return MatchAny(g_rules.bypass, name);
}

bool checkHandleName(DWORD id)
{
	{
		auto name = GetProcessName(id);

		lock_guard<mutex> lg(g_rulesLock);
		if (MatchAny(g_rules.handle, name))
			return true;
	}

	if (filterParent)
	{
		// throttled snapshot instead of a full process list walk per connection;
		// a pid missing from the snapshot counts as no match (same as before)
		RefreshPpidMap();

		DWORD ppid = 0;
		{
			lock_guard<mutex> lg(g_ppidLock);
			auto it = g_ppidMap.find(id);
			if (it == g_ppidMap.end())
				return false;
			ppid = it->second;
		}

		if (ppid == 0)
			return false;

		auto name = GetProcessName(ppid);

		lock_guard<mutex> lg(g_rulesLock);
		return MatchAny(g_rules.handle, name);
	}

	return false;
}

void UdpWorker();
void udpReceiveHandler(ENDPOINT_ID id, SocksHelper::PUDP remote, PNF_UDP_OPTIONS options);

bool eh_init()
{
	CurrentID = GetCurrentProcessId();

	static bool workersStarted = false;
	if (!workersStarted)
	{
		workersStarted = true;
		thread(UdpWorker).detach();
		thread(UdpWorker).detach();
	}

	if (!DNSHandler::INIT())
		return false;

	if (!TCPHandler::INIT())
		return false;

	return true;
}

void eh_free()
{
	lock_guard<mutex> lg(udpContextLock);

	TCPHandler::FREE();

	for (auto i : udpContext)
		delete i.second;
	udpContext.clear();

	ConnTable::Clear();

	UP = 0;
	DL = 0;
}

void threadStart()
{

}

void threadEnd()
{

}

void tcpConnectRequest(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	if (CurrentID == info->processId)
	{
		nf_tcpDisableFiltering(id);
		return;
	}

	if (!filterTCP)
	{
		nf_tcpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!filterTCP] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (checkBypassName(info->processId))
	{
		nf_tcpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][checkBypassName] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (!checkHandleName(info->processId))
	{
		nf_tcpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!checkHandleName] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (info->ip_family != AF_INET && info->ip_family != AF_INET6)
	{
		nf_tcpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!IPv4 && !IPv6] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	SOCKADDR_IN6 client;
	memcpy(&client, info->localAddress, sizeof(SOCKADDR_IN6));

	SOCKADDR_IN6 remote;
	memcpy(&remote, info->remoteAddress, sizeof(SOCKADDR_IN6));

	// record the original target first (the rewrite below overwrites info->remoteAddress; the stack copy is the only source of the pre-rewrite value)
	auto processName = GetProcessName(info->processId);
	char target[64];
	AddrToString((PSOCKADDR)&remote, target);
	ConnTable::Insert(id, info->processId, false, processName.c_str(), target);

	if (info->ip_family == AF_INET)
	{
		auto addr = (PSOCKADDR_IN)info->remoteAddress;
		addr->sin_family = AF_INET;
		addr->sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);
		addr->sin_port = tcpListen;
	}

	if (info->ip_family == AF_INET6)
	{
		auto addr = (PSOCKADDR_IN6)info->remoteAddress;
		IN6ADDR_SETLOOPBACK(addr);
		addr->sin6_port = tcpListen;
	}

	TCPHandler::CreateHandler(id, client, remote);
	if (verboseLog)
	{
	wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "] " << ConvertIP((PSOCKADDR)&client) << " -> " << ConvertIP((PSOCKADDR)&remote) << endl;
	}
}

void tcpConnected(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	if (verboseLog)
	{
	wcout << "[Redirector][EventHandler][tcpConnected][" << id << "][" << info->processId << "][" << ConvertIP((PSOCKADDR)info->remoteAddress) << "] " << GetProcessName(info->processId) << endl;
	}
}

void tcpCanSend(ENDPOINT_ID id)
{
	UNREFERENCED_PARAMETER(id);
}

void tcpSend(ENDPOINT_ID id, const char* buffer, int length)
{
	UP += length;
	ConnTable::AddUp(id, length);

	nf_tcpPostSend(id, buffer, length);
}

void tcpCanReceive(ENDPOINT_ID id)
{
	UNREFERENCED_PARAMETER(id);
}

void tcpReceive(ENDPOINT_ID id, const char* buffer, int length)
{
	DL += length;
	ConnTable::AddDl(id, length);

	nf_tcpPostReceive(id, buffer, length);
}

void tcpClosed(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	TCPHandler::DeleteHandler(id);
	ConnTable::MarkClosed(id);

	if (verboseLog)
		printf("[Redirector][EventHandler][tcpClosed][%llu][%lu]\n", id, info->processId);
}

void udpCreated(ENDPOINT_ID id, PNF_UDP_CONN_INFO info)
{
	if (CurrentID == info->processId)
	{
		nf_udpDisableFiltering(id);
		return;
	}

	if (!filterUDP)
	{
		if (!filterDNS) nf_udpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][!filterUDP] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (checkBypassName(info->processId))
	{
		if (dnsOnly) nf_udpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][checkBypassName] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (!checkHandleName(info->processId))
	{
		if (dnsOnly) nf_udpDisableFiltering(id);

		if (verboseLog)
		{
		wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][!checkHandleName] " << GetProcessName(info->processId) << endl;
		}
		return;
	}

	if (verboseLog)
	{
	wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "] " << GetProcessName(info->processId) << endl;
	}

	{
		lock_guard<mutex> lg(udpContextLock);
		udpContext[id] = new SocksHelper::UDP();
	}

	ConnTable::Insert(id, info->processId, true, GetProcessName(info->processId).c_str(), "-");
}

void udpConnectRequest(ENDPOINT_ID id, PNF_UDP_CONN_REQUEST info)
{
	UNREFERENCED_PARAMETER(id);
	UNREFERENCED_PARAMETER(info);
}

void udpCanSend(ENDPOINT_ID id)
{
	UNREFERENCED_PARAMETER(id);
}

void udpSend(ENDPOINT_ID id, const unsigned char* target, const char* buffer, int length, PNF_UDP_OPTIONS options)
{
	char targetStr[64];
	AddrToString((PSOCKADDR)target, targetStr);

	if (DNSHandler::IsDNS((PSOCKADDR_IN6)target))
	{
		if (!filterDNS)
		{
			nf_udpPostSend(id, target, buffer, length, options);

			if (verboseLog)
			{
			wcout << "[Redirector][EventHandler][udpSend][" << id << "] B DNS to " << ConvertIP((PSOCKADDR)target) << endl;
			}
			return;
		}
		else
		{
			UP += length;
			ConnTable::AddUp(id, length);
			ConnTable::UpdateTarget(id, targetStr);
			DNSHandler::CreateHandler(id, (PSOCKADDR_IN6)target, buffer, length, options);

			if (verboseLog)
			{
			wcout << "[Redirector][EventHandler][udpSend][" << id << "] H DNS to " << ConvertIP((PSOCKADDR)target) << endl;
			}
			return;
		}
	}

	ConnTable::AddUp(id, length);
	ConnTable::UpdateTarget(id, targetStr);

	// callback slimming: retain + enqueue, the worker does the blocking work
	SocksHelper::PUDP remote = nullptr;
	{
		lock_guard<mutex> lg(udpContextLock);
		auto it = udpContext.find(id);
		if (it == udpContext.end())
		{
			nf_udpPostSend(id, target, buffer, length, options);
			return;
		}
		remote = it->second;
	}

	if (!remote->TryRetain())
	{
		// endpoint closed concurrently: let the packet through untouched
		nf_udpPostSend(id, target, buffer, length, options);
		return;
	}

	auto packet = new char[length];
	memcpy(packet, buffer, length);

	auto option = (PNF_UDP_OPTIONS)new char[sizeof(NF_UDP_OPTIONS) + options->optionsLength];
	memcpy(option, options, sizeof(NF_UDP_OPTIONS) + options->optionsLength - 1);

	UdpTask task{ id, remote, {}, packet, length, option };
	memcpy(&task.target, target, sizeof(SOCKADDR_IN6));

	{
		lock_guard<mutex> lg(udpQueueLock);
		if (udpQueue.size() >= UdpQueueMax)
		{
			udpQueue.pop_front();
			udpDropped++;
		}
		udpQueue.push_back(move(task));
	}
	udpQueueCv.notify_one();
}

/// M3-1 A5: consume the UDP work queue (Associate/CreateUDP/read-thread spawn/Send)
void UdpWorker()
{
	while (true)
	{
		UdpTask task;
		{
			unique_lock<mutex> lg(udpQueueLock);
			udpQueueCv.wait(lg, [] { return !udpQueue.empty(); });
			task = move(udpQueue.front());
			udpQueue.pop_front();
		}

		if (task.remote->detached)
		{
			delete[] task.options;
			delete[] task.packet;
			task.remote->Release();
			continue;
		}

		if (task.remote->tcpSocket == INVALID_SOCKET && !task.remote->Associate())
		{
			delete[] task.options;
			delete[] task.packet;
			task.remote->Release();
			continue;
		}

		if (task.remote->udpSocket == INVALID_SOCKET && !task.remote->detached)
		{
			if (!task.remote->CreateUDP())
			{
				delete[] task.options;
				delete[] task.packet;
				task.remote->Release();
				continue;
			}

			// options ownership transfers to the reader thread (extra reference held)
			if (task.remote->TryRetain())
				thread(udpReceiveHandler, task.id, task.remote, task.options).detach();
			else
				delete[] task.options;
		}
		else
		{
			delete[] task.options;
		}

		if (!task.remote->detached && task.remote->Send(&task.target, task.packet, task.length) == task.length)
			UP += task.length;

		delete[] task.packet;
		task.remote->Release();
	}
}

void udpCanReceive(ENDPOINT_ID id)
{
	UNREFERENCED_PARAMETER(id);
}

void udpReceive(ENDPOINT_ID id, const unsigned char* target, const char* buffer, int length, PNF_UDP_OPTIONS options)
{
	nf_udpPostReceive(id, target, buffer, length, options);
}

void udpClosed(ENDPOINT_ID id, PNF_UDP_CONN_INFO info)
{
	UNREFERENCED_PARAMETER(info);

	if (verboseLog)
		printf("[Redirector][EventHandler][udpClosed][%llu]\n", id);
	ConnTable::MarkClosed(id);

	SocksHelper::PUDP closed = nullptr;
	{
		lock_guard<mutex> lg(udpContextLock);
		auto it = udpContext.find(id);
		if (it != udpContext.end())
		{
			closed = it->second;
			udpContext.erase(it);
		}
	}

	// M3-1 A5: mark detached and drop our reference; queue workers and the
	// reader thread release theirs - the last one recycles the object
	if (closed != nullptr)
	{
		closed->detached = true;
		closed->Release();
	}
}

void udpReceiveHandler(ENDPOINT_ID id, SocksHelper::PUDP remote, PNF_UDP_OPTIONS options)
{
	char buffer[1458];

	while (!remote->detached && remote->tcpSocket != INVALID_SOCKET && remote->udpSocket != INVALID_SOCKET)
	{
		SOCKADDR_IN6 target;

		int length = remote->Read(&target, buffer, sizeof(buffer), NULL);
		if (length == 0 || length == SOCKET_ERROR)
			break;

		DL += length;
		ConnTable::AddDl(id, length);

		nf_udpPostReceive(id, (unsigned char*)&target, buffer, length, options);
	}

	delete[] options;
	remote->Release();
}
