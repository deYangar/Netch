# SingboxConfigVerifier

生成 sing-box 客户端配置样例并调用 `sing-box check` 校验，用于回归 `SingboxConfigUtils` 的字段映射。

## 用法

```powershell
# 1. 生成全部协议样例配置到指定目录
dotnet run --project SingboxConfigVerifier -c Release -- <输出目录>

# 2. 生成并逐个调用 sing-box check（--check 后跟 sing-box.exe 路径）
dotnet run --project SingboxConfigVerifier -c Release -- <输出目录> --check <sing-box.exe 路径>
```

sing-box 是严格 JSON 解析（未知字段即报错），任何协议字段映射错误都会在这里暴露。

## 覆盖协议

anytls / hysteria2（含最小字段样例）/ tuic / shadowsocks（含插件）/ trojan /
vmess（tcp / ws+tls / grpc / quic 等 transport 变体）/ vless / socks5 / wireguard（endpoint）/ ssh

mKCP 与 QUIC 传输 sing-box 未实现（或无法表达 v2ray 的加密参数），对应节点由
`MainController.CreateServerController` 自动路由到 Xray 兜底内核，不在本工具覆盖范围。
