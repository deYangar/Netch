#include "ConnTable.h"

#include "Utils.h"

namespace ConnTable
{
	static mutex tableLock;
	static map<ENDPOINT_ID, ConnEntry> table;

	void Insert(ENDPOINT_ID id, DWORD pid, bool udp, const wchar_t* process, const char* target)
	{
		lock_guard<mutex> lg(tableLock);

		auto& e = table[id];
		e.id = id;
		e.pid = pid;
		e.udp = udp;
		e.closed = false;
		wcsncpy_s(e.process, _countof(e.process), process ? process : L"", _TRUNCATE);
		strncpy_s(e.target, _countof(e.target), target ? target : "-", _TRUNCATE);
		e.up = 0;
		e.dl = 0;
		e.started = GetTickCount64();
	}

	void MarkClosed(ENDPOINT_ID id)
	{
		lock_guard<mutex> lg(tableLock);

		if (table.find(id) != table.end())
			table[id].closed = true;
	}

	// Lookup + add bytes under lock: entries may be cleaned by Export/Clear at any time,
	// and the whole entry lives under tableLock (Export runs once per second for microseconds;
	// per-packet lock cost is acceptable here, lock-free snapshot comes with M3-1 dataplane rework)
	void AddUp(ENDPOINT_ID id, unsigned long long bytes)
	{
		if (bytes == 0)
			return;

		lock_guard<mutex> lg(tableLock);

		if (table.find(id) != table.end())
			table[id].up += bytes;
	}

	void AddDl(ENDPOINT_ID id, unsigned long long bytes)
	{
		if (bytes == 0)
			return;

		lock_guard<mutex> lg(tableLock);

		if (table.find(id) != table.end())
			table[id].dl += bytes;
	}

	void UpdateTarget(ENDPOINT_ID id, const char* target)
	{
		if (!target || !target[0])
			return;

		lock_guard<mutex> lg(tableLock);

		if (table.find(id) != table.end() && strcmp(table[id].target, target) != 0)
			strncpy_s(table[id].target, _countof(table[id].target), target, _TRUNCATE);
	}

	void Clear()
	{
		lock_guard<mutex> lg(tableLock);
		table.clear();
	}

	int Export(char* buffer, int capacity, int maxRows)
	{
		lock_guard<mutex> lg(tableLock);

		// lazy cleanup of closed entries
		for (auto it = table.begin(); it != table.end();)
			it = it->second.closed ? table.erase(it) : next(it);

		// active entries beyond the cap drop the oldest (by started)
		vector<ConnEntry*> entries;
		entries.reserve(table.size());
		for (auto& kv : table)
			entries.push_back(&kv.second);

		if ((int)entries.size() > maxRows)
		{
			sort(entries.begin(), entries.end(), [](const ConnEntry* a, const ConnEntry* b) { return a->started < b->started; });
			entries.erase(entries.begin(), entries.end() - maxRows);
		}

		string out;
		out.reserve(entries.size() * 160);

		auto now = GetTickCount64();
		char line[MAX_PATH * 2 + 128];
		for (auto* e : entries)
		{
			char process[MAX_PATH * 2];
			auto processLen = WideCharToMultiByte(CP_UTF8, 0, e->process, -1, process, sizeof(process), NULL, NULL);
			if (processLen <= 0)
				strcpy_s(process, "?");
			else
				process[processLen - 1] = 0;

			snprintf(line, sizeof(line), "%c|%llu|%lu|%s|%s|%llu|%llu|%llu\n",
				e->udp ? 'U' : 'T', e->id, e->pid, process, e->target,
				e->up.load(), e->dl.load(), now - e->started);
			out += line;
		}

		if ((int)out.size() + 1 > capacity)
			return -1;

		memcpy(buffer, out.data(), out.size() + 1);
		return (int)out.size();
	}
}
