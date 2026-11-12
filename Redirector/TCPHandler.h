#pragma once
#ifndef TCPHANDLER_H
#define TCPHANDLER_H
#include "Based.h"
#include "SocksHelper.h"

namespace TCPHandler
{
	bool INIT();
	void FREE();

	void CreateHandler(ENDPOINT_ID id, SOCKADDR_IN6 client, SOCKADDR_IN6 remote);
	void DeleteHandler(ENDPOINT_ID id);

	void Accept();
}

#endif
