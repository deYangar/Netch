#include "IPEventHandler.h"

#include <deque>

extern DWORD icmping;

// M3-1 A6: the NF callback used to Sleep(icmping) inline, blocking the driver's
// callback thread; now it only enqueues and a resident worker paces the replies
struct IcmpTask
{
	BYTE* data;
	int length;
	NF_IP_PACKET_OPTIONS options;
};

static mutex icmpQueueLock;
static condition_variable icmpQueueCv;
static deque<IcmpTask> icmpQueue;

void IcmpWorker()
{
	while (true)
	{
		IcmpTask task;
		{
			unique_lock<mutex> lg(icmpQueueLock);
			icmpQueueCv.wait(lg, [] { return !icmpQueue.empty(); });
			task = icmpQueue.front();
			icmpQueue.pop_front();
		}

		if (icmping > 0)
			this_thread::sleep_for(chrono::milliseconds(icmping));

		nf_ipPostReceive((char*)task.data, task.length, &task.options);
		delete[] task.data;
	}
}

static void EnqueueIcmpReply(BYTE* data, int length, PNF_IP_PACKET_OPTIONS options)
{
	static bool workerStarted = false;
	if (!workerStarted)
	{
		workerStarted = true;
		thread(IcmpWorker).detach();
	}

	{
		lock_guard<mutex> lg(icmpQueueLock);
		if (icmpQueue.size() >= 1024)
		{
			delete[] data;
			return;
		}
		icmpQueue.push_back({ data, length, *options });
	}
	icmpQueueCv.notify_one();
}

USHORT IPv4Checksum(PBYTE buffer, ULONG64 size)
{
	UINT32 sum = 0;
	for (int i = 0; i < size; i += 2)
	{
		sum += (buffer[i] << 8) + buffer[i + 1];
	}

	if ((size % 2) == 1)
	{
		sum += buffer[size - 1] << 8;
	}

	while (sum > 0xffff)
	{
		sum = (sum >> 16) + (sum & 0xffff);
	}

	return ~sum & 0xffff;
}

USHORT ICMPChecksum(PBYTE buffer, ULONG64 size)
{
	UINT32 sum = 0;
	for (int i = 0; i < size; i += 2)
	{
		sum += buffer[i] + (buffer[i + 1] << 8);
	}

	if ((size % 2) == 1)
	{
		sum += buffer[size - 1];
	}

	sum = (sum >> 16) + (sum & 0xffff);
	sum += (sum >> 16);

	return ~sum & 0xffff;
}

void ipSend(const char* buffer, int length, PNF_IP_PACKET_OPTIONS options)
{
	if (options->ip_family != AF_INET ||
		options->ipHeaderSize != 20   ||
		length < 28                   ||
		buffer[options->ipHeaderSize] != 0x08)
	{
		nf_ipPostSend(buffer, length, options);
		return;
	}

	auto data = new BYTE[length]();
	memcpy(data, buffer, length);

	{
		BYTE src[4];
		BYTE dst[4];
		memcpy(src, data + 12, 4);
		memcpy(dst, data + 16, 4);
		memcpy(data + 12, dst, 4);
		memcpy(data + 16, src, 4);
	}

	data[10] = 0x00;
	data[11] = 0x00;
	auto ipv4sum = IPv4Checksum(data, options->ipHeaderSize);
	data[10] = (ipv4sum >> 8);
	data[11] = ipv4sum & 0xff;

	data[options->ipHeaderSize] = 0x00;
	data[options->ipHeaderSize + 2] = 0x00;
	data[options->ipHeaderSize + 3] = 0x00;
	auto icmpsum = ICMPChecksum(data + options->ipHeaderSize, (ULONG64)length - options->ipHeaderSize);
	data[options->ipHeaderSize + 2] = icmpsum & 0xff;
	data[options->ipHeaderSize + 3] = (icmpsum >> 8);

	EnqueueIcmpReply(data, length, options);
}

void ipReceive(const char* buffer, int length, PNF_IP_PACKET_OPTIONS options)
{
	nf_ipPostReceive(buffer, length, options);
}
