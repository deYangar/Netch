#pragma once
#ifndef SOCKSHELPER_H
#define SOCKSHELPER_H
#include "Based.h"

namespace SocksHelper
{
	SOCKET Connect();
	bool Handshake(SOCKET client);
	bool SplitAddr(SOCKET client, PSOCKADDR_IN6 addr);

	typedef class TCP
	{
	public:
		~TCP();

		bool Connect(PSOCKADDR_IN6 target);

		int Send(const char* buffer, int length);
		int Read(char* buffer, int length);

		SOCKET tcpSocket = INVALID_SOCKET;
	} *PTCP;

	typedef class UDP
	{
	public:
		~UDP();

		static void Run(SOCKET tcpSocket, SOCKET udpSocket);

		bool Associate();
		bool CreateUDP();

		int Send(PSOCKADDR_IN6 target, const char* buffer, int length);
		int Read(PSOCKADDR_IN6 target, char* buffer, int length, PTIMEVAL timeout);

		// M3-1 A5: ownership control - udpClosed marks detached instead of deleting
		// under the receiver thread; the last Release() recycles the object
		atomic<bool> detached{ false };
		atomic<int> refs{ 1 };

		bool TryRetain()
		{
			int current = refs.load();
			while (current > 0)
			{
				if (refs.compare_exchange_weak(current, current + 1))
					return true;
			}
			return false;
		}

		void Release()
		{
			if (--refs == 0)
				delete this;
		}

		SOCKET tcpSocket = INVALID_SOCKET;
		SOCKET udpSocket = INVALID_SOCKET;

		SOCKADDR_IN6 address = { 0 };
	} *PUDP;
};

#endif
