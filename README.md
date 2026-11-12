# Netch — Windows 代理客户端（WPF 重制版）

fork 自 [netchx/Netch](https://github.com/netchx/netch)，在其内核层基础上整体重制：
UI 重写为 WPF（Fluent 风格、三态主题、中英双语），代理内核换为 sing-box 主力 + Xray 自动兜底，
并重做了订阅管理、分流规则编辑、备份恢复与程序内更新。

## 功能特性

- **界面**：WPF + [WPF-UI](https://github.com/lepoco/wpfui) Fluent 风格；浅色 / 深色 / 跟随系统三态主题；中文 / 英文双语即时切换
- **双内核**：sing-box 主力 + Xray 自动兜底；Release 包把内核直接嵌进 exe，首次启动自动释放，用户自行升级过的内核不会被覆盖
- **模式**：进程代理（Netfilter 驱动）/ TUN 网卡代理（WinTUN）——没有全局模式开关，选哪个模式文件、启动就进哪种模式
- **订阅**：链接订阅一键拉取、分组管理、单条 / 全量更新、节点测速
- **分流规则**：规则集（远程 .srs / 本地文件）+ 模式编辑器（域名 / IP / 端口 / 协议 / 进程多维度，手写规则与规则集可混编）；模式文件支持 `//` 注释与尾随逗号
- **连接观测**：实时连接列表，IP 归属地走 ip2region v4/v6 离线库
- **备份恢复**：仅设置 / 完整（含自建模式）两种备份，zip 载体；恢复带校验（坏包 / 版本 / 类型 / 路径穿越全拒）
- **程序内更新**：主程序与内核均可在线更新，先下载后切换、全程不断网；全部外部资源下载可统一走内置代理
- **便携**：单文件自包含发布，免装 .NET 运行时；数据全在 exe 同目录，不写注册表

## 模式说明

主界面模式列表里每个条目自带类型，双击启动时按类型走对应流程（模式文件 JSON 的 `type` 字段决定）：

| 类型 | 原理 | 适用场景 |
| --- | --- | --- |
| `ProcessMode` 进程代理 | Netfilter 驱动（nfdriver.sys）按 `handle` 里的进程名正则拦截指定进程的流量 | 只想让某些程序（如某游戏、Steam）走代理，其余流量直连；进程外流量完全不碰 |
| `TunMode` 网卡代理 | WinTUN 虚拟网卡接管全局流量，由 sing-box 按 `rules` 路由规则（域名/IP/端口/协议/规则集）分流 | 全局加速、UDP 游戏、需要按域名/IP 细细分流的场景 |
| `ShareMode` 网络共享 | WinPcap / Npcap 捕获网卡流量，共享给同网段其他设备 | 尚未实装，暂不可用 |

> 旧版的 `WebMode`（本地 Web 代理口）已移除。进程代理与 TUN 模式均需管理员权限运行。

## 支持协议

[Socks5](https://www.wikiwand.com/en/SOCKS) · [Shadowsocks](https://shadowsocks.org) · [ShadowsocksR](https://github.com/shadowsocksrr/shadowsocksr-libev) · [WireGuard](https://www.wireguard.com) · [Trojan](https://trojan-gfw.github.io/trojan) · [VMess](https://www.v2fly.org) · [VLESS](https://xtls.github.io)

- UDP NAT FullCone（取决于服务端）

## 下载与安装

从 [Releases](../../releases) 下载 `Netch.exe`（约 220 MB，自包含 .NET 10 运行时 + 内嵌双内核），放入任意目录直接运行，首次启动自动释放内核。

- 数据（节点、订阅、模式、规则集缓存、日志）全在 exe 同目录的 `data/`、`mode/`、`bin/`、`logging/` 下——换目录即换数据，天然便携
- 升级直接覆盖 exe；程序内更新自动保留用户数据

## 构建与测试

依赖：.NET 10 SDK、MSVC + Windows SDK（Redirector / RouteHelper 为原生组件）、Windows x64。

```powershell
dotnet build Netch.sln -c Release      # 开发构建
dotnet test Tests/Tests.csproj         # 单元测试
pwsh -NoProfile -File build.ps1        # 发版：release/ 产出单文件自包含 exe
```

push / PR 自动跑编译与单元测试（`.github/workflows/build.yml`）；打 `v*` 标签自动构建发版（`.github/workflows/release.yml`）。

## 致谢与许可

- [NetchX/Netch](https://github.com/netchx/netch) —— 本项目的前身
- [SagerNet/sing-box](https://github.com/SagerNet/sing-box) · [XTLS/Xray-core](https://github.com/XTLS/Xray-core)
- [ip2region](https://github.com/lionsoul2014/ip2region)（离线 IP 归属地）· [WPF-UI](https://github.com/lepoco/wpfui)

以 [GPL-3.0](LICENSE) 发布，与上游同源同许可。
