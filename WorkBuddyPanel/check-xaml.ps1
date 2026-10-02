<#
    XAML 静态检查器。改完界面跑一下，几秒的事。

    抓三类**编译期不报、运行期才炸**的问题：

    ── 1. <Run Text="{Binding X}" /> 没写 Mode ──
       Run.Text 的 BindsTwoWayByDefault 是 true（WPF 里少见的例外）。
       不写 Mode 默认就是 TwoWay，而 VM 的属性基本都是只读的 → 渲染时抛
       「无法对只读属性进行 TwoWay 绑定」，弹一个很难看的错误框。
       这个坑真踩过，所以单独拎出来查：**只要 <Run> 里绑了东西又没写 Mode，一律报**。

    ── 2. 绑到 VM 上不存在的属性 ──
       WPF 对绑定失败是静默的：那块界面直接空着，不报错也不崩。
       拼错一个字母，只能靠肉眼发现。

    ── 3. {StaticResource X} 里的 X 不存在 ──
       这条是 2026-09-29 补上的，因为真踩了：
       PoolEditDialog.xaml 写了 {StaticResource WindowBg}，而 App.xaml 里
       叫的是 Bg —— 编译通过、没有任何警告，直到用户点「新建池」才抛
       XamlParseException「无法找到名为 WindowBg 的资源」。

       注意光看 App.xaml 不够：XAML 的资源查找是**沿可视树向上**的，
       所以每个文件自己定义的 ResourceDictionary 也要算进可用集合。

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

# ── 2. 收集所有可用的资源 key ────────────────────────────────
#
# x:Key="X" 就是资源定义。App.xaml 里的是全局的，
# 其它 XAML 里 ResourceDictionary 里的对这个文件（及其子元素）可见。
$globalKeys = New-Object System.Collections.Generic.HashSet[string]
$appXaml = Join-Path $root "App.xaml"
if (Test-Path $appXaml) {
    $appText = Get-Content $appXaml -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($appText, 'x:Key="([^"]+)"')) {
        [void]$globalKeys.Add($m.Groups[1].Value)
    }
}
Write-Host "App.xaml 里定义了 $($globalKeys.Count) 个全局资源" -ForegroundColor DarkGray

$xamlFiles = Get-ChildItem -Path $root -Filter "*.xaml" -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|publish)\\' }

$problems = New-Object System.Collections.Generic.List[string]
$bindingCount = 0
$resourceCount = 0

foreach ($file in $xamlFiles) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    $rel = $file.FullName.Substring($root.Length).TrimStart('\')

    # 这个文件自己定义的资源
    $localKeys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in [regex]::Matches($text, 'x:Key="([^"]+)"')) {
        [void]$localKeys.Add($m.Groups[1].Value)
    }

    $lines = Get-Content $file.FullName -Encoding UTF8

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $where = "$rel`:$($i + 1)"

        # 注释里的东西不算 —— {$Binding ...} 写在注释里也曾经骗过我
        $codeOnly = $line -replace '<!--.*?-->', ''
        $isRun = $codeOnly -match '<Run\s'

        # ── 检查 1 / 2：绑定 ──
        foreach ($m in [regex]::Matches($codeOnly, '\{Binding\s+([A-Za-z_]\w*)')) {
            $bindingCount++
            $prop = $m.Groups[1].Value

            if ($isRun -and $codeOnly -notmatch 'Mode\s*=') {
                $problems.Add("$where  <Run> 绑 '$prop' 没写 Mode；Run.Text 默认 TwoWay，VM 只读属性会抛异常 → 改成 Mode=OneWay")
                continue
            }

            if (-not $members.Contains($prop)) {
                $problems.Add("$where  绑到不存在的成员 '$prop'（拼错了？还是忘了在 VM 上定义？）")
            }
        }

        # ── 检查 3：静态资源引用 ──
        foreach ($m in [regex]::Matches($codeOnly, '\{StaticResource\s+([^\s,}]+)')) {
            $resourceCount++
            $key = $m.Groups[1].Value
            if (-not $globalKeys.Contains($key) -and -not $localKeys.Contains($key)) {
                $problems.Add("$where  引用了不存在的资源 '$key'（打开这个窗口时会抛 XamlParseException）")
            }
        }
    }
}

# ── 3. 反向检查：定义了但没人用的资源（只提示，不算错）──────
$allUsed = New-Object System.Collections.Generic.HashSet[string]
foreach ($file in $xamlFiles) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, '\{StaticResource\s+([^\s,}]+)')) {
        [void]$allUsed.Add($m.Groups[1].Value)
    }
}
$unused = @($globalKeys | Where-Object { -not $allUsed.Contains($_) })

Write-Host "扫描 $($xamlFiles.Count) 个 XAML，$bindingCount 处绑定，$resourceCount 处资源引用" -ForegroundColor DarkGray
if ($unused.Count -gt 0) {
    Write-Host "（提示：App.xaml 里有 $($unused.Count) 个资源没人用：$($unused -join ', ')。可能是留着给将来用，也可能该删。）" -ForegroundColor DarkGray
}
Write-Host ""

if ($problems.Count -eq 0) {
    Write-Host "OK — 没有发现问题" -ForegroundColor Green
    exit 0
}

Write-Host "发现 $($problems.Count) 处问题：" -ForegroundColor Red
foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Yellow }
exit 1
