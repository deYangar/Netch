# Storage/kernel — 内核随包嵌入源（不进 git）

build.ps1 发布时把用户区内核（release/bin 的 sing-box.exe / xray.exe）压缩成
`sing-box.zip` / `xray.zip` 写到这里，Netch.csproj 条件嵌入 Netch.exe；
KernelBootstrap 首启对缺失/残缺内核自动释放，用户区有效内核（含自行升级版）永不覆盖。

本目录 zip 为构建产物，已 .gitignore；全新 clone 首次构建无 zip = exe 不嵌内核（可预期）。
