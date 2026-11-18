<#
.SYNOPSIS
  版本发布与更新日志管理：记录改动 -> 升版本号 -> 写入更新日志 -> 运行测试 -> 编译 -> 打包 -> 把用户配置带到新版本。

.DESCRIPTION
  更新日志在仓库根目录 CHANGELOG.md。日常改动记到「## [未发布]」下，发版时自动改为「## [版本号] - 日期」，
  并随发布包分发（更新日志.md），软件内工具栏「更新日志」也能查看（CHANGELOG.md 嵌入 exe）。
  发布失败（测试/编译不过）时，csproj 版本号和更新日志会还原。

.EXAMPLE
  .\tools\release.ps1 -AddNote "支持百望云链接"                 # 记一条「新增」到「未发布」，不发版
  .\tools\release.ps1 -AddNote "修复日期识别" -Type 修复
  .\tools\release.ps1 -Changes                                  # 查看「未发布」的改动
  .\tools\release.ps1 -History                                  # 列出已发布的版本
  .\tools\release.ps1                                           # 版本号 patch +1 并发布（1.0.0 -> 1.0.1）
  .\tools\release.ps1 -Bump minor                               # 1.0.0 -> 1.1.0
  .\tools\release.ps1 -Version 2.0.0 -Notes "全新界面","支持 OFD"  # 发版时顺便补几条改动
  .\tools\release.ps1 -Rebuild                                  # 不改版本号、不动日志，重新生成当前版本
  .\tools\release.ps1 -Edition Public                           # 只生成开源公开版（默认 All：公开版 + 内部版）
#>
param(
    [string]$Version,
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [switch]$Rebuild,        # 使用 csproj 里的当前版本号，不自增
    [switch]$SkipTests,
    [int]$Keep = 2,          # 保留最近几个版本目录/压缩包
    [string[]]$Notes,        # 发版时追加到「未发布」的改动
    [string]$AddNote,        # 只记录一条改动，不发版
    [ValidateSet('新增', '改进', '修复')][string]$Type = '新增',
    [switch]$Changes,        # 只显示「未发布」的改动
    [switch]$History,        # 只列出已发布版本
    [switch]$AllowEmpty,     # 允许「未发布」为空时发版
    # All = 公开版 + 内部版（有 internal\companies.txt 时）；Public = 只出开源公开版；Internal = 只出内部版
    [ValidateSet('All', 'Public', 'Internal')][string]$Edition = 'All'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$csproj = Join-Path $root 'src\PiaoMate.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'
$dist = Join-Path $root 'build\dist'
$backupDir = Join-Path $root 'build\config-backup'
$cfgName = 'PiaoMate.config'
$cfgNames = @('PiaoMate.config')
function Find-Cfg($dir) { foreach ($n in $cfgNames) { $c = Join-Path $dir $n; if (Test-Path $c) { return $c } }; return $null }
$utf8Bom = New-Object Text.UTF8Encoding($true)
$Unreleased = '## [未发布]'

# ---------- 更新日志 ----------
function Read-Log {
    if (-not (Test-Path $changelog)) { [IO.File]::WriteAllText($changelog, "# 更新日志`n`n$Unreleased`n", $utf8Bom) }
    (Get-Content $changelog -Raw -Encoding UTF8) -replace "`r`n", "`n"
}
function Save-Log($t) { [IO.File]::WriteAllText($changelog, ($t -replace "`n", "`r`n"), $utf8Bom) }

# 返回「未发布」小节的 [起点, 终点)（终点为下一个 "## " 或文末）
function Get-UnreleasedRange($t) {
    $s = $t.IndexOf($Unreleased)
    if ($s -lt 0) { return $null }
    $m = [regex]::Match($t.Substring($s + $Unreleased.Length), '(?m)^## ')
    $e = if ($m.Success) { $s + $Unreleased.Length + $m.Index } else { $t.Length }
    return @($s, $e)
}

function Add-LogNote([string]$text, [string]$kind) {
    $text = $text.Trim(); if (-not $text) { return }
    $t = Read-Log
    $r = Get-UnreleasedRange $t
    if (-not $r) {
        # 在第一个版本小节前插入「未发布」
        $m = [regex]::Match($t, '(?m)^## ')
        $ins = "$Unreleased`n`n"
        $t = if ($m.Success) { $t.Insert($m.Index, $ins) } else { $t.TrimEnd() + "`n`n$ins" }
        $r = Get-UnreleasedRange $t
    }
    $sec = $t.Substring($r[0], $r[1] - $r[0])
    $head = "### $kind"
    $h = $sec.IndexOf($head)
    if ($h -ge 0) {
        # 加到该分组最后一条之后
        $after = $h + $head.Length
        $m = [regex]::Match($sec.Substring($after), '(?m)^(###|## )')
        $end = if ($m.Success) { $after + $m.Index } else { $sec.Length }
        $sec = $sec.Substring(0, $end).TrimEnd() + "`n- $text`n`n" + $sec.Substring($end).TrimStart("`n")
    }
    else { $sec = $sec.TrimEnd() + "`n`n$head`n- $text`n`n" }
    Save-Log ($t.Substring(0, $r[0]) + $sec + $t.Substring($r[1]))
}

function Get-UnreleasedItems {
    $t = Read-Log; $r = Get-UnreleasedRange $t
    if (-not $r) { return @() }
    @($t.Substring($r[0], $r[1] - $r[0]) -split "`n" | Where-Object { $_ -match '^\s*[-*]\s+\S' })
}

if ($AddNote) { Add-LogNote $AddNote $Type; Write-Host "已记录到「未发布」（$Type）：$AddNote"; exit 0 }
if ($Changes) {
    $t = Read-Log; $r = Get-UnreleasedRange $t
    if ($r) { $t.Substring($r[0], $r[1] - $r[0]).TrimEnd() } else { '（没有「未发布」的改动）' }
    exit 0
}
if ($History) {
    [regex]::Matches((Read-Log), '(?m)^## \[(\d+\.\d+\.\d+)\](.*)$') | ForEach-Object { "v$($_.Groups[1].Value)$($_.Groups[2].Value)" }
    exit 0
}

# ---------- 0. 要构建的版本 ----------
$hasInternal = Test-Path (Join-Path $root 'internal\companies.txt')
$Editions = switch ($Edition) { 'Public' { @('Public') } 'Internal' { @('Internal') } default { if ($hasInternal) { @('Public', 'Internal') } else { @('Public') } } }
if ($Editions -contains 'Internal' -and -not $hasInternal) { throw '内部版需要 internal\companies.txt（每行：公司名称|税号），可参照 internal\companies.example.txt 创建' }
if ($Edition -eq 'All' -and -not $hasInternal) { Write-Host '未找到 internal\companies.txt，只生成公开版。' -ForegroundColor Yellow }
Write-Host "构建：$($Editions -join ' + ')"

# ---------- 1. 版本号 ----------
$xmlOrig = Get-Content $csproj -Raw -Encoding UTF8
$logOrig = Read-Log
$cur = [regex]::Match($xmlOrig, '<Version>([\d.]+)</Version>').Groups[1].Value
if (-not $cur) { throw '未在 csproj 中找到 <Version>' }
if ($Version) { $new = $Version }
elseif ($Rebuild) { $new = $cur }
else {
    $p = $cur.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) { 'major' { $p[0]++; $p[1] = 0; $p[2] = 0 } 'minor' { $p[1]++; $p[2] = 0 } default { $p[2]++ } }
    $new = $p -join '.'
}
if ($new -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 x.y.z：$new" }
$isNew = $new -ne $cur
if ($isNew -and $logOrig -match ('(?m)^## \[' + [regex]::Escape($new) + '\]')) { throw "更新日志里已有 v$new，请换一个版本号" }

foreach ($n in $Notes) { Add-LogNote $n '新增' }
if ($isNew) {
    $items = Get-UnreleasedItems
    if ($items.Count -eq 0 -and -not $AllowEmpty) {
        throw "「未发布」里还没有改动记录。请先用 -AddNote 或 -Notes 记录本次改动（或加 -AllowEmpty 强制发版）。"
    }
    Write-Host "本次改动（$($items.Count) 条）："; $items | ForEach-Object { Write-Host "  $_" }
}
Write-Host "版本号：$cur -> $new"

function Restore-Release {
    [IO.File]::WriteAllText($csproj, $xmlOrig, $utf8Bom)
    Save-Log $logOrig
    Write-Host '发布失败，已还原 csproj 版本号和更新日志。' -ForegroundColor Yellow
}

try {
    if ($isNew) {
        [IO.File]::WriteAllText($csproj, ($xmlOrig -replace '<Version>[\d.]+</Version>', "<Version>$new</Version>"), $utf8Bom)
        # 「未发布」→「版本号 - 日期」，并在上方留一个新的空「未发布」
        $t = Read-Log
        $t = $t.Replace($Unreleased, "$Unreleased`n`n## [$new] - $(Get-Date -Format 'yyyy-MM-dd')")
        Save-Log $t
    }

    # ---------- 2. 测试 ----------
    if (-not $SkipTests) {
        Write-Host '运行测试…'
        dotnet build tests\PiaoMate.Tests -v q --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '测试项目编译失败' }
        & (Join-Path $root 'tests\PiaoMate.Tests\bin\Debug\net48\PiaoMate.Tests.exe') | Select-Object -Last 3
        if ($LASTEXITCODE -ne 0) { throw '测试未通过，已中止发布' }
    }

    # ---------- 3. 编译（公开版 + 内部版） ----------
    $builds = @()
    foreach ($ed in $Editions) {
        $o = Join-Path ([IO.Path]::GetTempPath()) ('fpbuild_' + $ed + '_' + [guid]::NewGuid().ToString('N'))
        Write-Host "编译 Release（$ed）…"
        # 两个版本嵌入的资源不同，先清掉中间文件避免增量编译沿用上一个版本
        Remove-Item (Join-Path $root 'src\obj') -Recurse -Force -ErrorAction SilentlyContinue
        dotnet build src -c Release -o $o -v q --nologo "-p:Edition=$ed"
        if ($LASTEXITCODE -ne 0) { throw "编译失败（$ed）" }
        $builds += [pscustomobject]@{ Edition = $ed; Out = $o; Exe = (Join-Path $o 'PiaoMate.exe'); Name = $(if ($ed -eq 'Internal') { "PiaoMate-v$new-内部版" } else { "PiaoMate-v$new" }) }
    }
}
catch { if ($isNew) { Restore-Release }; throw }

# ---------- 4. 找到要继承的配置：优先最新的旧版本目录，其次备份目录 ----------
New-Item -ItemType Directory -Force $dist, $backupDir | Out-Null
function Get-VerKey($name) { $m = [regex]::Match($name, 'v(\d+)\.(\d+)\.(\d+)'); if ($m.Success) { [int]$m.Groups[1].Value * 1000000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value } else { -1 } }
$cfgSrc = $null
$newKey = Get-VerKey "v$new"
$oldDirs = Get-ChildItem $dist -Directory | Where-Object { (Get-VerKey $_.Name) -ge 0 -and (Get-VerKey $_.Name) -ne $newKey } | Sort-Object { Get-VerKey $_.Name } -Descending
foreach ($d in $oldDirs) { $c = Find-Cfg $d.FullName; if ($c) { $cfgSrc = $c; break } }
if (-not $cfgSrc) { $cfgSrc = Find-Cfg $backupDir }
# 同版本重新生成时，目录里已有的配置也要保留
$self = Get-ChildItem $dist -Directory | Where-Object { (Get-VerKey $_.Name) -eq $newKey } | ForEach-Object { Find-Cfg $_.FullName } | Where-Object { $_ } | Select-Object -First 1
if ($self) { Copy-Item $self (Join-Path $backupDir $cfgName) -Force; $cfgSrc = Join-Path $backupDir $cfgName }
elseif ($cfgSrc) {
    $bk = Join-Path $backupDir $cfgName
    if ([IO.Path]::GetFullPath($cfgSrc) -ne [IO.Path]::GetFullPath($bk)) { Copy-Item $cfgSrc $bk -Force }
    $cfgSrc = $bk
}

# ---------- 5. 发布目录 + zip（zip 里不含个人配置） ----------
$rels = @()
foreach ($bd in $builds) {
    $rel = Join-Path $dist $bd.Name
    $zip = Join-Path $dist "$($bd.Name).zip"
    if (Test-Path $rel) { Get-ChildItem $rel -Exclude $cfgNames | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $rel | Out-Null
    Copy-Item $bd.Exe $rel -Force
    $zipItems = @(Join-Path $rel 'PiaoMate.exe')
    foreach ($pair in @(@('docs\使用说明.md', '使用说明.md'), @('docs\快速使用手册.md', '快速使用手册.md'), @('CHANGELOG.md', '更新日志.md'), @('docs\免责声明.md', '免责声明.md'), @('LICENSE', 'LICENSE.txt'), @('THIRD-PARTY-NOTICES.txt', 'THIRD-PARTY-NOTICES.txt'))) {
        $srcDoc = Join-Path $root $pair[0]
        if (Test-Path $srcDoc) { $dst = Join-Path $rel $pair[1]; Copy-Item $srcDoc $dst -Force; $zipItems += $dst }
    }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Start-Sleep -Milliseconds 800
    for ($i = 0; $i -lt 5; $i++) { try { Compress-Archive -Path $zipItems -DestinationPath $zip -Force -ErrorAction Stop; break } catch { Start-Sleep 2; if ($i -eq 4) { throw } } }
    $rels += $rel; $rels += $zip
}
# 个人配置只放到自己用的版本（有内部版时放内部版）旁边
$own = Join-Path $dist ($builds | Sort-Object { $_.Edition -ne 'Internal' } | Select-Object -First 1).Name
if ($cfgSrc) { Copy-Item $cfgSrc (Join-Path $own $cfgName) -Force; Write-Host "已把配置带到新版本：$cfgSrc -> $own" }
else { Write-Host '没有找到旧配置（首次发布或尚未运行过软件），新版本将使用默认配置。' }

# ---------- 6. 清理旧版本（保留最近 $Keep 个版本号的公开版/内部版；被占用的删不掉就跳过；历史改动仍保留在更新日志中） ----------
$all = Get-ChildItem $dist -Directory | Where-Object { (Get-VerKey $_.Name) -ge 0 }
$keepKeys = $all | ForEach-Object { Get-VerKey $_.Name } | Sort-Object -Unique -Descending | Select-Object -First $Keep
foreach ($d in ($all | Where-Object { $keepKeys -notcontains (Get-VerKey $_.Name) })) {
    try { Remove-Item $d.FullName -Recurse -Force -ErrorAction Stop } catch { Write-Host "跳过(被占用)：$($d.Name)" }
    $z = "$($d.FullName).zip"; if (Test-Path $z) { Remove-Item $z -Force -ErrorAction SilentlyContinue }
}

# ---------- 7. 文档里的版本号 + 清理临时产物 ----------
$ag = Join-Path $root 'AGENTS.md'
if (Test-Path $ag) { $t = Get-Content $ag -Raw -Encoding UTF8; [IO.File]::WriteAllText($ag, ($t -replace '当前实现（v[\d.]+）', "当前实现（v$new）"), $utf8Bom) }
foreach ($bd in $builds) { Remove-Item $bd.Out -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $root 'src\obj') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n发布完成：$(($builds | ForEach-Object Name) -join '、')" -ForegroundColor Green
Get-ChildItem $rels | Select-Object FullName, Length | Format-Table -AutoSize
