<#
    把账号保险库切成几个独立的池，每个池给一个 workbuddy_api 实例用。

    ── 为什么要这么做 ─────────────────────────────────────────

    workbuddy_api 的保险库里只有一个 activeId —— 也就是「此刻用哪个号」。
    一个实例同一时刻只能烧一个账号的额度。

    要让 9 个号的额度能并行用，就得起多个实例，每个实例管一部分号。
    BuddyDeck Hub 再把它们聚合成一个入口。

    ── 这个脚本做什么 ─────────────────────────────────────────

    读原来的保险库（只读，绝不修改）
      ↓
    按你指定的方式分组
      ↓
    给每组生成一份独立的保险库，放到 ~/.workbuddy-api/pools/<池名>/

    每个池的保险库都是**合法的完整副本**（version / activeId / accounts 都在），
    workbuddy_api 拿 WORKBUDDY_API_HOME 指过去就能直接用。

    ── 怎么用 ─────────────────────────────────────────────────

    # 看当前有哪些号（不改任何东西）
    .\split-pools.ps1 -List

    # 按顺序两两分组：池1=前5个号，池2=后4个号
    .\split-pools.ps1 -Split 5,4

    # 每个号单独一个池（9 个池）
    .\split-pools.ps1 -EachAccount

    # 指定哪些号进哪个池
    .\split-pools.ps1 -Pools @{ "pool-a" = @("主号","少钦号"); "pool-b" = @("杨佳葳","张天翼") }

    # 生成后看看每个池的到期情况
    .\split-pools.ps1 -Status
#>
[CmdletBinding(DefaultParameterSetName = "None")]
param(
    # 按顺序切分。5,4 = 前 5 个号一组、后 4 个号一组
    [Parameter(ParameterSetName = "Split")][int[]]$Split,

    # 同上，但用字符串传，避开 powershell -File 的逗号解析坑。
    # 实测：powershell -File x.ps1 -Split 3,3,3 传进来的数组是 [333]。
    # 想从外部脚本调就用这个：-SplitSpec "3,3,3"
    [Parameter(ParameterSetName = "SplitSpec")][string]$SplitSpec,

    # 每个号一个池
    [Parameter(ParameterSetName = "Each")][switch]$EachAccount,

    # 手工指定：池名 -> 账号 label 数组
    [Parameter(ParameterSetName = "Named")][hashtable]$Pools,

    [Parameter(ParameterSetName = "List")][switch]$List,
    [Parameter(ParameterSetName = "Status")][switch]$Status,

    # 保险库位置
    [string]$Vault = (Join-Path $env:USERPROFILE ".workbuddy-api"),

    # 干跑：只打印，不写文件
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ── 读保险库。必须 -Encoding UTF8，否则中文 label 会坏 ──
function Read-Vault {
    $path = Join-Path $Vault "accounts.cn.json"
    if (-not (Test-Path $path)) { throw "找不到 $path" }
    $raw = Get-Content $path -Raw -Encoding UTF8
    return $raw | ConvertFrom-Json
}

function Label-Of($account) {
    if ($account.label) { return $account.label }
    return $account.uid.Substring(0, 8)
}

# ── 生成一份池的保险库 ──
function Write-Pool($name, $accounts, $all) {
    $dir = Join-Path $Vault "pools\$name"
    $file = Join-Path $dir "accounts.cn.json"

    # 池里第一个号当 activeId。实例启动就用这个号，
    # 后面靠 WorkBuddy 面板或 CLI 手动切。
    $active = if ($accounts.Count -gt 0) { $accounts[0].id } else { "" }

    $doc = [ordered]@{
        version  = $all.version
        activeId = $active
        accounts = @($accounts)
    }

    $json = $doc | ConvertTo-Json -Depth 10

    if ($DryRun) {
        $labels = ($accounts | ForEach-Object { Label-Of $_ }) -join ", "
        Write-Host ("  [dry-run] {0,-10} {1,2} 个号  {2}" -f $name, $accounts.Count, $labels)
        return
    }

    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # 里面是 refreshToken，等同账号密码 —— 0600 权限
    [System.IO.File]::WriteAllText($file, $json, [System.Text.UTF8Encoding]::new($false))

    $labels = ($accounts | ForEach-Object { Label-Of $_ }) -join ", "
    Write-Host ("  ✓ {0,-10} {1,2} 个号  {2}" -f $name, $accounts.Count, $labels) -ForegroundColor Green
    Write-Host ("      {0}" -f $file) -ForegroundColor DarkGray
}

# ── 列出账号 ──
function Show-Accounts($all) {
    Write-Host ""
    Write-Host "  保险库里的账号" -ForegroundColor Cyan
    Write-Host ("  " + ("─" * 66)) -ForegroundColor DarkGray
    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    for ($i = 0; $i -lt $all.accounts.Count; $i++) {
        $a = $all.accounts[$i]
        $exp = [int](($a.expiresAtMs - $now) / 86400000)
        $rexp = [int](($a.refreshExpiresAtMs - $now) / 86400000)
        Write-Host ("  {0,2}. {1,-14} access 剩 {2,3} 天   refresh 剩 {3,3} 天" -f `
            ($i + 1), (Label-Of $a), $exp, $rexp)
    }
    Write-Host ""
}

$v = Read-Vault

# 按给定的切分方案生成多个池。
function Write-Split($parts, $all) {
    $total = ($parts | Measure-Object -Sum).Sum
    if ($total -ne $all.accounts.Count) {
        throw "切分数量 $total 和账号总数 $($all.accounts.Count) 不匹配"
    }

    Write-Host ""
    Write-Host "  生成账号池" -ForegroundColor Cyan
    $idx = 0
    for ($g = 0; $g -lt $parts.Count; $g++) {
        $n = $parts[$g]
        $slice = @($all.accounts[$idx..($idx + $n - 1)])
        Write-Pool ("pool-" + [char](65 + $g)) $slice $all
        $idx += $n
    }
}
Write-Host ""
Write-Host "  源保险库: $(Join-Path $Vault 'accounts.cn.json')" -ForegroundColor DarkGray
Write-Host ("  共 {0} 个账号，当前活跃: {1}" -f $v.accounts.Count, ($v.accounts | Where-Object { $_.id -eq $v.activeId } | ForEach-Object { Label-Of $_ }))

switch ($PSCmdlet.ParameterSetName) {

    "List" {
        Show-Accounts $v
    }

    "Status" {
        $poolDir = Join-Path $Vault "pools"
        if (-not (Test-Path $poolDir)) {
            Write-Host ""
            Write-Host "  还没有池。先跑 .\split-pools.ps1 -Split 5,4" -ForegroundColor Yellow
            return
        }
        Write-Host ""
        Write-Host "  已有的池" -ForegroundColor Cyan
        Write-Host ("  " + ("─" * 66)) -ForegroundColor DarkGray
        $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
        Get-ChildItem $poolDir -Directory | ForEach-Object {
            $f = Join-Path $_.FullName "accounts.cn.json"
            if (-not (Test-Path $f)) { return }
            $pv = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
            $labels = ($pv.accounts | ForEach-Object { Label-Of $_ }) -join ", "
            $minExp = ($pv.accounts | ForEach-Object { [int](($_.expiresAtMs - $now) / 86400000) } | Measure-Object -Minimum).Minimum
            Write-Host ("  {0,-12} {1,2} 个号   最近到期 {2,3} 天   {3}" -f `
                $_.Name, $pv.accounts.Count, $minExp, $labels)
        }
        Write-Host ""
    }

    "Split" {
        if (-not $Split) { throw "给个切法，比如 -Split 5,4" }
        Write-Split $Split $v
    }

    "SplitSpec" {
        if (-not $SplitSpec) { throw "给个切法，比如 -SplitSpec 5,4" }
        $parts = @($SplitSpec -split '[,\s]+' | Where-Object { $_ -ne "" } | ForEach-Object { [int]$_ })
        Write-Split $parts $v
    }

    "Each" {
        Write-Host ""
        Write-Host "  每个号一个池" -ForegroundColor Cyan
        for ($i = 0; $i -lt $v.accounts.Count; $i++) {
            $a = $v.accounts[$i]
            $name = "acc-" + ($i + 1).ToString("00")
            Write-Pool $name @($a) $v
        }
    }

    "Named" {
        Write-Host ""
        Write-Host "  按指定分组" -ForegroundColor Cyan
        foreach ($name in $Pools.Keys) {
            $wanted = $Pools[$name]
            $picked = @($v.accounts | Where-Object { $wanted -contains (Label-Of $_) })
            if ($picked.Count -eq 0) {
                Write-Host ("  ! {0} 里没有匹配的号" -f $name) -ForegroundColor Yellow
                continue
            }
            Write-Pool $name $picked $v
        }
    }

    default {
        Show-Accounts $v
        Write-Host "  用法:" -ForegroundColor Cyan
        Write-Host "    .\split-pools.ps1 -Split 5,4        前5个一组、后4个一组"
        Write-Host "    .\split-pools.ps1 -EachAccount      每个号一个池"
        Write-Host "    .\split-pools.ps1 -Status           看已有的池"
        Write-Host "    .\split-pools.ps1 -List             只看账号列表"
        Write-Host ""
    }
}
