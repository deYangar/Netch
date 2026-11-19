# 分组二分定位全量挂死的类组合（git bash 的 filter 转义不可靠，PS 传参）
param(
    [string[]]$Classes,
    [string]$Label
)
# -File 模式下逗号串不自动拆数组，手动 split
if ($Classes.Count -eq 1 -and $Classes[0].Contains(',')) { $Classes = $Classes[0] -split ',' }
$ErrorActionPreference = 'Continue'
# MSTest4/vstest 18.12 下 TestClass= 与 = 全等匹配失效，用 ~ 包含 + OR（已实测可用）
$filter = ($Classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
if ($env:FORCE_RUNTIME_ASYNC_OFF -eq '1') { $env:DOTNET_RuntimeAsync = '0' }
Write-Host "[$Label] filter: $filter"
$p = Start-Process -FilePath 'dotnet' -ArgumentList @('test', 'Tests/Tests.csproj', '--nologo', '--no-build', '--filter', $filter) -WorkingDirectory 'C:/Users/Yang/.zcode/workspace/default/projects/Netch/netch' -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\group-$Label-out.log" -RedirectStandardError "$env:TEMP\group-$Label-err.log"
if ($p.WaitForExit(600000)) {
    Write-Host "[$Label] exit=$($p.ExitCode)"
} else {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" | Invoke-CimMethod -MethodName Terminate | Out-Null
    Write-Host "[$Label] HANG (600s timeout killed)"
}
Get-Content "$env:TEMP\group-$Label-out.log" -Tail 3
