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
    [switch]$SkipBuild,
    # 从哪个目录取料。默认 publish.next（普通版），
    # 出授权版传 -Source publish.licensed。
    [string]$Source = "publish.next",
    # 产物文件名后缀。不传就按 Source 猜（licensed -> -Licensed，其它为空）。
    # 想从 publish.licensed 出「不带后缀」的正式发布版就传 -Suffix ""。
    [string]$Suffix = "__auto__"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "!!! $text" -ForegroundColor Red; exit 1 }

# 版本号统一在这里清洗一次：容忍手抖传进来的 v1.0.0，但拦下真正不合法的值。
# 不清洗的话，产物会叫 BuddyDeck-Setup-v1.0.0.exe，而 workflow 里
# upload-artifact / gh-release 找的是 BuddyDeck-Setup-*.exe —— 通配符能兜住，
# 但安装程序里显示的版本会带个 v，Windows「应用和功能」里也不好看。
$Version = $Version.Trim() -replace '^[vV]', ''
if ($Version -notmatch '^\d+(\.\d+){1,3}') {
    Fail "版本号不合法：'$Version'。用 1.0.0 这样的形式（不要带 v 前缀）。"
}

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
    if (-not (Test-Path (Join-Path $root (Join-Path $Source "WorkBuddyPanel.exe")))) {
        Fail "$Source 里没有 WorkBuddyPanel.exe，没法打安装包。去掉 -SkipBuild 先编译。"
    }
} else {
    Step "1/2  编译 + 打免安装文件夹"
    # 输出到 publish.next —— 不用碰正在运行的 publish\
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1") `
        -NodeVersion $NodeVersion -Output $Source
    if ($LASTEXITCODE -ne 0) { Fail "build.ps1 失败" }
}

# ── 2) 安装程序 ───────────────────────────────────────────────
Step "2/2  生成安装程序（LZMA2 压缩，约 1 分钟）"

$iss = Join-Path $root "installer.iss"
if (-not (Test-Path $iss)) { Fail "找不到 installer.iss" }

# dist 是被 .gitignore 排除的，干净的检出（比如 CI）里根本没有这个目录。
# ISCC 不会自己建 OutputDir，缺了会直接报错退出 —— 这里先建好。
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 产物名区分版本：授权版加 -Licensed 后缀。
# 这样普通版和授权版可以各自打包，不会互相覆盖，也不会认错文件。
$suffix = if ($Suffix -ne "__auto__") {
    $Suffix
} elseif ($Source -like "*licensed*") {
    "-Licensed"
} else {
    ""
}

# 版本号 + 源目录 + 产物后缀，都通过命令行覆盖，不用改文件
& $iscc "/DAppVersion=$Version" "/DSourceDir=$Source" "/DOutputSuffix=$suffix" $iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC 编译失败" }

$out = Join-Path $dist "BuddyDeck-Setup-$Version$suffix.exe"
if (-not (Test-Path $out)) { Fail "编译报成功但找不到产物：$out" }

$zip = [math]::Round((Get-Item $out).Length / 1MB, 1)
$folder = [math]::Round(((Get-ChildItem (Join-Path $root $Source) -Recurse -File |
    Measure-Object -Property Length -Sum).Sum) / 1MB, 1)

Write-Host ""
Write-Host "完成" -ForegroundColor Green
Write-Host ("  免安装文件夹 : {0,7:N1} MB" -f $folder)
Write-Host ("  安装程序     : {0,7:N1} MB   {1}" -f $zip, $out) -ForegroundColor Green
Write-Host ""
Write-Host "发给别人就发这一个 exe。对方双击、下一步、完事 —— 不需要装 .NET，也不需要装 Node。" -ForegroundColor Green
