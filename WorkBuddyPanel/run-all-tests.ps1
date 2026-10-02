<#
    跑全部测试。改完代码跑这一个就够了。

    用法：
        cd WorkBuddyPanel
        powershell -ExecutionPolicy Bypass -File run-all-tests.ps1

        # 跳过界面测试（那步要起程序、慢，而且要有桌面会话）
        powershell -ExecutionPolicy Bypass -File run-all-tests.ps1 -SkipUi

    依次跑：
      ① 编译
      ② XAML 静态检查（绑定 + 资源引用）
      ③ 纯逻辑单元测试（PoolStore / VaultMover）
      ③b 旧网关替换（会起/停测试网关，不碰 8787）
      ③c 异常处理器防递归（防闪退）
      ④ 分池隔离验证
      ⑤ 界面点击测试（8 个标签页）

    退出码 0 = 全过。

    ── 两个踩过的坑 ────────────────────────────────────────

    ⚠ 路径字符串一律用正斜杠。
      PowerShell 里 "bin\Release\net8..." 的 \n 是**换行符** ——
      路径会断成两截，然后永远找不到文件。本文件里出现过这个错。

    ⚠ 不要在判断退出码的地方接管道。
      & dotnet build ... | Select-String   ← $LASTEXITCODE 变成 Select-String 的，
      结果失败的任务也报成功。实测假报过。
#>
[CmdletBinding()]
param(
    [switch]$SkipUi,
    [int]$StartupWait = 34
)

$ErrorActionPreference = "Continue"
$here = $PSScriptRoot

$results = @()
$failed = 0

function Write-Head([string]$text) {
    Write-Host ""
    Write-Host ("=" * 64) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ("=" * 64) -ForegroundColor DarkCyan
}

function Add-Result([string]$name, [bool]$isOk) {
    $script:results += [pscustomobject]@{ Name = $name; Ok = $isOk }
    if (-not $isOk) { $script:failed++ }
}

# ── ① 编译 ────────────────────────────────────────────────────
Write-Head "编译（Release）"

$buildOut = & dotnet build (Join-Path $here "WorkBuddyPanel.csproj") -c Release 2>&1
$buildCode = $LASTEXITCODE
$buildOut | Where-Object { $_ -match "error|已成功|Build succeeded" } | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
Add-Result "编译（Release）" ($buildCode -eq 0)

# ── ② XAML 静态检查 ───────────────────────────────────────────
Write-Head "XAML 静态检查"

$xamlOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "check-xaml.ps1") 2>&1
$xamlCode = $LASTEXITCODE
$xamlOut | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
Add-Result "XAML 静态检查" ($xamlCode -eq 0)

# ── ②b 池路由检查 ────────────────────────────────────────────
#
# 这条专门防「改账号的命令忘了带池」——
# 那样会静默地改到默认池，界面不报错、只是改错了地方。
# 实测踩到两次（添加账号、设为使用中/删除/重命名）。
$routeScript = Join-Path $here "check-pool-routing.ps1"
if (Test-Path $routeScript) {
    Write-Head "池路由检查"
    $routeOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $routeScript 2>&1
    $routeCode = $LASTEXITCODE
    $routeOut | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    Add-Result "池路由检查" ($routeCode -eq 0)
}

# ── ③ 纯逻辑单元测试 ──────────────────────────────────────────
$logicProj = Join-Path $here "tests/PoolLogic"
if (Test-Path $logicProj) {
    Write-Head "纯逻辑单元测试（PoolStore / VaultMover）"

    Push-Location $logicProj
    try {
        $logicOut = & dotnet run -c Release 2>&1
        $logicCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $logicOut |
        Where-Object { $_ -notmatch "warning CA1416" } |
        Select-Object -Last 8 |
        ForEach-Object { Write-Host "  $_" }
    Add-Result "纯逻辑单元测试" ($logicCode -eq 0)
}

# ── ③b 进程清理（旧网关替换）──────────────────────────────────
#
# 这一段测的是**会杀进程**的代码，所以单独列出来。
# 它自己搭场景（拷一份源码加标记、起一个旧网关）、自己收尾。
# ⚠ 它只对 8787 做只读探测 —— 那是 AI 助手的命脉，绝不碰。
$reaperProj = Join-Path $here "tests/ProcessReaper"
if (Test-Path $reaperProj) {
    Write-Head "旧网关替换（ProcessReaper）"

    Push-Location $reaperProj
    try {
        $reaperOut = & dotnet run -c Release 2>&1
        $reaperCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $reaperOut |
        Where-Object { $_ -notmatch "warning" } |
        Select-Object -Last 6 |
        ForEach-Object { Write-Host "  $_" }
    Add-Result "旧网关替换" ($reaperCode -eq 0)
}

# ── ③c 异常处理器防递归 ────────────────────────────────────────
#
# 测的是「弹窗重入消息循环导致栈溢出」那个闪退。
$guardProj = Join-Path $here "tests/ExceptionGuard"
if (Test-Path $guardProj) {
    Write-Head "异常处理器防递归（ExceptionGuard）"

    Push-Location $guardProj
    try {
        $guardOut = & dotnet run -c Release 2>&1
        $guardCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $guardOut |
        Where-Object { $_ -notmatch "warning" } |
        Select-Object -Last 5 |
        ForEach-Object { Write-Host "  $_" }
    Add-Result "异常处理器防递归" ($guardCode -eq 0)
}

# ── ④ 分池隔离验证 ────────────────────────────────────────────
$verify = Join-Path $here "verify-pools.py"
if (Test-Path $verify) {
    Write-Head "分池隔离验证"

    $env:PYTHONIOENCODING = "utf-8"
    $verifyOut = & python $verify 2>&1
    $verifyCode = $LASTEXITCODE
    $verifyOut | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }
    Add-Result "分池隔离验证" ($verifyCode -eq 0)
}

# ── ⑤ 界面点击测试 ────────────────────────────────────────────
if (-not $SkipUi) {
    Write-Head "界面点击测试"

    # 关键字是 Release 编译的 —— 发布版会校验同目录下的 .lic，
    # 没有就弹激活窗、主界面不出现，于是每个标签页都找不到（实测踩过）。
    # 所以先补一份授权文件；实在没有就跳过（不算失败）。
    $exe = Join-Path $here "bin/Release/net8.0-windows/WorkBuddyPanel.exe"
    $exeDir = Split-Path $exe -Parent
    $licTarget = Join-Path $exeDir "license.lic"

    if ((Test-Path $exeDir) -and -not (Test-Path $licTarget)) {
        foreach ($candidate in @(
            "D:/BuddyDeck/license.lic",
            (Join-Path $env:USERPROFILE ".workbuddy-license/license.lic")
        )) {
            if (Test-Path $candidate) {
                Copy-Item $candidate $licTarget -Force
                Write-Host "  补了授权文件（来自 $candidate）" -ForegroundColor DarkGray
                break
            }
        }
    }

    if (-not (Test-Path $licTarget)) {
        Write-Host "  跳过：没有 license.lic，发布版会弹激活窗挡住主界面。" -ForegroundColor Yellow
        Write-Host "  想跑的话，把一份 .lic 拷到：$exeDir" -ForegroundColor Yellow
        Add-Result "界面点击测试（跳过：无授权文件）" $true
    } elseif (-not (Test-Path $exe)) {
        Write-Host "  跳过：还没编译出 $exe" -ForegroundColor Yellow
        Add-Result "界面点击测试（跳过：无 exe）" $true
    } else {
        # ── 备份配置（要在**任何**操作之前）────────────────────
        #
        # 界面测试会真的点按钮 —— 其中「设置」页的端口输入框、
        # 「换一个空闲的端口」之类会直接写 %APPDATA%\WorkBuddyPanel\settings.json
        # 和 pools.json。
        #
        # 不备一份的话，跑完测试用户的端口就变了。实测踩过两次：
        # 一次 8787→8789，一次两个池的端口被整体挪了一格。
        #
        # 备份放在最前面（而不是界面测试那一支里）——
        # 因为后面任何一步失败都不该导致「改了配置但没恢复」。
        $cfgDir = Join-Path $env:APPDATA "WorkBuddyPanel"
        $backups = @{}
        foreach ($f in @("settings.json", "pools.json")) {
            $src = Join-Path $cfgDir $f
            if (Test-Path $src) {
                $bak = "$src.uitest-backup"
                Copy-Item $src $bak -Force
                $backups[$src] = $bak
            }
        }
        if ($backups.Count -gt 0) {
            Write-Host "  备份了 $($backups.Count) 个配置文件（测完恢复）" -ForegroundColor DarkGray
        }

        # 先关掉**我自己**的开发实例，免得撞单实例锁。
        #
        # ⚠ 只杀 bin\Release 那个 —— 那是本脚本编译出来的。
        #   用户可能在跑 D:\BuddyDeck\ 或 publish.new\ 里的实例，
        #   而且网关是那种实例的子进程 —— 杀了它，**助手自己的模型来源就断了**。
        #   实测误杀过一次，助手当场失联。
        # 只停「本脚本编译出来那个 exe」的实例。
        #
        # 用户的实例在 D:\BuddyDeck 或 publish.new —— 那些不能碰，
        # 因为网关是它们的子进程，杀了助手自己的模型来源就断了。
        Get-CimInstance Win32_Process -Filter "Name='WorkBuddyPanel.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -eq $exe } |
            ForEach-Object {
                Write-Host "  停掉测试实例 PID $($_.ProcessId)" -ForegroundColor DarkGray
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
        Start-Sleep 3

        Start-Process $exe
        Write-Host "  等程序起来（$StartupWait 秒）…" -ForegroundColor DarkGray
        Start-Sleep $StartupWait

        # 找**刚才启动的这个**实例。
        #
        # 不写死 "*bin*Release*" —— putput 路径会变（bin\Release、
        # publish.licensed、_smoke3…）。写死的话，在别的路径上跑时
        # $proc 是 null，后面的「恢复配置」就不会执行 ——
        # 实测就是这么把用户端口改丢的。
        #
        # 改成「exe 路径等于本脚本编译出来那个」。
        $proc = Get-CimInstance Win32_Process -Filter "Name='WorkBuddyPanel.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath -eq $exe } |
                Select-Object -First 1

        if ($proc) {
            $uiOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "run-ui-clicktest-all.ps1") -ProcessId $proc.ProcessId 2>&1
            $uiCode = $LASTEXITCODE
            $uiOut | Select-Object -Last 14 | ForEach-Object { Write-Host "  $_" }
            Add-Result "界面点击测试" ($uiCode -eq 0)

            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            Start-Sleep 2

            # 恢复配置 —— 测试点过的按钮可能改了它
            foreach ($pair in $backups.GetEnumerator()) {
                try {
                    Copy-Item $pair.Value $pair.Key -Force
                    Remove-Item $pair.Value -Force -ErrorAction SilentlyContinue
                } catch {
                    Write-Host "  恢复 $($pair.Key) 失败：$($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
            if ($backups.Count -gt 0) {
                Write-Host "  已恢复配置文件" -ForegroundColor DarkGray
            }
        } else {
            Write-Host "  程序没起来" -ForegroundColor Red
            Add-Result "界面点击测试" $false
        }

        # ── 恢复配置（不管上面成功失败都要跑）──────────────────
        foreach ($pair in $backups.GetEnumerator()) {
            try {
                Copy-Item $pair.Value $pair.Key -Force
                Remove-Item $pair.Value -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Host "  ⚠ 恢复 $($pair.Key) 失败：$($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
        if ($backups.Count -gt 0) {
            Write-Host "  已恢复配置文件" -ForegroundColor DarkGray
        }
    }
}

# ── 汇总 ──────────────────────────────────────────────────────
Write-Head "汇总"
foreach ($r in $results) {
    $mark = if ($r.Ok) { "OK  " } else { "FAIL" }
    $color = if ($r.Ok) { "Green" } else { "Red" }
    Write-Host "  $mark  $($r.Name)" -ForegroundColor $color
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "全部通过" -ForegroundColor Green
    exit 0
}
Write-Host "$failed 项有问题" -ForegroundColor Red
exit 1
