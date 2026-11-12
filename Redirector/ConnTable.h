#pragma once
#ifndef CONNTABLE_H
#define CONNTABLE_H
#include "Based.h"

// Connection table entry: NF endpoint_id as primary key
// (process + original target + traffic counters + lifetime)
struct ConnEntry
{
	ENDPOINT_ID id;
	DWORD pid;
	bool udp;
	bool closed;      // lazy flag, cleaned up on export
	WCHAR process[MAX_PATH];
	char target[64];  // "1.2.3.4:443" / "[::1]:443"
	atomic_ullong up;
	atomic_ullong dl;
	ULONGLONG started; // GetTickCount64
};

namespace ConnTable
{
	void Insert(ENDPOINT_ID id, DWORD pid, bool udp, const wchar_t* process, const char* target);
	void MarkClosed(ENDPOINT_ID id);
	void AddUp(ENDPOINT_ID id, unsigned long long bytes);
	void AddDl(ENDPOINT_ID id, unsigned long long bytes);
	void UpdateTarget(ENDPOINT_ID id, const char* target);
	void Clear();

	// Snapshot export (line text protocol, pulled by C# once per second):
	// line format "T|<id>|<pid>|<process>|<target>|<up>|<dl>|<age_ms>\n" (T=TCP U=UDP)
	// returns required byte count; -1 when capacity is insufficient (caller retries with larger buffer);
	// each call cleans up closed entries; drops oldest beyond maxRows (connection storm guard)
	int Export(char* buffer, int capacity, int maxRows = 4096);
}

#endif
