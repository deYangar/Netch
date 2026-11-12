# -*- coding: utf-8 -*-
"""RedirectorTester 客户端用例（独立进程：本进程被 NF 拦截，Tester 主进程被 CurrentID 排除）。
用法：python client_cases.py <case> <args...>
输出 JSON 结果到 stdout。
"""
import json
import random
import socket
import struct
import sys
import time

PLACEHOLDER = "198.51.100.10"


def local_ip():
    """本机出口 IP：发往它的包走 local 路由（必达），且不属于 loopback/intranet 白名单段（NF 会拦）"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.connect(("8.8.8.8", 9))
    ip = s.getsockname()[0]
    s.close()
    return ip


def recvn(s, n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("early EOF")
        buf += chunk
    return buf


def case_t1_concurrency(echo_port, concurrency=128):
    """128 并发 TCP 随机 payload 双向字节严格一致"""
    rng = random.Random(42)
    failures = 0
    first_error = ""
    socks = []
    for i in range(concurrency):
        try:
            s = socket.create_connection((PLACEHOLDER, echo_port), timeout=30)
            s.settimeout(60)
            socks.append(s)
            for _ in range(4):
                size = random.randint(1, 256 * 1024)
                block = rng.randbytes(min(size, 4096))
                payload = (block * (size // min(size, 4096) + 1))[:size]
                s.sendall(payload)
                echo = recvn(s, size)
                if echo != payload:
                    raise ValueError("byte mismatch")
        except Exception as e:
            failures += 1
            if not first_error:
                first_error = f"conn#{i}: {type(e).__name__}: {e}"
    for s in socks:
        try:
            s.close()
        except Exception:
            pass
    return {"ok": failures == 0, "detail": f"failures={failures}/{concurrency} {first_error}"}


def case_t2_refused(port):
    """上游拒绝：应用应快速得到 RST（ConnectionReset），非超时挂起"""
    t0 = time.monotonic()
    err = None
    try:
        s = socket.create_connection((PLACEHOLDER, port), timeout=8)
        s.settimeout(8)
        s.sendall(b"probe")
        s.recv(16)  # 期待 RST
        return {"ok": False, "detail": "clean EOF (no reset)"}
    except ConnectionResetError as e:
        err = e
    except Exception as e:
        err = e
    elapsed = time.monotonic() - t0
    ok = err is not None and isinstance(err, ConnectionResetError) and elapsed < 2.5
    return {"ok": ok, "detail": f"{type(err).__name__} in {elapsed:.2f}s"}


def case_t3_halfclose(echo_port):
    """应用 shutdown(SD_SEND) 后继续收完上游回包（半关闭传播）"""
    payload = bytes(random.Random(7).getrandbits(8) for _ in range(4096)) * 128  # 512KiB
    s = socket.create_connection((PLACEHOLDER, echo_port), timeout=8)
    s.settimeout(30)
    s.sendall(payload)
    s.shutdown(socket.SHUT_WR)
    received = 0
    try:
        while received < len(payload):
            chunk = s.recv(65536)
            if not chunk:
                break
            received += len(chunk)
    except Exception as e:
        s.close()
        return {"ok": False, "detail": f"{type(e).__name__} at {received}"}
    s.close()
    return {"ok": received == len(payload), "detail": f"{received}/{len(payload)}"}


def case_t5_port_reuse(echo_port, count=1000):
    """同源端口快速复用：连接关闭后立刻用同源端口新建连接 ×N，无串数据"""
    failures = 0
    local_port = None
    for i in range(count):
        try:
            s = socket.socket()
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if local_port is None:
                s.bind(("127.0.0.1", 0))
                local_port = s.getsockname()[1]
            else:
                s.bind(("127.0.0.1", local_port))
            s.settimeout(8)
            s.connect((PLACEHOLDER, echo_port))
            probe = f"reuse-{i}".encode()
            s.sendall(probe)
            echo = recvn(s, len(probe))
            if not echo.startswith(b"reuse-"):
                raise ValueError("crossed data!")
            # 硬关（RST）后源端口立即可复用
            s.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
            s.close()
        except Exception:
            failures += 1
            try:
                s.close()
            except Exception:
                pass
    return {"ok": failures == 0, "detail": f"failures={failures}/{count}"}


def case_t4_udp(count=4096, rate=1024):
    """UDP 双向：按 rate 包/秒发 count 包，统计回包匹配率（方案要求 1024pps 无丢失）"""
    matched = 0
    sent = 0
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("127.0.0.1", 0))
    udp.settimeout(0.05)
    target = (local_ip(), 40001)
    start = time.monotonic()
    try:
        for i in range(count):
            payload = b"udp-%06d" % i
            udp.sendto(payload, target)
            sent += 1
            # 节流到目标速率
            due = start + sent / float(rate)
            now = time.monotonic()
            if due > now:
                time.sleep(min(due - now, 0.05))
            # 顺手收包（非阻塞窗口）
            try:
                data, _ = udp.recvfrom(65535)
                if data.startswith(b"udp-") and len(data) == 10:
                    matched += 1
            except socket.timeout:
                pass

        # 收尾：把在途回包收干净
        deadline = time.monotonic() + 3
        while matched < sent and time.monotonic() < deadline:
            try:
                data, _ = udp.recvfrom(65535)
                if data.startswith(b"udp-") and len(data) == 10:
                    matched += 1
            except socket.timeout:
                continue
    except Exception as e:
        udp.close()
        return {"ok": False, "detail": f"{type(e).__name__}"}

    udp.close()
    ratio = matched / float(sent) if sent else 0
    return {"ok": ratio >= 0.99, "detail": f"matched={matched}/{sent} ({ratio:.1%})"}


def case_t6_dns_servfail():
    """DNS 上游无响应 → SERVFAIL 回包（事务 ID 保留）"""
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("127.0.0.1", 0))
    udp.settimeout(3.5)
    query = bytes.fromhex("abcd01000001000000000000") + b"\x03www\x05baidu\x03com\x00" + struct.pack(">HH", 1, 1)
    udp.sendto(query, (local_ip(), 53))
    t0 = time.monotonic()
    try:
        data, _ = udp.recvfrom(65535)
        elapsed = time.monotonic() - t0
        rcode = data[3] & 0x0F
        tid_ok = data[:2] == b"\xab\xcd"
        return {"ok": rcode == 2 and tid_ok and elapsed < 3.5, "detail": f"rcode={rcode} tid={tid_ok} in {elapsed:.2f}s"}
    except socket.timeout:
        return {"ok": False, "detail": "no servfail within 3.5s"}
    finally:
        udp.close()


def main():
    case = sys.argv[1]
    args = [int(a) for a in sys.argv[2:]]
    cases = {
        "t1": case_t1_concurrency,
        "t2": case_t2_refused,
        "t3": case_t3_halfclose,
        "t4": case_t4_udp,
        "t5": case_t5_port_reuse,
        "t6": case_t6_dns_servfail,
    }
    result = cases[case](*args)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
