<#
    一键启动 BuddyDeck Hub。

    做的事：
      1. 检查账号池在不在（不在就提示先跑 split-pools.ps1）
      2. 启动 Hub（它会自动把三个网关实例拉起来）
      3. 等实例就绪，打印可用信息

    用法：
        .\start-hub.ps1
        .\start-hub.ps1 -Stop        停掉 Hub（顺带停它拉起的实例）
        .\start-hub.ps1 -Status      只看状态
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Status,
    [string]$DataHome = (Join-Path $env:APPDATA "BuddyDeckHub")
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = $PSScriptRoot
$exe = Join-Path $root "bin\Release\net10.0\BuddyDeckHub.exe"
$vault = Join-Path $env:USERPROFILE ".workbuddy-api"

function Section($t) { Write-Host ""; Write-Host $t -ForegroundColor Cyan; Write-Host ("─" * 62) -ForegroundColor DarkGray }

function Get-HubProcesses {
    Get-CimInstance Win32_Process -Filter "Name='BuddyDeckHub.exe'" -EA SilentlyContinue |
        Where-Object { $_.ExecutablePath -like "*BuddyDeckHub\bin\Release*" }
}

# ────────────────────────────────────────────────────────────

if ($Stop) {
    Section "停止"
    foreach ($p in Get-HubProcesses) {
        Write-Host "  停 Hub PID $($p.ProcessId)"
        Stop-Process -Id $p.ProcessId -Force -EA SilentlyContinue
    }
    Start-Sleep 2

    # 停掉由 Hub 拉起来的网关实例（只停池的端口，绝对不碰 8787）
    $poolPorts = @(8788, 8789, 8790)
    foreach ($p in (Get-CimInstance Win32_Process -Filter "Name='node.exe'" -EA SilentlyContinue)) {
        $cmd = $p.CommandLine
        if (-not $cmd) { continue }
        foreach ($port in $poolPorts) {
            if ($cmd -match "--port\s+$port\b") {
                Write-Host "  停实例 PID $($p.ProcessId)（端口 $port）"
                Stop-Process -Id $p.ProcessId -Force -EA SilentlyContinue
            }
        }
    }
    Write-Host ""
    Write-Host "  已停止。8787（面板网关）没动。" -ForegroundColor Green
    return
}

if ($Status) {
    Section "进程"
    $hubs = @(Get-HubProcesses)
    if ($hubs.Count -eq 0) { Write-Host "  Hub 没在跑" -ForegroundColor Yellow }
    else { foreach ($p in $hubs) { Write-Host "  Hub PID $($p.ProcessId)" -ForegroundColor Green } }

    Write-Host ""
    Write-Host "  端口:" -ForegroundColor Cyan
    foreach ($port in @(8787, 8788, 8789, 8790, 8890)) {
        $line = netstat -ano | Select-String ":$port\s.*LISTENING"
        if ($line) {
            $owner = ($line[0] -split '\s+')[-1]
            $note = switch ($port) {
                8787 { "面板网关（别动）" }
                8890 { "Hub" }
                default { "账号池" }
            }
            Write-Host ("    {0}  占用 PID {1,-6} {2}" -f $port, $owner, $note) -ForegroundColor Green
        } else {
            Write-Host ("    {0}  空闲" -f $port) -ForegroundColor DarkGray
        }
    }

    if ($hubs.Count -gt 0) {
        Write-Host ""
        try {
            $h = Invoke-RestMethod "http://127.0.0.1:8890/healthz" -TimeoutSec 5
            Write-Host "  Hub: $($h.channels) 个渠道，$($h.members) 个成员，$($h.healthyChannels) 个健康" -ForegroundColor Green
        } catch { Write-Host "  Hub 没响应" -ForegroundColor Yellow }
    }
    return
}

# ────────────────────────────────────────────────────────────
Section "1/3  检查账号池"

$pools = @()
if (Test-Path (Join-Path $vault "pools")) {
    $pools = @(Get-ChildItem (Join-Path $vault "pools") -Directory)
}
if ($pools.Count -eq 0) {
    Write-Host "  还没建账号池。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  先跑这个（它会读你的账号保险库，生成几个独立的池）："
    Write-Host "      .\split-pools.ps1 -SplitSpec `"3,3,3`"" -ForegroundColor White
    Write-Host ""
    Write-Host "  或者你只想用一个池，可以跳过 —— Hub 会直接用 ~\.workbuddy-api" -ForegroundColor DarkGray
} else {
    foreach ($p in $pools) {
        $f = Join-Path $p.FullName "accounts.cn.json"
        if (Test-Path $f) {
            $v = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
            Write-Host ("  {0,-10} {1} 个号" -f $p.Name, $v.accounts.Count) -ForegroundColor Green
        }
    }
}

# ────────────────────────────────────────────────────────────
Section "2/3  启动 Hub"

if (-not (Test-Path $exe)) {
    Write-Host "  没编译过。先：" -ForegroundColor Yellow
    Write-Host "      dotnet build -c Release"
    return
}

$running = @(Get-HubProcesses)
if ($running.Count -gt 0) {
    Write-Host "  已经在跑了（PID $($running[0].ProcessId)）" -ForegroundColor DarkGray
} else {
    New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
    $env:BUDDYDECK_HUB_HOME = $DataHome
    Start-Process -FilePath $exe -WindowStyle Hidden
    Write-Host "  已启动，数据目录 $DataHome"
}

# ────────────────────────────────────────────────────────────
Section "3/3  等网关实例就绪"

$deadline = (Get-Date).AddSeconds(90)
$ready = @()
while ((Get-Date) -lt $deadline) {
    $ready = @()
    foreach ($port in @(8788, 8789, 8790)) {
        try {
            $r = Invoke-RestMethod "http://127.0.0.1:$port/healthz" -TimeoutSec 2
            if ($r.ok) { $ready += $port }
        } catch { }
    }
    if ($ready.Count -ge 3) { break }
    # 没配池的时候可能一个都没有，别死等
    if ($pools.Count -eq 0 -and $ready.Count -eq 0) {
        Start-Sleep 8
        break
    }
    Write-Host ("  就绪 {0}/3…" -f $ready.Count) -ForegroundColor DarkGray
    Start-Sleep 5
}

Write-Host ""
if ($ready.Count -gt 0) {
    Write-Host ("  网关实例就绪：{0}" -f ($ready -join ", ")) -ForegroundColor Green
} else {
    Write-Host "  没有池实例就绪 —— 去 Hub 的日志里看原因" -ForegroundColor Yellow
}

Write-Host ""
try {
    $cfgPath = Join-Path $DataHome "config.json"
    $admin = (Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json).AdminKey
    Write-Host "  Hub     http://127.0.0.1:8890" -ForegroundColor Green
    Write-Host "  管理密钥 $admin" -ForegroundColor Green
    Write-Host ""
    Write-Host "  接下来："
    Write-Host "      .\bd.ps1 status              看谁在用"
    Write-Host "      .\bd.ps1 member-add 张三 free 加成员"
    Write-Host "      .\bd.ps1 invite <成员id>     发 key"
} catch {
    Write-Host "  还没生成配置，等几秒再跑 .\start-hub.ps1 -Status" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  提醒：8787 是面板自己的网关，本脚本从不碰它。" -ForegroundColor DarkGray
