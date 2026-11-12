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

	[Parameter()]
	[bool]
	$PublishSingleFile = $True,

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

# 内核随包嵌入源（咩咩 2026-10-31 拍板：发版 exe 自带内核，首启缺失/残缺自动释放，
# 用户区有效内核（含自行升级版）永不回写——覆盖语义在 KernelBootstrap）。
# 从 stash 的发版内核压缩生成 Storage\kernel\*.zip（csproj 条件嵌入）；内核不在用户区=exe 不嵌该内核并告警
$kernelEmbedDir = Join-Path (Get-Location) "Storage\kernel"
New-Item -ItemType Directory $kernelEmbedDir -Force | Out-Null
$stashAbs = Join-Path (Get-Location) $stash
foreach ( $k in $binKeepFiles ) {
	if ( $k -notmatch '\.exe$' ) { continue }   # 只嵌内核 exe；归属地库 37MB 不随 exe（外部资源页首拉通道）
	$src = Join-Path $stashAbs $k
	$zipName = [IO.Path]::GetFileNameWithoutExtension($k) + '.zip'
	$dst = Join-Path $kernelEmbedDir $zipName
	if ( Test-Path $src ) {
		# tmp 必须以 .zip 结尾：Compress-Archive 拒绝其它后缀（NotSupportedArchiveFileExtension，
		# 曾被非终止错误语义静默吞掉、连续两版打出 0 MB 假成功 2026-10-31）；全程绝对路径防 Push/Pop CWD 陷阱
		$tmpZip = "$dst.tmp.zip"
		if ( Test-Path $tmpZip ) { rm -Force $tmpZip }
		Compress-Archive -Path (Join-Path $stashAbs $k) -DestinationPath $tmpZip -CompressionLevel Optimal -Force
		Move-Item $tmpZip $dst -Force
		Write-Host "Kernel embedded: $k -> $zipName ($([math]::Round((Get-Item $dst).Length/1MB,1)) MB)"
	}
	else {
		Write-Warning "Kernel NOT in user zone (bin\$k): Netch.exe will ship WITHOUT embedded $k"
	}
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
# 内核 zip 比上次 publish 新 → 删 stamp 强制重 publish（重嵌内核进 exe；否则代码没变时旧 exe 一直嵌着旧内核包）
foreach ( $z in Get-ChildItem $kernelEmbedDir -Filter '*.zip' -ErrorAction SilentlyContinue ) {
	if ( (Test-Path $publishStamp) -and ($z.LastWriteTime -gt (Get-Item $publishStamp).LastWriteTime) ) {
		rm -Force $publishStamp
		Write-Host "Kernel zip newer than last publish: forcing republish"
		break
	}
}
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
		-p:IncludeNativeLibrariesForSelfExtract=$SelfContained `
		-p:SatelliteResourceLanguages='zh-CN' `
		-o ".\Netch\bin\$Configuration" `
		'.\Netch\Netch.csproj'
	if ( -Not $? ) { exit $lastExitCode }
	$now = [DateTime]::Now
	(Get-Item $publishExe).LastWriteTime = $now
	Set-Content -Path $publishStamp -Value $now.ToString('o')
	(Get-Item $publishStamp).LastWriteTime = $now.AddSeconds(1)
}
cp -Force ".\Netch\bin\$Configuration\Netch.exe" $OutputPath

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
# 曾因非终止错误吞掉 Move 失败、无条件删 stash 而真丢内核（sing-box.exe 2026-10-31 事故）
foreach ( $d in $userZoneDirs ) {
	$src = Join-Path $stash $d
	if ( Test-Path $src ) { Move-Item $src (Join-Path $OutputPath $d) }
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

Pop-Location
exit 0
