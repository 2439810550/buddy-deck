# 编译前清理：杀掉残留的 BuddyDeck.Gateway / BuddyDeck.App 测试实例
# 只动 bin\Debug 下的自己的进程，绝不碰 8787 上的旧 node 网关（AI 助手命脉）

$ErrorActionPreference = "SilentlyContinue"

# 只杀 BuddyDeck.Gateway.exe 和 BuddyDeck.App.exe（bin\Debug 下的测试实例）
Get-CimInstance Win32_Process -Filter "Name='BuddyDeck.Gateway.exe'" |
    ForEach-Object { Write-Host "清理网关: PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "Name='BuddyDeck.App.exe'" |
    ForEach-Object { Write-Host "清理 App: PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }

# dotnet 里跑 BuddyDeck.Gateway.dll 的（dotnet host 形式）
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*BuddyDeck.Gateway.dll*' } |
    ForEach-Object { Write-Host "清理 dotnet 网关: PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }

Start-Sleep -Seconds 1

# 确认 8787 旧网关还在（绝不能被误杀）
$health = try { (Invoke-WebRequest -Uri http://127.0.0.1:8787/healthz -TimeoutSec 5 -UseBasicParsing).Content } catch { "" }
if ($health -match "aec74cebbc60") {
    Write-Host "✓ 旧网关 8787 在线（未动）"
} else {
    Write-Host "⚠ 旧网关 8787 无响应 —— 如果你没动它，可能出问题了"
}

dotnet build
