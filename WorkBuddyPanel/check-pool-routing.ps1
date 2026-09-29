<#
    检查：所有「作用于某个池」的 node 命令，是不是都带了池路由。

    ── 为什么需要这个检查 ──────────────────────────────────────

    分池靠的是给子进程设 WORKBUDDY_API_HOME 环境变量，node 那边
    于是读另一份账号库。而这个「设环境变量」只发生在 RunPoolCliAsync 里。

    所以任何一条**改账号**的命令，只要没走池路由，就会静默地作用在
    **默认池**上。实测踩到两次：

      · 「添加账号」加到默认池去了（用户报的）
      · 「设为使用中 / 删除 / 重命名」也全打在默认池上

    这类 bug 特别难发现：不报错、界面看着正常，只是改错了地方。

    ── 判定规则 ────────────────────────────────────────────────

    池感知的包装（走这些 = 安全）：
        RunPoolCliAsync      直接带 WORKBUDDY_API_HOME
        RunAccountCommand    内部调 RunPoolCliAsync

    不感知池的（走这些 + 池相关命令 = bug）：
        RunCliAsync
        CliRunner.RunAsync

    还认这两种写法：
        · 命令先赋给变量，后面才传进包装 —— 会顺着变量名找
        · 明确标注「// pool: exempt」的——故意的例外（比如 --maintain）

    用法：
        powershell -ExecutionPolicy Bypass -File check-pool-routing.ps1
        powershell -ExecutionPolicy Bypass -File check-pool-routing.ps1 -Verbose

    退出码 0 = 全带池路由，1 = 有漏的。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 改账号的命令 —— 必须带池
$poolScoped = @(
    'accounts add', 'accounts use', 'accounts clear', 'accounts remove',
    'accounts rename', 'accounts renew', 'accounts import', 'accounts export',
    'accounts --json', 'accounts --no-checkin', 'checkin'
)

# 池感知的包装函数名
$poolAware = @('RunPoolCliAsync', 'RunAccountCommand')

# 不感知池的
$notPoolAware = @('RunCliAsync', 'CliRunner.RunAsync')

$problems = New-Object System.Collections.Generic.List[string]
$exempt = New-Object System.Collections.Generic.List[string]
$okCount = 0

$files = Get-ChildItem -Path (Join-Path $root "ViewModels") -Filter "*.cs" -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

foreach ($file in $files) {
    $rel = $file.FullName.Substring($root.Length).TrimStart('\')
    $lines = Get-Content $file.FullName -Encoding UTF8

    # ── 第一遍：找出哪些变量装的是池相关命令 ──
    # 例如： var args = $"src/bin.js accounts add{...}";
    $poolVars = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], 'var\s+(\w+)\s*=\s*\$?"src/bin\.js\s+([^"$]*)')
        if ($m.Success) {
            $varName = $m.Groups[1].Value
            $cmd = $m.Groups[2].Value.Trim()
            foreach ($p in $poolScoped) {
                if ($cmd -like "$p*") { $poolVars[$varName] = $cmd; break }
            }
        }
    }

    # ── 第二遍：检查每个调用点 ──
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $where = "$rel`:$($i + 1)"

        # 找所有 "xxx.RunAsync(...)" 或 "xxx(...)" 形式的调用
        foreach ($wrapper in $notPoolAware) {
            if ($line -notmatch [regex]::Escape($wrapper) + '\s*\(') { continue }

            # 看这行前后有没有池相关命令（字面量或变量）
            $window = ($lines[[Math]::Max(0, $i - 5)..[Math]::Min($lines.Count - 1, $i + 1)] -join "`n")

            # 是不是故意的例外
            if ($window -match '//\s*pool:\s*exempt') {
                $exempt.Add("$where  $wrapper（标注了 pool: exempt）")
                continue
            }

            # 字面量命令
            $hit = $null
            foreach ($p in $poolScoped) {
                if ($window -match [regex]::Escape("src/bin.js $p")) { $hit = $p; break }
            }

            # 变量形式的命令
            if (-not $hit) {
                foreach ($v in $poolVars.Keys) {
                    if ($window -match [regex]::Escape($v) + '\s*\)') { $hit = "$v（= $($poolVars[$v])）"; break }
                }
            }

            if ($hit) {
                $problems.Add("$where  「$hit」走 $wrapper → 会作用在默认池上")
            } else {
                $okCount++
            }
        }
    }
}

Write-Host "扫描 $($files.Count) 个文件" -ForegroundColor DarkGray
if ($okCount -gt 0) { Write-Host "  $okCount 处调用不带池命令（正常）" -ForegroundColor DarkGray }
if ($exempt.Count -gt 0) {
    Write-Host "  $($exempt.Count) 处标注为例外：" -ForegroundColor DarkGray
    foreach ($e in $exempt) { Write-Host "    $e" -ForegroundColor DarkGray }
}
Write-Host ""

if ($problems.Count -eq 0) {
    Write-Host "OK — 所有池相关命令都带了池路由" -ForegroundColor Green
    exit 0
}

Write-Host "发现 $($problems.Count) 处问题：" -ForegroundColor Red
foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Yellow }
Write-Host ""
Write-Host "修法：把 RunCliAsync 换成 RunPoolCliAsync（或者用 RunAccountCommand）。" -ForegroundColor Cyan
Write-Host "确实是故意的（比如计划任务的维护流程），加一行注释：// pool: exempt" -ForegroundColor Cyan
exit 1
