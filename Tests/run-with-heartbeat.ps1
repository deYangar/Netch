# 带进度心跳跑 dotnet test：每 10 秒报新增输出与已通过数；90 秒无新输出判定挂死并杀掉
# 用法：pwsh -File run-with-heartbeat.ps1 -Classes A,B -Label R1 [-TimeoutSec 120] [-Serial]
# -Serial：全量禁并行（mihomo 在线等环境下并行合跑会挂死，全绿基准就是串行跑出来的）
param(
    [string[]]$Classes,
    [string]$Label,
    [int]$TimeoutSec = 120,
    [switch]$Serial
)
if ($Classes.Count -eq 1 -and $Classes[0].Contains(',')) { $Classes = $Classes[0] -split ',' }
$ErrorActionPreference = 'Continue'

$filter = ($Classes | Where-Object { $_ } | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
# -c Release：与 CI 同配置（脚本曾漏配置跑过 Debug 旧产物，治本改动「没生效」的假象由此而来）
$args = @('test', 'Tests/Tests.csproj', '-c', 'Release', '--nologo', '--no-build')
if ($filter) { $args += @('--filter', $filter) }
$args += @('--logger', 'console;verbosity=normal')
if ($Serial) { $args += @('--', 'RunConfiguration.DisableParallelization=true') }
$out = "$env:TEMP\hb-$Label-out.log"
$err = "$env:TEMP\hb-$Label-err.log"
Remove-Item $out, $err -ErrorAction SilentlyContinue

$p = Start-Process -FilePath 'dotnet' -ArgumentList $args -WorkingDirectory 'C:/Users/Yang/.zcode/workspace/default/projects/Netch/netch' -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err

$lastSize = 0
$lastChange = Get-Date
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$verdict = 'EXITED'

while (-not $p.HasExited) {
    Start-Sleep -Seconds 10
    if (Test-Path $out) {
        $size = (Get-Item $out).Length
        if ($size -ne $lastSize) {
            $lastSize = $size
            $lastChange = Get-Date
            $passed = (Select-String -Path $out -Pattern '已通过|Passed' -AllMatches | Measure-Object).Count
            Write-Host ("[{0}] {1,3}s alive | output {2}B | passed-lines {3}" -f $Label, $sw.Elapsed.Seconds, $size, $passed)
        } else {
            $idle = ((Get-Date) - $lastChange).TotalSeconds
            Write-Host ("[{0}] {1,3}s NO NEW OUTPUT for {2}s" -f $Label, $sw.Elapsed.Seconds, [int]$idle)
            if ($idle -ge 90) { $verdict = 'HANG(killed at 90s idle)'; break }
        }
    }
    if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { $verdict = "TIMEOUT($TimeoutSec)"; break }
}

if (-not $p.HasExited) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" | Invoke-CimMethod -MethodName Terminate | Out-Null
    Start-Sleep -Seconds 2
} else {
    $verdict = "exit=$($p.ExitCode)"
}

Write-Host "[$Label] VERDICT: $verdict"
if (Test-Path $out) { Get-Content $out -Tail 3 | ForEach-Object { Write-Host "  $_" } }
