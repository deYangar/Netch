param (
	[Parameter()]
	[ValidateSet('Debug', 'Release')]
	[string]
	$Configuration = 'Release',

	[Parameter()]
	[ValidateNotNullOrEmpty()]
	[string]
	$OutputPath = 'release',

	[Parameter()]
	[bool]
	$SelfContained = $True,

	# 2026-11-20 咩咩拍板：发版形态从单 exe 改 zip 文件夹（单 exe 自举要维护嵌入清单，
	# 漏一项就「Dll was not found」——ReleaseRedirector 组件缺失事故；散装 exe 进 zip
	# 本体更小启动更快，程序内 Updater 本就是 zip+顶层 Netch/ 目录契约，回 zip 即修好）
	[Parameter()]
	[bool]
	$PublishSingleFile = $False,

	[Parameter()]
	[bool]
	$PublishReadyToRun = $False
)

Push-Location (Split-Path $MyInvocation.MyCommand.Path -Parent)

# 非终止错误一律炸停（发版脚本宁可失败不可假成功：曾吞 Compress-Archive 后缀错误打出 0 MB 假成功 2026-10-31）
$ErrorActionPreference = 'Stop'

# 用户区保护（咩咩 2026-10-31 拍板治本：rm release/ 曾每次发版把 data/ 连锅端，节点全空）。
# data/ 纯用户区整保；mode/Custom 用户自定义模式整保（10-31 深夜发版曾把它清掉——咩咩 2026-11-03 发现）；
# bin/ 里内核随包与外部资源库不在下方构建拷贝清单，一并 stash 放回。
# 中断残档（$stash 存在）绝不删除：先恢复回 release 再走正常流程。
# logging/ 运行日志整保（2026-11-11 咩咩点名补齐：与 Updater.KeepDirectories 三样对齐，脚本此前漏它，每次发版清空咩咩运行日志）
$userZoneDirs = @('data', 'bin/rule-set', 'mode/Custom', 'logging')
$binKeepFiles = @('sing-box.exe', 'xray.exe', 'ip2region.xdb', 'ip2region_v6.xdb')
$stash = "$OutputPath.userzone_stash"
if ( Test-Path $stash ) {
	# 上轮炸停残档：逐项合并回现有 release（绝不整体替换——替换曾把已放回的内核随 release 一起丢掉 2026-10-31）。
	# data 用 robocopy /MOVE 合并（按 mtime 保守，不覆盖 release 已有较新文件）；内核原件 Move -Force 覆盖（用户区权威）
	Write-Warning "Interrupted stash from previous run found: restoring leftovers into release"
	if ( -Not (Test-Path $OutputPath) ) { New-Item -ItemType Directory -Name $OutputPath | Out-Null }
	foreach ( $d in $userZoneDirs ) {
		$src = Join-Path $stash $d
		if ( Test-Path $src ) {
			robocopy $src (Join-Path $OutputPath $d) /MOVE /E /NFL /NDL /NJH /NJS | Out-Null
			if ( $LASTEXITCODE -ge 8 ) { throw "robocopy restore failed ($LASTEXITCODE): $src" }
		}
	}
	foreach ( $f in $binKeepFiles ) {
		$src = Join-Path $stash $f
		if ( Test-Path $src ) { Move-Item $src (Join-Path "$OutputPath\bin" $f) -Force }
	}
	rm -Recurse -Force $stash
}
if ( Test-Path -Path $OutputPath ) {
	New-Item -ItemType Directory $stash | Out-Null
	foreach ( $d in $userZoneDirs ) {
		$src = Join-Path $OutputPath $d
		if ( Test-Path $src ) {
			# 多级用户区（bin/rule-set）的目标父目录必须先建：Move-Item 不建父目录，
			# 漏建曾炸停整个构建段且 powershell 仍退 0（2026-11-02 RE 发版实测）
			$dstDir = Join-Path $stash $d
			New-Item -ItemType Directory (Split-Path $dstDir -Parent) -Force | Out-Null
			Move-Item $src $dstDir
		}
	}
	foreach ( $f in $binKeepFiles ) {
		$src = Join-Path "$OutputPath\bin" $f
		if ( Test-Path $src ) { Move-Item $src (Join-Path $stash $f) }
	}
	rm -Recurse -Force $OutputPath
}

# 2026-11-20 发版形态改 zip：不再生成内核嵌入包。清掉 Storage\kernel 旧残留——
# csproj 条件嵌入仍在（Exists 才嵌），旧 zip 不清会被继续嵌进 exe 且永远没人更新它
$kernelEmbedDir = Join-Path (Get-Location) "Storage\kernel"
if ( Test-Path $kernelEmbedDir ) {
	rm -Recurse -Force $kernelEmbedDir
	Write-Host "Cleared legacy kernel embed dir (zip shipping mode)"
}
# 内核随 zip 分发：stash 收走的内核/归属地库在尾部放回后进包，缺失即告警（防打出不完整包）
$stashAbs = Join-Path (Get-Location) $stash
foreach ( $k in $binKeepFiles ) {
	if ( Test-Path (Join-Path $stashAbs $k) ) { continue }
	Write-Warning "NOT in user zone (bin\$k): zip will ship WITHOUT $k"
}

# 干净构建（XAML/嵌入资源曾不被 dotnet 增量编译吃进 publish 产物：连续两版 exe 哈希相同事故 2026-10-31）
rm -Recurse -Force ".\Netch\obj", ".\Netch\bin" -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Name $OutputPath | Out-Null

Push-Location $OutputPath
New-Item -ItemType Directory -Name 'bin'  | Out-Null
cp -Recurse -Force '..\Storage\i18n' '.'  | Out-Null
cp -Recurse -Force '..\Storage\mode' '.'  | Out-Null
cp -Recurse -Force '..\Storage\stun.txt' 'bin'  | Out-Null
cp -Recurse -Force '..\Storage\nfdriver.sys' 'bin'  | Out-Null
cp -Recurse -Force '..\Storage\aiodns.conf' 'bin'  | Out-Null
# 归属地库不随包（v4 11MB/v6 37MB 不进 git 也不走脚本下载）：外部资源页自动更新通道首次拉取
cp -Recurse -Force '..\Storage\tun2socks.bin' 'bin'  | Out-Null
cp -Recurse -Force '..\Storage\README.md' 'bin'  | Out-Null
Pop-Location

if ( -Not ( Test-Path '.\Other\release' ) ) {
	.\Other\build.ps1
	if ( -Not $? ) {
		exit $lastExitCode
	}
}
cp -Force '.\Other\release\*.bin' "$OutputPath\bin"
cp -Force '.\Other\release\*.dll' "$OutputPath\bin"
cp -Force '.\Other\release\*.exe' "$OutputPath\bin"

# publish 成品标记：bin 里的 Netch.exe 可能是 publish 出的也可能是 dotnet test/build 普通编译的
# （455KB 假 exe 两次事故 2026-10-29：只判目录存在、判 NOTICE.txt 都放行过假 exe；而 NOTICE.txt
# 只有 CopyToOutputDirectory，publish 根本不产出它，.NET 10 publish 清空输出目录后即消失）。
# 改用 build.ps1 publish 完成后自写的 .publish_stamp，mtime 钉在 exe 之后：exe 晚于标记 =
# publish 之后被普通编译污染过，强制重 publish
$publishStamp = ".\Netch\bin\$Configuration\.publish_stamp"
$publishExe = ".\Netch\bin\$Configuration\Netch.exe"
$publishFresh = (Test-Path $publishStamp) -and (Test-Path $publishExe) -and
	((Get-Item $publishExe).LastWriteTime -le (Get-Item $publishStamp).LastWriteTime.AddSeconds(2))
if ( -Not $publishFresh ) {
	Write-Host
	Write-Host 'Building Netch'

	dotnet publish `
		-c $Configuration `
		-r 'win-x64' `
		-p:Platform='x64' `
		-p:SelfContained=$SelfContained `
		-p:PublishTrimmed=$PublishReadyToRun `
		-p:PublishSingleFile=$PublishSingleFile `
		-p:PublishReadyToRun=$PublishReadyToRun `
		-p:PublishReadyToRunShowWarnings=$PublishReadyToRun `
		-p:SatelliteResourceLanguages='zh-CN' `
		-o ".\Netch\bin\$Configuration" `
		'.\Netch\Netch.csproj'
	if ( -Not $? ) { exit $lastExitCode }
	$now = [DateTime]::Now
	(Get-Item $publishExe).LastWriteTime = $now
	Set-Content -Path $publishStamp -Value $now.ToString('o')
	(Get-Item $publishStamp).LastWriteTime = $now.AddSeconds(1)
}
# 散装 publish 全量拷进 release（原单文件时代只拷 Netch.exe 一个；PublishSingleFile=False 后
# publish 输出是 exe+dll 散件，漏拷即缺运行库）。.publish_stamp 是本脚本的假产物防重标记，不进包
$publishOut = ".\Netch\bin\$Configuration"
Get-ChildItem $publishOut | Where-Object { $_.Name -ne '.publish_stamp' } |
	Copy-Item -Destination $OutputPath -Recurse -Force

if ( -Not ( Test-Path ".\Redirector\bin\$Configuration" ) ) {
	Write-Host
	Write-Host 'Building Redirector'

	msbuild `
		-property:Configuration=$Configuration `
		-property:Platform=x64 `
		'.\Redirector\Redirector.vcxproj'
	if ( -Not $? ) { exit $lastExitCode }
}
cp -Force ".\Redirector\bin\$Configuration\nfapi.dll"      "$OutputPath\bin"
cp -Force ".\Redirector\bin\$Configuration\Redirector.bin" "$OutputPath\bin"

if ( -Not ( Test-Path ".\RouteHelper\bin\$Configuration" ) ) {
	Write-Host
	Write-Host 'Building RouteHelper'

	msbuild `
		-property:Configuration=$Configuration `
		-property:Platform=x64 `
		'.\RouteHelper\RouteHelper.vcxproj'
	if ( -Not $? ) { exit $lastExitCode }
}
cp -Force ".\RouteHelper\bin\$Configuration\RouteHelper.bin" "$OutputPath\bin"

if ( $Configuration.Equals('Release') ) {
	rm -Force "$OutputPath\*.pdb"
	rm -Force "$OutputPath\*.xml"
}

# 用户区放回（见顶部保护逻辑）。放回不全必须炸停绝不删 stash：
# 曾因非终止错误吞掉 Move 失败、无条件删 stash 而真丢内核（sing-box.exe 2026-10-31 事故）。
# 目录一律 robocopy /MOVE 合并（2026-11-20 散装化后 publish 输出自带 bin\rule-set 内置快照，
# release 侧目录已存在，裸 Move-Item 目录到已存在路径会炸；默认 mtime 语义下内置 srs 较新
# 保留发版版、用户新增缓存文件照常挪回）
foreach ( $d in $userZoneDirs ) {
	$src = Join-Path $stash $d
	if ( Test-Path $src ) {
		robocopy $src (Join-Path $OutputPath $d) /MOVE /E /NFL /NDL /NJH /NJS | Out-Null
		if ( $LASTEXITCODE -ge 8 ) { throw "robocopy restore failed ($LASTEXITCODE): $src" }
	}
}
foreach ( $f in $binKeepFiles ) {
	$src = Join-Path $stash $f
	if ( Test-Path $src ) { Move-Item $src (Join-Path "$OutputPath\bin" $f) -Force }  # -Force：Other/release 的 *.exe 通配可能已拷同名件（如 xray.exe），用户区原件权威
}
# 只认文件：robocopy /MOVE 与 Move-Item 挪净内容后会留空壳目录（bin），空目录不是数据丢失
$leftover = Get-ChildItem $stash -Recurse -File -ErrorAction SilentlyContinue
if ( $leftover ) {
	Pop-Location
	throw "user zone stash not fully restored: $($leftover.FullName -join ', ')"
}
rm -Recurse -Force $stash

# ===== 发版 zip（2026-11-20 咩咩拍板：zip 文件夹替代单 exe 分发）=====
# 顶层 Netch/ 目录是程序内 Updater.ApplyUpdate 的硬契约（7za 解压后必须存在 extract\Netch\Netch.exe）。
# 红线：release/ 是咩咩日用目录，用户区绝不进公开发布包。排除清单有意窄于 userZoneDirs——
# data（订阅节点）、mode\Custom（自定义模式）、logging（运行日志，含节点域名）排除；
# bin\rule-set 保留：内置规则集快照的分发位，混入的用户缓存 srs 是公开规则文件无隐私
$zipExcludeDirs = @('data', 'mode\Custom', 'logging')
$zipName = 'Netch-win64.zip'
$zipPath = Join-Path (Get-Location) $zipName
if ( Test-Path $zipPath ) { rm -Force $zipPath }
# staging 干净结构再打包：Compress-Archive 无排除参数，先 robocopy 出「无用户区」的副本
# （全程绝对路径防 Push/Pop CWD 陷阱；*.tmp/.*.old 残件不进包）
$releaseAbs = Join-Path (Get-Location) $OutputPath
$zipStage = Join-Path ([IO.Path]::GetTempPath()) ("netch-zip-" + [IO.Path]::GetRandomFileName())
$stageRoot = Join-Path $zipStage 'Netch'
New-Item -ItemType Directory $stageRoot | Out-Null
try {
	$xd = $zipExcludeDirs | ForEach-Object { Join-Path $releaseAbs $_ }
	robocopy $releaseAbs $stageRoot /E /XD $xd /XF *.tmp *.old* /NFL /NDL /NJH /NJS /NP | Out-Null
	if ( $LASTEXITCODE -ge 8 ) { throw "robocopy zip stage failed ($LASTEXITCODE)" }

	# tmp 必须以 .zip 结尾：Compress-Archive 拒绝其它后缀（NotSupportedArchiveFileExtension，
	# 曾被非终止错误语义静默吞掉打出 0 MB 假成功 2026-10-31）
	$tmpZip = "$zipPath.tmp.zip"
	if ( Test-Path $tmpZip ) { rm -Force $tmpZip }
	Compress-Archive -Path $stageRoot -DestinationPath $tmpZip -CompressionLevel Optimal -Force
	Move-Item $tmpZip $zipPath -Force
	Write-Host "Release zip: $zipName ($([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB)"
}
finally {
	if ( Test-Path $zipStage ) { rm -Recurse -Force $zipStage }
}

Pop-Location
exit 0
