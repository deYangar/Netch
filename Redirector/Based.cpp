#include "Based.h"

bool filterLoopback = false;
bool filterIntranet = false;
bool filterParent = false;
bool filterICMP = true;
bool filterTCP = true;
bool filterUDP = true;
bool filterDNS = true;

DWORD icmping = 0;

bool dnsOnly = false;
bool dnsProx = true;
// sing-box kernel: DNS packets are sent via SOCKS to their original target (kernel-side hijack-dns); v2ray/Xray keeps sending to the configured dnsAddr
bool dnsKeepTarget = false;
string dnsHost = "1.1.1.1";
USHORT dnsPort = 53;

wstring tgtHost = L"127.0.0.1";
wstring tgtPort = L"1080";
string tgtUsername = "";
string tgtPassword = "";

// verbose logging switch (dial AIO_VERBOSE; default off)
bool verboseLog = false;

// forwarding pump selection (dial AIO_PUMP): IOCP by default, thread-pair for rollback
bool useIocpPump = true;

vector<wstring> bypassList;
vector<wstring> handleList;
