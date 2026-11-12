# 内置规则集快照下载（构建期）：
# 从 SagerNet 官方 sing-geosite/sing-geoip 仓库 rule-set 分支拉取 .srs，
# 生成 SHA256 清单；产物随包分发到 bin\rule-set\，作为 remote rule_set 的 initial_path 离线兜底。
# 源选型 2026-10-26 实测：LoyalLogic release 分支 404，SagerNet 官方直链可用。
# 2026-10-29 换 jsDelivr CDN（运行期外部资源通道同步换）：raw.githubusercontent 直连不稳，CDN 国内可达。
param(
    [string]$Proxy = $null
)

$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

$rules = @(
    @{ Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geosite@rule-set/geosite-cn.srs"; Out = "geosite-cn.srs" },
    @{ Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geoip@rule-set/geoip-cn.srs"; Out = "geoip-cn.srs" }
)

$manifest = @()
foreach ($rule in $rules) {
    $target = Join-Path $dir $rule.Out
    if ($Proxy) {
        Invoke-WebRequest -Uri $rule.Url -OutFile $target -Proxy $Proxy
    }
    else {
        Invoke-WebRequest -Uri $rule.Url -OutFile $target
    }

    $magic = [System.IO.File]::ReadAllBytes($target)[0..2]
    if ([System.Text.Encoding]::ASCII.GetString($magic) -ne "SRS") {
        throw "$($rule.Out): not a valid .srs file (magic mismatch)"
    }

    $hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
    $manifest += "$hash  $($rule.Out)"
    Write-Host "$($rule.Out): $hash"
}

$manifest | Set-Content -Encoding ascii (Join-Path $dir "SHA256SUMS")
Write-Host "Done. Manifest: $(Join-Path $dir 'SHA256SUMS')"
