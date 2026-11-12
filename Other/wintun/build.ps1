Set-Location (Split-Path $MyInvocation.MyCommand.Path -Parent)

# wintun 官方发行包（sing-box tun 依赖），版本固定便于复现
$zipName = 'wintun-0.14.1.zip'
$url = "https://www.wintun.net/builds/$zipName"
# 0.14.1 官方 zip SHA256
$sha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'

try {
    Invoke-WebRequest -Uri $url -OutFile $zipName
}
catch {
    exit 1
}

$fileHash = (Get-FileHash $zipName -Algorithm SHA256).Hash.ToLower()
if ( $fileHash -ne $sha256 ) {
    Write-Host "wintun SHA256 mismatch: $fileHash"
    exit 1
}

7z x $zipName
if ( -Not $? ) { exit $lastExitCode }

# dll 留在本目录（csproj Content 分发到构建输出 bin\），副本进 ..\release 组包
mv -Force 'wintun\bin\amd64\wintun.dll' '.\wintun.dll'
copy-Item '.\wintun.dll' '..\release\wintun.dll' -Force

rm -Recurse -Force 'wintun'
rm -Force $zipName
exit 0