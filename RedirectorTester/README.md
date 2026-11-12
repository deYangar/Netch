# RedirectorTester · 数据面验收

两套验收，覆盖不同前置条件：

| 套件 | 前置 | 覆盖 |
| --- | --- | --- |
| `RedirectorTester.exe`（C#, net8.0） | **管理员**（需装 `netfilter2.sys`） | NF 驱动真实拦截链路：T1 128 并发字节一致 / T2 上游拒绝 RST / T3 半关闭 / T4 UDP 1024pps / T5 源端口复用 / T6 DNS SERVFAIL / T7 超长凭据 |
| `DataPlaneHarness/DataPlaneHarness.exe`（C++） | **无需驱动、无需提权** | 转发泵本身：B1 128 并发字节一致 / B2 半关闭 / B3 源端口复用 ×1000 / B4 单连接吞吐 / B5 128 并发聚合吞吐 / B6 500 连接内存 |

## RedirectorTester（驱动链路）

```bat
run_tester.bat            :: 提权运行，结果写 tester_result.txt
```

客户端用例在独立 python 子进程里跑（本进程被 `CurrentID` 排除，NF 不会拦自己），
`AIO_ADDNAME` 需含 `python`。`client_cases.py` 与 exe 同目录。

## DataPlaneHarness（用户态泵验收）

```bat
msbuild DataPlaneHarness.vcxproj /p:Configuration=Release /p:Platform=x64
bin\Release\DataPlaneHarness.exe            :: IOCP 泵（AIO_PUMP=iocp，默认）
bin\Release\DataPlaneHarness.exe --legacy   :: 线程对泵（AIO_PUMP=legacy，回退路径）
```

harness 直接调用 `TCPHandler::CreateHandler` 注册连接（与 NF 回调 `tcpConnectRequest`
写的是同一张 endpoint_id 主表 + 源端口索引），随后客户端连监听端口，走完整
`accept → 有界握手池 → SOCKS5 CONNECT → 转发泵` 链路。因此它验证的是数据面本体，
不覆盖驱动层。

参考基线（本机 i7 / 回环，同一次运行内对比）：

| 指标 | IOCP | legacy（Step A 线程对） |
| --- | --- | --- |
| B4 单连接 64MiB | 481 MB/s | 385 MB/s |
| B5 128 并发聚合 | 6023 MB/s | 5885 MB/s |
| B6 500 连接私有内存增量 | 2247 KiB/conn | 2283 KiB/conn |

> B6 的绝对值由 harness 自身的「每连接 3 个线程」主导，两边同构，故只能作相对比较；
> Redirector 自身的每连接堆占用两边都是 128KiB（两方向各 64KiB），B 版的收益是
> **去掉每连接 2 个 OS 线程**（500 连接 = 1000 线程 → 8 个 IOCP worker）。
