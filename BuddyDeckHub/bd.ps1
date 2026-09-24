<#
    BuddyDeck Hub 的日常操作。

    管理接口是用 curl 设计的，但 curl 有个坑：中文请求体在 PowerShell / git bash 里
    会被编码搞坏（实测报 400）。这个脚本内部统一走 .NET 的 HTTP 客户端 + UTF-8，
    所以中文名字、备注都不会出问题。

    用法：
        .\bd.ps1 status                        看谁在用、渠道健不健康
        .\bd.ps1 calls                         最近调用流水
        .\bd.ps1 channel-add <名字> <地址> <key>
        .\bd.ps1 channel-list
        .\bd.ps1 channel-probe <渠道id>
        .\bd.ps1 member-add <名字> [分组] [日配额]
        .\bd.ps1 member-list
        .\bd.ps1 member-disable <成员id>
        .\bd.ps1 member-enable <成员id>
        .\bd.ps1 invite <成员id>               发一把新 key（明文只显示这一次）
        .\bd.ps1 keys <成员id>                 看这个人的所有 key
        .\bd.ps1 key-revoke <tokenId>
        .\bd.ps1 reset-usage <成员id>
        .\bd.ps1 groups                        看分组和倍率表
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Action = "status",
    [Parameter(Position = 1)][string]$A1 = "",
    [Parameter(Position = 2)][string]$A2 = "",
    [Parameter(Position = 3)][string]$A3 = "",
    [string]$Hub = "http://127.0.0.1:8890",
    [string]$KeyFile = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ── 找管理密钥 ──────────────────────────────────────────────

# 从一个文件里读出管理密钥。
# 两种都认：纯文本的 key 文件，或者 Hub 的 config.json（取里面的 AdminKey）。
#
# 读文件必须显式指定 -Encoding UTF8。
# PowerShell 5.1 默认按 ANSI 读，而 config.json 是 UTF-8，
# 里面中文会变成乱码、JSON 直接解析失败（实测踩过）。
function Read-KeyFromFile([string]$path) {
    $raw = (Get-Content $path -Raw -Encoding UTF8).Trim()
    if ($raw.StartsWith("{")) {
        try {
            $cfg = $raw | ConvertFrom-Json
            if ($cfg.AdminKey) { return ([string]$cfg.AdminKey).Trim() }
        } catch { }
    }
    return $raw
}

function Get-AdminKey {
    if ($KeyFile) {
        if (-not (Test-Path $KeyFile)) { throw "找不到密钥文件：$KeyFile" }
        return Read-KeyFromFile $KeyFile
    }

    # 默认位置：和 Hub 用同一套规则找数据目录
    $candidates = @()
    if ($env:BUDDYDECK_HUB_HOME) { $candidates += (Join-Path $env:BUDDYDECK_HUB_HOME "config.json") }
    $candidates += (Join-Path $env:APPDATA "BuddyDeckHub\config.json")
    $candidates += (Join-Path $PSScriptRoot ".testdata\config.json")

    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $k = Read-KeyFromFile $c
            if ($k) { return $k }
        }
    }
    throw "找不到管理密钥。看看 %APPDATA%\BuddyDeckHub\config.json 里的 adminKey 字段。"
}

$script:AdminKey = $null

# ── HTTP 封装。全部走 UTF-8，绕开 shell 的编码坑 ──────────────
function Invoke-Hub {
    param(
        [string]$Method = "GET",
        [string]$Path,
        $Body = $null
    )
    if (-not $script:AdminKey) { $script:AdminKey = Get-AdminKey }

    $headers = @{ "X-Admin-Key" = $script:AdminKey }
    $args = @{
        Uri         = "$Hub$Path"
        Method      = $Method
        Headers     = $headers
        ContentType = "application/json; charset=utf-8"
    }

    if ($Body -ne $null) {
        # 关键：显式转 UTF-8 字节，不要交给 PowerShell 的默认编码
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 6 }
        $args["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
    }

    $resp = Invoke-RestMethod @args
    return $resp
}

function Show-Json($obj) {
    $obj | ConvertTo-Json -Depth 6
}

# ── 输出辅助 ────────────────────────────────────────────────
function Section($t) { Write-Host ""; Write-Host $t -ForegroundColor Cyan; Write-Host ("─" * 60) -ForegroundColor DarkGray }

switch ($Action.ToLower()) {

    "status" {
        $d = Invoke-Hub -Path "/admin/overview"
        Section "今天：$($d.day)"

        Section "成员"
        if ($d.members.Count -eq 0) { Write-Host "  （还没有成员）" -ForegroundColor DarkGray }
        foreach ($m in $d.members) {
            $pct = if ($m.dailyQuota -gt 0) { [math]::Round($m.usedToday / $m.dailyQuota * 100, 1) } else { 0 }
            $bar = if ($pct -gt 90) { "🔴" } elseif ($pct -gt 60) { "🟡" } else { "🟢" }
            $state = if ($m.enabled) { "" } else { "  [已停用]" }
            Write-Host ("  {0} {1,-10} {2,-8} {3,10:N0} / {4,-10:N0} ({5,5}%)  {6,5} 次  {7,12:N0} tokens{8}" -f `
                $bar, $m.name, $m.groupName, $m.usedToday, $m.dailyQuota, $pct, $m.callsToday, $m.tokensToday, $state)
            foreach ($t in $m.tokens) {
                $kstate = if ($t.enabled) { "" } else { " (停用)" }
                $last = if ($t.lastUsedAt) { $t.lastUsedAt } else { "还没用过" }
                Write-Host ("      key {0}  {1}  最后使用 {2}{3}" -f `
                    $t.prefix, $t.name, $last, $kstate) -ForegroundColor DarkGray
            }
        }

        Section "渠道"
        foreach ($c in $d.channels) {
            $icon = if ($c.available) { "🟢" } else { "🔴" }
            Write-Host ("  {0} {1,-6} {2,-18} 在途={3} 连续失败={4}" -f `
                $icon, $c.id, $c.name, $c.inFlight, $c.consecutiveFailures)
            if ($c.lastError) { Write-Host "        最后错误：$($c.lastError)" -ForegroundColor DarkYellow }
            if ($c.disabledUntil) { Write-Host "        冷却到：$($c.disabledUntil)" -ForegroundColor DarkYellow }
        }
    }

    "calls" {
        $n = if ($A1) { [int]$A1 } else { 30 }
        $rows = Invoke-Hub -Path "/admin/calls?limit=$n"
        Section "最近 $n 条调用"
        Write-Host ("  {0,-14} {1,-10} {2,-22} {3,7} {4,7} {5,9} {6,6}" -f `
            "时间", "成员", "模型", "输入", "输出", "配额", "状态")
        Write-Host ("  " + ("─" * 82)) -ForegroundColor DarkGray
        foreach ($r in $rows) {
            $color = if ($r.status -ge 400) { "DarkYellow" } else { "Gray" }
            Write-Host ("  {0,-14} {1,-10} {2,-22} {3,7:N0} {4,7:N0} {5,9:N0} {6,6}" -f `
                $r.time, $r.member, $r.model, $r.promptTokens, $r.completionTokens, $r.quota, $r.status) -ForegroundColor $color
        }
    }

    "channel-add" {
        if (-not $A1) { throw "用法：bd.ps1 channel-add <名字> <地址> [key]" }
        $name = $A1
        $url = if ($A2) { $A2 } else { "http://127.0.0.1:8787" }
        $apiKey = if ($A3) { $A3 } else { "" }

        $id = "ch-" + (Get-Random -Minimum 1000 -Maximum 9999)
        $body = @{ id = $id; name = $name; baseUrl = $url; apiKey = $apiKey; priority = 10 }
        $r = Invoke-Hub -Method POST -Path "/admin/channels" -Body $body
        Write-Host "  ✓ 渠道已加：$($r.id)" -ForegroundColor Green

        # 顺手探一下
        $probe = Invoke-Hub -Method POST -Path "/admin/channels/$id/probe"
        if ($probe.ok) {
            Write-Host "  ✓ 探测通过，$($probe.models) 个模型" -ForegroundColor Green
        } else {
            $msg = if ($probe.error) { $probe.error } else { $probe.body }
            Write-Host "  ✗ 探测失败：$msg" -ForegroundColor Yellow
        }
    }

    "channel-list" {
        $d = Invoke-Hub -Path "/admin/overview"
        Section "渠道"
        foreach ($c in $d.channels) { Show-Json $c }
    }

    "channel-probe" {
        if (-not $A1) { throw "用法：bd.ps1 channel-probe <渠道id>" }
        $r = Invoke-Hub -Method POST -Path "/admin/channels/$A1/probe"
        if ($r.ok) {
            Write-Host "  ✓ 通过，$($r.models) 个模型" -ForegroundColor Green
        } else {
            $msg = if ($r.error) { $r.error } else { $r.body }
            Write-Host "  ✗ 失败：$msg" -ForegroundColor Yellow
        }
    }

    "member-add" {
        if (-not $A1) { throw "用法：bd.ps1 member-add <名字> [分组] [日配额]" }
        $body = @{
            name       = $A1
            group      = $(if ($A2) { $A2 } else { "free" })
            dailyQuota = $(if ($A3) { [long]$A3 } else { 5000000 })
        }
        $r = Invoke-Hub -Method POST -Path "/admin/members" -Body $body
        Write-Host "  ✓ 成员已加：$($r.id)  $($r.name)" -ForegroundColor Green
        Write-Host ""
        Write-Host "  现在给他发一把 key：" -ForegroundColor Yellow
        Write-Host "      .\bd.ps1 invite $($r.id)"
    }

    "member-list" {
        $d = Invoke-Hub -Path "/admin/overview"
        Section "成员"
        foreach ($m in $d.members) { Show-Json $m }
    }

    { $_ -in "member-disable", "member-enable" } {
        if (-not $A1) { throw "用法：bd.ps1 $Action <成员id>" }
        $on = $Action -eq "member-enable"
        Invoke-Hub -Method PATCH -Path "/admin/members/$A1" -Body @{ enabled = $on } | Out-Null
        if ($on) { Write-Host "  ✓ 已启用 $A1" -ForegroundColor Green }
        else { Write-Host "  ✓ 已停用 $A1（他的 key 立刻失效）" -ForegroundColor Yellow }
    }

    "invite" {
        if (-not $A1) { throw "用法：bd.ps1 invite <成员id> [key名字]" }
        $body = @{ name = $(if ($A2) { $A2 } else { "主 key" }) }
        $r = Invoke-Hub -Method POST -Path "/admin/members/$A1/tokens" -Body $body

        Section "把下面这段发给对方"
        Write-Host ""
        Write-Host "  确认对方已经装好并登录了 WorkBuddy 桌面 App，然后告诉他：" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  Base URL:  $Hub/v1" -ForegroundColor White
        Write-Host "  API Key:   $($r.key)" -ForegroundColor Green
        Write-Host ""
        Write-Host "  把这两项填进客户端（Claude Code / Codex / pi 都行）。" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  ⚠ 这把 key 只显示这一次。库里只存了哈希，丢了只能重新发一把。" -ForegroundColor Yellow
    }

    "keys" {
        if (-not $A1) { throw "用法：bd.ps1 keys <成员id>" }
        $d = Invoke-Hub -Path "/admin/overview"
        $m = $d.members | Where-Object { $_.id -eq $A1 }
        if (-not $m) { throw "没这个成员：$A1" }
        Section "$($m.name) 的 key"
        foreach ($t in $m.tokens) {
            $s = if ($t.enabled) { "🟢" } else { "🔴" }
            $last = if ($t.lastUsedAt) { $t.lastUsedAt } else { "未使用" }
            Write-Host ("  {0} {1}  {2,-12} 最后使用 {3,-16} 过期 {4}" -f `
                $s, $t.prefix, $t.name, $last, $t.expiresAt)
            Write-Host "         tokenId: $($t.id)" -ForegroundColor DarkGray
        }
    }

    "key-revoke" {
        if (-not $A1) { throw "用法：bd.ps1 key-revoke <tokenId>  （先 keys <成员id> 看 id）" }
        $d = Invoke-Hub -Path "/admin/overview"
        foreach ($m in $d.members) {
            $t = $m.tokens | Where-Object { $_.id -eq $A1 }
            if ($t) {
                Invoke-Hub -Method DELETE -Path "/admin/members/$($m.id)/tokens/$A1" | Out-Null
                Write-Host "  ✓ 已撤销 $($t.prefix)（$($m.name) 的 $($t.name)）" -ForegroundColor Yellow
                return
            }
        }
        throw "没找到这个 tokenId"
    }

    "reset-usage" {
        if (-not $A1) { throw "用法：bd.ps1 reset-usage <成员id>" }
        Invoke-Hub -Method POST -Path "/admin/members/$A1/reset-usage" | Out-Null
        Write-Host "  ✓ 已清零 $A1 今日用量" -ForegroundColor Green
    }

    "groups" {
        $d = Invoke-Hub -Path "/admin/overview"
        Section "分组"
        foreach ($g in $d.groups) {
            Write-Host ("  {0,-10} {1,-10} 倍率={2,-5} {3}" -f $g.id, $g.name, $g.rate, $g.description)
            Write-Host ("      可用模型: " + ($g.models -join ", ")) -ForegroundColor DarkGray
        }

        Section "模型倍率（基准：deepseek-v4.1-flash = 1.0）"
        Write-Host ("  {0,-24} {1,7} {2,7}  {3}" -f "模型", "输入", "输出", "说明")
        Write-Host ("  " + ("─" * 52)) -ForegroundColor DarkGray
        foreach ($r in $d.rates) {
            $mark = if ($r.input -eq 0 -and $r.output -eq 0) { " ← 免费，不扣配额" } else { "" }
            $color = if ($r.input -eq 0) { "Green" } else { "Gray" }
            Write-Host ("  {0,-24} {1,7:N1} {2,7:N1}  {3}{4}" -f $r.model, $r.input, $r.output, $r.note, $mark) -ForegroundColor $color
        }
    }

    "help" {
        Get-Help $PSCommandPath -Detailed
    }

    default {
        Write-Host "不认识的命令：$Action" -ForegroundColor Red
        Write-Host ""
        Write-Host "可用命令："
        Write-Host "  status                      看谁在用、渠道健不健康"
        Write-Host "  calls [条数]                最近调用流水"
        Write-Host "  channel-add <名字> [地址] [key]"
        Write-Host "  channel-list / channel-probe <渠道id>"
        Write-Host "  member-add <名字> [分组] [日配额]"
        Write-Host "  member-list / member-enable <id> / member-disable <id>"
        Write-Host "  invite <成员id> [key名字]   发一把新 key"
        Write-Host "  keys <成员id> / key-revoke <tokenId>"
        Write-Host "  reset-usage <成员id>"
        Write-Host "  groups                      看分组和倍率表"
    }
}
