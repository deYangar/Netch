#include "DNSHandler.h"

#include "SocksHelper.h"

extern bool dnsProx;
extern bool dnsKeepTarget;
extern string dnsHost;
extern USHORT dnsPort;

SOCKADDR_IN6 dnsAddr;

/// M3-1 A6: reply SERVFAIL on the original path instead of black-holing the query.
/// The transaction id and the question section are preserved (clients match on both);
/// only QR/RCODE and the section counts are rewritten. Used solely for the abnormal
/// case "the upstream (kernel) never answered" - normal timeouts are retried inside
/// the kernel's own dns module.
static void PostServfail(ENDPOINT_ID id, PSOCKADDR_IN6 target, const char* query, int length, PNF_UDP_OPTIONS option)
{
	if (length < 12)
		return;

	auto response = new char[length];
	memcpy(response, query, length);

	response[2] = (char)((unsigned char)response[2] | 0x80);          // QR = 1
	response[3] = (char)(((unsigned char)response[3] & 0xF0) | 0x02); // RCODE = SERVFAIL
	response[6] = response[7] = 0;                                    // ANCOUNT
	response[8] = response[9] = 0;                                    // NSCOUNT
	response[10] = response[11] = 0;                                  // ARCOUNT

	nf_udpPostReceive(id, (const unsigned char*)target, response, length, option);
	delete[] response;
}

void HandleClientDNS(ENDPOINT_ID id, PSOCKADDR_IN6 target, char* packet, int length, PNF_UDP_OPTIONS option)
{
	auto answered = false;

	auto remote = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
	if (remote != INVALID_SOCKET)
	{
		int v6only = 0;

		if (setsockopt(remote, IPPROTO_IPV6, IPV6_V6ONLY, (char*)&v6only, sizeof(v6only)) != SOCKET_ERROR)
		{
			SOCKADDR_IN6 addr;
			IN6ADDR_SETANY(&addr);

			if (bind(remote, (PSOCKADDR)&addr, sizeof(SOCKADDR_IN6)) != SOCKET_ERROR)
			{
				if (sendto(remote, packet, length, 0, (PSOCKADDR)&dnsAddr, (dnsAddr.sin6_family == AF_INET ? sizeof(SOCKADDR_IN) : sizeof(SOCKADDR_IN6))) == length)
				{
					timeval timeout{};
					timeout.tv_sec = 2;
					timeout.tv_usec = 500 * 1000;

					fd_set fds;
					FD_ZERO(&fds);
					FD_SET(remote, &fds);

					int size = select(NULL, &fds, NULL, NULL, &timeout);
					if (size != 0 && size != SOCKET_ERROR)
					{
						char buffer[1024];

						size = recvfrom(remote, buffer, sizeof(buffer), 0, NULL, NULL);
						if (size != 0 && size != SOCKET_ERROR)
						{
							nf_udpPostReceive(id, (PBYTE)target, buffer, size, option);
							answered = true;
						}
					}
				}
			}
		}
	}

	if (remote != INVALID_SOCKET)
		closesocket(remote);

	if (!answered)
		PostServfail(id, target, packet, length, option);

	delete target;
	delete[] packet;
	delete[] option;
}

void HandleRemoteDNS(ENDPOINT_ID id, PSOCKADDR_IN6 target, char* packet, int length, PNF_UDP_OPTIONS option)
{
	auto answered = false;

	auto remote = new SocksHelper::UDP();
	if (remote->Associate())
	{
		if (remote->CreateUDP())
		{
			// dnsKeepTarget: send to the original query target (sing-box route port-53 rule hijacks it);
			// otherwise send to the configured dnsAddr (v2ray semantics: query the configured upstream via proxy)
			const auto dest = dnsKeepTarget ? target : &dnsAddr;
			if (remote->Send(dest, packet, length) == length)
			{
				char buffer[1024];

				timeval timeout{};
				timeout.tv_sec = 2;
				timeout.tv_usec = 500 * 1000;

				int size = remote->Read(NULL, buffer, sizeof(buffer), &timeout);
				if (size != 0 && size != SOCKET_ERROR)
				{
					nf_udpPostReceive(id, (PBYTE)target, buffer, size, option);
					answered = true;
				}
			}
		}
	}

	delete remote;

	if (!answered)
		PostServfail(id, target, packet, length, option);

	delete target;
	delete[] packet;
	delete[] option;
}

bool DNSHandler::INIT()
{
	memset(&dnsAddr, 0, sizeof(dnsAddr));

	auto ipv4 = (PSOCKADDR_IN)&dnsAddr;
	if (inet_pton(AF_INET, dnsHost.c_str(), &ipv4->sin_addr) == 1)
	{
		ipv4->sin_family = AF_INET;
		ipv4->sin_port = htons(dnsPort);
		return true;
	}

	auto ipv6 = (PSOCKADDR_IN6)&dnsAddr;
	if (inet_pton(AF_INET6, dnsHost.c_str(), &ipv6->sin6_addr) == 1)
	{
		ipv6->sin6_family = AF_INET6;
		ipv6->sin6_port = htons(dnsPort);
		return true;
	}

	return false;
}

bool DNSHandler::IsDNS(PSOCKADDR_IN6 target)
{
	if (target->sin6_family == AF_INET)
		return ((PSOCKADDR_IN)target)->sin_port == htons(53);
	else
		return target->sin6_port == htons(53);
}

void DNSHandler::CreateHandler(ENDPOINT_ID id, PSOCKADDR_IN6 target, const char* packet, int length, PNF_UDP_OPTIONS options)
{
	auto remote = new SOCKADDR_IN6();
	auto buffer = new char[length]();
	auto option = (PNF_UDP_OPTIONS)new char[sizeof(NF_UDP_OPTIONS) + options->optionsLength];

	memcpy(remote, target, sizeof(SOCKADDR_IN6));
	memcpy(buffer, packet, length);
	memcpy(option, options, sizeof(NF_UDP_OPTIONS) + options->optionsLength - 1);

	if (!dnsProx)
		thread(HandleClientDNS, id, remote, buffer, length, option).detach();
	else
		thread(HandleRemoteDNS, id, remote, buffer, length, option).detach();
}
