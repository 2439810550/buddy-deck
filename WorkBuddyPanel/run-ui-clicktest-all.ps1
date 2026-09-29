<#
    一把梭：把每个标签页的按钮都点一遍。

    用法：
        powershell -ExecutionPolicy Bypass -File run-ui-clicktest-all.ps1 -ProcessId 1234
#>
[CmdletBinding()]
param(
    [int]$ProcessId = 0,
    [int64]$Hwnd = 0
)

$ErrorActionPreference = "Continue"
$here = $PSScriptRoot

if ($ProcessId -eq 0 -and $Hwnd -eq 0) {
    Write-Host "要给 -ProcessId 或 -Hwnd" -ForegroundColor Red
    exit 1
}

# 注意：「对话」页有个发送按钮，点了会真发请求（花积分）；
# 「设置」页有保存按钮，点了会写配置。都不算破坏性，但会留下痕迹。
$tabs = @("首页", "一键接入", "账号", "分池", "对话", "模型", "设置", "日志")

$totalFail = 0
$results = @()

foreach ($tab in $tabs) {
    Write-Host ""
    Write-Host ("█" * 60) -ForegroundColor DarkCyan
    Write-Host "  标签页：$tab" -ForegroundColor Cyan
    Write-Host ("█" * 60) -ForegroundColor DarkCyan

    $argList = @("-ProcessId", $ProcessId, "-Tab", $tab)
    if ($Hwnd -ne 0) { $argList = @("-Hwnd", $Hwnd, "-Tab", $tab) }

    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "ui-clicktest.ps1") @argList 2>&1
    $code = $LASTEXITCODE

    $out | ForEach-Object { Write-Host "  $_" }

    if ($code -ne 0) { $totalFail++ }
    $results += [pscustomobject]@{ Tab = $tab; Ok = ($code -eq 0) }
}

Write-Host ""
Write-Host ("█" * 60) -ForegroundColor DarkCyan
Write-Host "  汇总" -ForegroundColor Cyan
Write-Host ("█" * 60) -ForegroundColor DarkCyan

foreach ($r in $results) {
    $mark = if ($r.Ok) { "OK  " } else { "FAIL" }
    $color = if ($r.Ok) { "Green" } else { "Red" }
    Write-Host "  $mark  $($r.Tab)" -ForegroundColor $color
}

Write-Host ""
if ($totalFail -eq 0) {
    Write-Host "全部通过" -ForegroundColor Green
    exit 0
}
Write-Host "$totalFail 个标签页有问题" -ForegroundColor Red
exit 1
