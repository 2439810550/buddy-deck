<#
    XAML 绑定检查器。改完界面跑一下，几秒的事。

    抓两类**编译期不报、运行期才炸**的问题：

    ── 1. <Run Text="{Binding X}" /> 没写 Mode ──
       Run.Text 的 BindsTwoWayByDefault 是 true（WPF 里少见的例外）。
       不写 Mode 默认就是 TwoWay，而 VM 的属性基本都是只读的 → 渲染时抛
       「无法对只读属性进行 TwoWay 绑定」，弹一个很难看的错误框。
       这个坑真踩过，所以单独拎出来查：**只要 <Run> 里绑了东西又没写 Mode，一律报**。
       （本项目没有需要从 Run 写回 VM 的场景，写回一律是错的。）

    ── 2. 绑到 VM 上不存在的属性 ──
       WPF 对绑定失败是静默的：那块界面直接空着，不报错也不崩。
       拼错一个字母，只能靠肉眼发现。

    用法：
        powershell -ExecutionPolicy Bypass -File check-xaml.ps1

    退出码 0 = 通过，1 = 有问题。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# ── 1. 收集 VM 上的公开成员 ──────────────────────────────────
#
# 刻意宽松：只要 `public ... 名字` 后面跟的是 ( { = ; 就算一个成员。
# 宁可多收（漏报一个不存在的属性），不可少收（把真属性当成不存在，报一堆假问题）。
# 成员名字后面不可能直接跟 `=`、`(`、`;` 的其它情况，在 C# 里基本不存在。
$members = New-Object System.Collections.Generic.HashSet[string]
$memberPattern = 'public\s+(?:static\s+)?(?:readonly\s+)?(?:partial\s+)?(?:virtual\s+)?(?:override\s+)?[\w\.<>\?\[\],]+\s+(\w+)\s*[\({=;]'

$sources = @(
    Get-ChildItem -Path (Join-Path $root "ViewModels") -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue
    Get-ChildItem -Path $root -Filter "*.xaml.cs" -Recurse -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $root "Models") -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $root "Services") -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue
) | Where-Object { $_.FullName -notmatch '\\(bin|obj|publish)\\' }

foreach ($file in $sources) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, $memberPattern)) {
        [void]$members.Add($m.Groups[1].Value)
    }
}

Write-Host "收集到 $($members.Count) 个公开成员（来自 $($sources.Count) 个 .cs）" -ForegroundColor DarkGray

# ── 2. 扫 XAML ────────────────────────────────────────────────
$xamlFiles = Get-ChildItem -Path $root -Filter "*.xaml" -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|publish)\\' }

$problems = New-Object System.Collections.Generic.List[string]
$bindingCount = 0

foreach ($file in $xamlFiles) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    $rel = $file.FullName.Substring($root.Length).TrimStart('\')

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $where = "$rel`:$($i + 1)"
        $isRun = $line -match '<Run\s'

        # 只处理 {Binding X} / {Binding X, ...} 这种简单形式；
        # Path= / RelativeSource / Source= 之类的复杂写法跳过
        foreach ($m in [regex]::Matches($line, '\{Binding\s+([A-Za-z_]\w*)')) {
            $bindingCount++
            $prop = $m.Groups[1].Value

            # 检查 1：Run 必须显式写 Mode
            if ($isRun -and $line -notmatch 'Mode\s*=') {
                $problems.Add("$where  <Run> 绑 '$prop' 没写 Mode；Run.Text 默认 TwoWay，VM 只读属性会抛异常 → 改成 Mode=OneWay")
                continue
            }

            # 检查 2：属性存在吗
            if (-not $members.Contains($prop)) {
                $problems.Add("$where  绑到不存在的成员 '$prop'（拼错了？还是忘了在 VM 上定义？）")
            }
        }
    }
}

Write-Host "扫描 $($xamlFiles.Count) 个 XAML，共 $bindingCount 处简单绑定" -ForegroundColor DarkGray
Write-Host ""

if ($problems.Count -eq 0) {
    Write-Host "OK — 没有发现问题" -ForegroundColor Green
    exit 0
}

Write-Host "发现 $($problems.Count) 处问题：" -ForegroundColor Red
foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Yellow }
exit 1
