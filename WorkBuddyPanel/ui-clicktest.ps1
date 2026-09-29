<#
    界面点击测试。改完界面跑一遍，自动点每个按钮，抓运行时才炸的异常。

    ── 为什么需要它 ────────────────────────────────────────────

    WPF 的绑定和资源查找都是运行时的。写错一个资源名、Command 里抛异常 ——
    编译器和静态检查都看不见，只有真正点到那个按钮才炸。
    2026-09-29 就翻了车：PoolEditDialog 里写了个不存在的资源名，
    「新建池 / 改名字」一点就抛 XamlParseException，静态检查全绿。

    ── 它做什么 ────────────────────────────────────────────────

      1. 连到指定的窗口（按 PID / 句柄，不按标题 —— 见下面的坑）
      2. 切到每个标签页
      3. 把页面上每个按钮点一遍（跳过破坏性的那几个）
      4. 每次点完检查有没有新弹的对话框，有就把内容记下来然后关掉
      5. 输出报告：哪些按钮点了没反应、哪些弹了窗

    弹窗必须**点完立刻检测并关掉** —— 模态对话框会阻塞 UI 线程，
    不理它后面所有操作全卡住。

    用法：
        # 先让程序跑起来，拿到 PID
        powershell -ExecutionPolicy Bypass -File ui-clicktest.ps1 -ProcessId 1234

        # 也可以直接给窗口句柄
        powershell -ExecutionPolicy Bypass -File ui-clicktest.ps1 -Hwnd 330048 -Tab 分池

    退出码 0 = 没有异常弹窗，1 = 有。

    ── 已知的坑（都踩过）────────────────────────────────────────

    ⚠ 必须按 PID / 句柄定位窗口，不能按标题找。
      正式版（D:\BuddyDeck）和开发版的窗口标题**一模一样**（都叫
      「WorkBuddy 控制台」），按标题找会把点击打到用户正在用的窗口上。

    ⚠ 禁用的按钮 Invoke() 会抛「无法识别」，那是正常的，不是 bug。
      典型：账号列表第一个「设为使用中」永远是禁用的（它就是当前在用的号）。

    ⚠ 破坏性按钮要跳过：删除、停止、打开目录。
      「打开目录」会弹一堆 explorer 窗口骚扰用户。
#>
[CmdletBinding()]
param(
    [int]$ProcessId = 0,
    [int64]$Hwnd = 0,
    # 先切到这个标签页再点（空 = 当前页）
    [string]$Tab = "",
    # 每个按钮点完等多久（毫秒）。网络操作慢的页面调大。
    [int]$WaitMs = 1200
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class UiTest {
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);

    /// 所有可见对话框（类名 #32770）
    public static List<string> Dialogs() {
        var r = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
            if (cls.ToString() != "#32770") return true;
            var s = new StringBuilder(400); GetWindowText(h, s, 400);
            r.Add(h.ToInt64() + "|" + s.ToString());
            return true;
        }, IntPtr.Zero);
        return r;
    }

    public static void Close(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
}
"@

# ── 解析窗口 ─────────────────────────────────────────────────
#
# 按 PID 查顶层窗口要用 UIAutomation 的 RootElement（已验证可靠），
# 不要自己 EnumWindows：那个会捞到 "GDI+ Window (WorkBuddyPanel.exe)"
# 这类隐藏辅助窗口，而且拿到的句柄对不上。
#
# 一个进程可能有多个顶层窗口，挑那个**带标签页的** —— 那就是主窗口。
function Find-AppWindow([int]$targetPid) {
    $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
    $wins = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

    $tabCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)

    foreach ($w in $wins) {
        if ($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond).Count -gt 0) {
            return $w
        }
    }
    # 没有标签页的就退回第一个有标题的
    foreach ($w in $wins) {
        if ($w.Current.Name) { return $w }
    }
    return $null
}

if ($Hwnd -ne 0) {
    if (-not [UiTest]::IsWindow([IntPtr]$Hwnd)) {
        Write-Host "窗口句柄 $Hwnd 无效。" -ForegroundColor Red
        exit 1
    }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
} elseif ($ProcessId -ne 0) {
    $root = Find-AppWindow $ProcessId
    if ($null -eq $root) {
        Write-Host "PID $ProcessId 找不到窗口。" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "要给 -ProcessId 或 -Hwnd。" -ForegroundColor Red
    exit 1
}

$handle = $root.Current.NativeWindowHandle
Write-Host "窗口：$($root.Current.Name)（PID $($root.Current.ProcessId)，句柄 $handle）" -ForegroundColor Cyan

# ── 备份配置 ─────────────────────────────────────────────────
#
# 即使跳过了「保存设置」，程序自己也可能在某些操作里写配置
# （自动保存、端口占用的回退等）。
#
# 所以这个脚本自己也要备一份 —— 不能指望调用方（run-all-tests.ps1）
# 一定备过。实测：直接调这个脚本时，用户的端口被改成了 8788。
$cfgDir = Join-Path $env:APPDATA "WorkBuddyPanel"
$cfgBackups = @{}
foreach ($f in @("settings.json", "pools.json")) {
    $src = Join-Path $cfgDir $f
    if (Test-Path $src) {
        $bak = "$src.clicktest-backup"
        Copy-Item $src $bak -Force
        $cfgBackups[$src] = $bak
    }
}
if ($cfgBackups.Count -gt 0) {
    Write-Host "  备份了 $($cfgBackups.Count) 个配置文件" -ForegroundColor DarkGray
}

# ── 清掉残留对话框（上次跑崩留下的）─────────────────────────
# 连清三轮 —— 对话框可能一层套一层（父表单 + 校验提示），
# 一轮只关得掉最上面那层。实测：只清一轮时，前一个标签页留下的
# 表单会堆到下一个页签，把 UI 线程堵住，后面的按钮全部点不动。
$residual = @()
for ($round = 1; $round -le 3; $round++) {
    $found = [UiTest]::Dialogs()
    if ($found.Count -eq 0) { break }
    $residual += $found
    foreach ($d in $found) {
        $parts = $d.Split("|")
        $dh = [IntPtr][int64]$parts[0]
        if ($round -eq 1) { Write-Host "  清残留对话框：$($parts[1])" -ForegroundColor DarkGray }
        $closed = $false
        try {
            $de = [System.Windows.Automation.AutomationElement]::FromHandle($dh)
            $bc = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            foreach ($bb in $de.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
                if ($bb.Current.Name.Trim() -eq "取消") {
                    $bb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    $closed = $true
                    break
                }
            }
        } catch { }
        if (-not $closed) { [UiTest]::Close($dh) }
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 300
}



# ── 切标签页 ─────────────────────────────────────────────────
if ($Tab -ne "") {
    $tabCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $switched = $false
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
        if ($t.Current.Name -like "*$Tab*") {
            $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Write-Host "切到标签页：$($t.Current.Name.Trim())" -ForegroundColor Cyan
            $switched = $true
            Start-Sleep 2
            break
        }
    }
    if (-not $switched) {
        Write-Host "没有匹配 '$Tab' 的标签页。" -ForegroundColor Red
        exit 1
    }
}

# ── 点每个按钮 ───────────────────────────────────────────────
# 跳过清单：会改状态、弹窗口、或者花钱的按钮
#
#   删除 / 停止      —— 改运行状态
#   打开目录         —— 弹一堆 explorer
#   恢复默认设置     —— 会把路径重置成发布版默认值，在开发目录里就废了
#                       （实测踩过：点完它之后所有账号操作都报「找不到文件」）
#   发送             —— 会真的发请求花积分
$skipPattern = "退出|关闭|取消|卸载|删除|停止|打开目录|恢复默认|保存设置|发送|浏览|自动探测"

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
Write-Host "找到 $($buttons.Count) 个按钮" -ForegroundColor Cyan
Write-Host ""

$clicked = 0
$skipped = 0
$disabled = 0
$dialogs = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]

foreach ($b in $buttons) {
    $name = $b.Current.Name.Trim()
    if (-not $name) { continue }

    if ($name -match $skipPattern) {
        $skipped++
        continue
    }

    # 禁用按钮跳过 —— Invoke 会抛「无法识别」，但那不是 bug
    if (-not $b.Current.IsEnabled) {
        $disabled++
        Write-Host "  ○ $name（已禁用，跳过）" -ForegroundColor DarkGray
        continue
    }

    # 只点真正支持 Invoke 的控件。
    #
    # UIAutomation 把 Expander / 复选框之类的也会报成 ControlType.Button，
    # 但它们只支持 Toggle 不支持 Invoke —— 直接 Invoke 会抛「不支持的模式」。
    # 那不是 bug（实测：账号页那个「使用说明」折叠条就被误报过）。
    $supportsInvoke = $false
    try {
        foreach ($pat in $b.GetSupportedPatterns()) {
            if ($pat.ProgrammaticName -match "InvokePattern") { $supportsInvoke = $true; break }
        }
    } catch { }
    if (-not $supportsInvoke) {
        $skipped++
        Write-Host "  ○ $name（不支持点击，跳过）" -ForegroundColor DarkGray
        continue
    }

    try {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $clicked++
    } catch {
        $failures.Add("$name  ← 点不动：$($_.Exception.Message)")
        Write-Host "  ✗ $name（点不动）" -ForegroundColor Red
        continue
    }

    Start-Sleep -Milliseconds $WaitMs

    # 立刻检查弹窗，否则模态框会把后面全堵住
    foreach ($d in [UiTest]::Dialogs()) {
        $parts = $d.Split("|")
        $dh = [IntPtr][int64]$parts[0]
        $title = $parts[1]

        $body = ""
        try {
            $de = [System.Windows.Automation.AutomationElement]::FromHandle($dh)
            $tc = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)
            foreach ($t in $de.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) {
                $body += $t.Current.Name + " "
            }
        } catch { }

        $dialogs.Add("$name → [$title] $($body.Trim())")
        Write-Host "  ⚠ $name 弹了窗：$($body.Trim())" -ForegroundColor Yellow

        # 关掉它
        $closed = $false
        try {
            $de = [System.Windows.Automation.AutomationElement]::FromHandle($dh)
            $bc = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            foreach ($bb in $de.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
                if ($bb.Current.Name -match "确定|OK|是|关闭") {
                    $bb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    $closed = $true
                    break
                }
            }
        } catch { }
        if (-not $closed) { [UiTest]::Close($dh) }

        Start-Sleep -Milliseconds 500
    }

    # 再扫一遍：有些按钮会弹出「父对话框 + 校验提示」两层。
    # 只关掉校验提示的话，父对话框还挂着 —— 堆几层之后 UI 线程就卡死了，
    # 后面的按钮全部点不动（实测踩过：账号页因此在全量跑里假报失败）。
    #
    # 这里只关**没有输入框**的那种纯提示框；带输入框的是用户真该填的表单，
    # 留着它会被当成「新开的对话框」记录下来，但不会一直堆。
    foreach ($d in [UiTest]::Dialogs()) {
        $parts = $d.Split("|")
        $dh = [IntPtr][int64]$parts[0]
        $hasEdit = $false
        try {
            $de = [System.Windows.Automation.AutomationElement]::FromHandle($dh)
            $ec = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)
            $hasEdit = $de.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ec).Count -gt 0
        } catch { }
        if (-not $hasEdit) { [UiTest]::Close($dh) }
    }

    if ($dialogs.Count -eq 0 -or $dialogs[$dialogs.Count - 1] -notlike "$name *") {
        Write-Host "  ✓ $name" -ForegroundColor Green
    }
}

# ── 报告 ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "════ 结果 ════" -ForegroundColor Cyan
Write-Host "  点了 $clicked 个，跳过 $skipped 个（破坏性），跳过 $disabled 个（禁用）"

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "  点不动的（$($failures.Count)）：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "    $f" -ForegroundColor Yellow }
}

if ($dialogs.Count -gt 0) {
    Write-Host ""
    Write-Host "  弹出的对话框（$($dialogs.Count)）：" -ForegroundColor Yellow
    foreach ($d in $dialogs) { Write-Host "    $d" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "  注意：有些弹窗是正常的（比如删除确认、错误提示）。" -ForegroundColor DarkGray
    Write-Host "  要区分「本来就该弹」和「不该弹」—— 看内容里有没有异常堆栈。" -ForegroundColor DarkGray
}

# 有异常字样才算失败
$bad = @($dialogs | Where-Object { $_ -match "Exception|异常|出错|失败|无法" })
if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host "  其中有 $($bad.Count) 个含异常信息 —— 这是 bug。" -ForegroundColor Red
    exit 1
}

# ── 恢复配置 ─────────────────────────────────────────────────
foreach ($pair in $cfgBackups.GetEnumerator()) {
    try {
        Copy-Item $pair.Value $pair.Key -Force
        Remove-Item $pair.Value -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "  恢复 $($pair.Key) 失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if ($cfgBackups.Count -gt 0) {
    Write-Host "  已恢复配置文件" -ForegroundColor DarkGray
}

if ($failures.Count -gt 0) { exit 1 }

Write-Host ""
Write-Host "OK — 没有发现异常" -ForegroundColor Green
exit 0
