<#
    一键做出可以发给别人的安装包。

    做两步：
      1. build.ps1   编译 + 打包成免安装文件夹（publish\）
      2. ISCC.exe    把它压成单个安装程序（dist\BuddyDeck-Setup-<版本>.exe）

    用法：
        powershell -ExecutionPolicy Bypass -File make-installer.ps1
        powershell -ExecutionPolicy Bypass -File make-installer.ps1 -Version 1.0.1
        powershell -ExecutionPolicy Bypass -File make-installer.ps1 -SkipBuild   # 只重打安装包

    前置：Inno Setup 6（winget install JRSoftware.InnoSetup）
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$NodeVersion = "22.23.3",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "!!! $text" -ForegroundColor Red; exit 1 }

# ── 找 Inno Setup 的编译器 ─────────────────────────────────────
function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Fail "没找到 Inno Setup 的 ISCC.exe。装一个：`n    winget install JRSoftware.InnoSetup"
}
Write-Host "  ISCC: $iscc" -ForegroundColor DarkGray

# ── 1) 免安装文件夹 ───────────────────────────────────────────
if ($SkipBuild) {
    Step "1/2  跳过编译（-SkipBuild）"
    if (-not (Test-Path (Join-Path $root "publish.next\WorkBuddyPanel.exe"))) {
        Fail "publish.next 里没有 WorkBuddyPanel.exe，没法打安装包。去掉 -SkipBuild 先编译。"
    }
} else {
    Step "1/2  编译 + 打免安装文件夹"
    # 输出到 publish.next —— 不用碰正在运行的 publish\
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1") `
        -NodeVersion $NodeVersion -Output "publish.next"
    if ($LASTEXITCODE -ne 0) { Fail "build.ps1 失败" }
}

# ── 2) 安装程序 ───────────────────────────────────────────────
Step "2/2  生成安装程序（LZMA2 压缩，约 1 分钟）"

$iss = Join-Path $root "installer.iss"
if (-not (Test-Path $iss)) { Fail "找不到 installer.iss" }

# 版本号通过命令行覆盖，不用改文件
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC 编译失败" }

$out = Join-Path $root "dist\BuddyDeck-Setup-$Version.exe"
if (-not (Test-Path $out)) { Fail "编译报成功但找不到产物：$out" }

$zip = [math]::Round((Get-Item $out).Length / 1MB, 1)
$folder = [math]::Round(((Get-ChildItem (Join-Path $root "publish.next") -Recurse -File |
    Measure-Object -Property Length -Sum).Sum) / 1MB, 1)

Write-Host ""
Write-Host "完成" -ForegroundColor Green
Write-Host ("  免安装文件夹 : {0,7:N1} MB" -f $folder)
Write-Host ("  安装程序     : {0,7:N1} MB   {1}" -f $zip, $out) -ForegroundColor Green
Write-Host ""
Write-Host "发给别人就发这一个 exe。对方双击、下一步、完事 —— 不需要装 .NET，也不需要装 Node。" -ForegroundColor Green
