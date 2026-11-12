Set-Location (Split-Path $MyInvocation.MyCommand.Path -Parent)

# sing-box 官方发行二进制（GPLv3，随包分发），版本固定便于复现
$version = '1.14.4'
$zipName = "sing-box-$version-windows-amd64.zip"
$url = "https://github.com/SagerNet/sing-box/releases/download/v$version/$zipName"
# 1.14.4 windows-amd64 官方 zip SHA256
$sha256 = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'

Invoke-WebRequest -Uri $url -OutFile $zipName
if ( -Not $? ) {
    exit $lastExitCode
}

$fileHash = (Get-FileHash $zipName -Algorithm SHA256).Hash.ToLower()
if ( $fileHash -ne $sha256 ) {
    Write-Host "sing-box SHA256 mismatch: $fileHash"
    exit 1
}

Expand-Archive -Path $zipName -DestinationPath 'extract'
if ( -Not $? ) {
    exit $lastExitCode
}

Copy-Item "extract\sing-box-$version-windows-amd64\sing-box.exe" '..\..\release\sing-box.exe'
Remove-Item -Recurse -Force 'extract'
Remove-Item $zipName

exit 0
