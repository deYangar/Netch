#pragma once
#ifndef BASED_H
#define BASED_H
#include <stdio.h>

#include <map>
#include <set>
#include <list>
#include <deque>
#include <queue>
#include <memory>
#include <atomic>
#include <condition_variable>
#include <regex>
#include <mutex>
#include <chrono>
#include <string>
#include <vector>
#include <thread>
#include <iostream>

#include <WinSock2.h>
#include <ws2ipdef.h>
#include <WS2tcpip.h>
#include <tlhelp32.h>
#include <mstcpip.h>
#include <Windows.h>

#include <nfapi.h>

using namespace std;

typedef enum _AIO_TYPE {
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

	// append new constants at the enum tail only (aio_dial passes ordinals; C# NameList must match)
	AIO_DNSKEEP,
	AIO_VERBOSE,
	// "iocp" (default) or "legacy": forwarding pump selection, runtime rollback switch
	AIO_PUMP
} AIO_TYPE;

#endif
