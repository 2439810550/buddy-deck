<#
    打成「免安装包」。

    干完之后 publish\ 目录里会有：

        WorkBuddyPanel.exe          自包含的 .NET，用户不需要装 .NET 运行时
        runtime\node.exe            便携版 Node.js，用户不需要装 Node
        runtime\workbuddy_api\      一份 workbuddy_api 源码副本

    把整个 publish 文件夹拷给任何人，双击 exe 就能用，什么都不用装。

    用法：
        powershell -ExecutionPolicy Bypass -File build.ps1
        powershell -ExecutionPolicy Bypass -File build.ps1 -NodeVersion 24.14.0
        powershell -ExecutionPolicy Bypass -File build.ps1 -SkipNode   # 只出来 exe
        powershell -ExecutionPolicy Bypass -File build.ps1 -Output publish.next  # 换个输出目录
#>
[CmdletBinding()]
param(
    [string]$NodeVersion = "22.23.3",
    [switch]$SkipNode,
    # 输出目录。默认 publish。
    # 想打到别处就传 -Output publish.next —— 正在运行的实例会锁住 publish\，
    # 这时候不用去杀进程，换个目录就行。
    [string]$Output = "publish"
)

$ErrorActionPreference = "Stop"

$root      = $PSScriptRoot
$parent    = Split-Path $root -Parent
$cache     = Join-Path $parent ".cache"
$publish   = Join-Path $root $Output
$runtime   = Join-Path $publish "runtime"
$apiSource = Join-Path $parent "workbuddy_api"
$csproj    = Join-Path $root "WorkBuddyPanel.csproj"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "!!! $text" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $csproj)) { Fail "找不到 $csproj" }

Step "1/4  清理 $Output"
if (Test-Path $publish) {
    try {
        Remove-Item -Recurse -Force $publish -ErrorAction Stop
    } catch {
        # 十有八九是用户正在运行那个 exe，文件被锁。
        # 这种情况不要反射性地去杀进程 —— 换个输出目录就行。
        Write-Host "    删不掉（多半是有实例正在运行）" -ForegroundColor Yellow
        Write-Host "    换个输出目录重试：-Output publish.next" -ForegroundColor Yellow
        exit 1
    }
}

Step "2/4  发布 WPF（自包含，用户不需要装 .NET）"
& dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 失败" }

if ($SkipNode) {
    Step "3/4  跳过 Node（-SkipNode）"
} else {
    Step "3/4  准备便携版 Node.js $NodeVersion"
    New-Item -ItemType Directory -Force -Path $cache | Out-Null

    $zipName = "node-v$NodeVersion-win-x64.zip"
    $zipPath = Join-Path $cache $zipName
    $nodeDir = Join-Path $cache "node-v$NodeVersion-win-x64"

    if (Test-Path $nodeDir) {
        Write-Host "    已有缓存：$nodeDir"
    } else {
        if (-not (Test-Path $zipPath)) {
            $url = "https://nodejs.org/dist/v$NodeVersion/$zipName"
            Write-Host "    下载 $url"
            try {
                # 用 curl 而不是 Invoke-WebRequest ——
                # PowerShell 5.1 的 Invoke-WebRequest 会渲染进度条，实测把 34MB 拖到几分钟，
                # curl 只要十来秒。curl.exe 从 Windows 10 1803 起就自带了。
                & curl.exe -sS -L --fail --max-time 900 -o $zipPath $url
                if ($LASTEXITCODE -ne 0) { throw "curl 退出码 $LASTEXITCODE" }
            } catch {
                Fail "下载 Node 失败：$($_.Exception.Message)`n    可以自己下好放进 $cache\，或者用 -SkipNode 只出 exe。"
            }
        }
        Write-Host "    解压到 $cache"
        Expand-Archive -Path $zipPath -DestinationPath $cache -Force
    }

    $nodeExe = Join-Path $nodeDir "node.exe"
    if (-not (Test-Path $nodeExe)) { Fail "解压后没找到 node.exe" }

    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Copy-Item $nodeExe (Join-Path $runtime "node.exe") -Force
    Write-Host ("    node.exe  {0:N0} MB" -f ((Get-Item $nodeExe).Length / 1MB))
}

Step "4/4  附上 workbuddy_api 源码"
if (-not (Test-Path (Join-Path $apiSource "src\bin.js"))) {
    Fail "找不到 $apiSource\src\bin.js"
}
$apiDest = Join-Path $runtime "workbuddy_api"
New-Item -ItemType Directory -Force -Path $apiDest | Out-Null
Copy-Item (Join-Path $apiSource "src") $apiDest -Recurse -Force
Copy-Item (Join-Path $apiSource "package.json") $apiDest -Force
if (Test-Path (Join-Path $apiSource "LICENSE")) {
    Copy-Item (Join-Path $apiSource "LICENSE") $apiDest -Force
}

# skills/：给「别人的 AI 工具」用的技能包（Claude Code / pi / DSH 那种）。
#
# 网关自己运行**不需要**它 —— 但用户装了这套之后，多半会想让自己的
# 编辑器/harness 也用上联网能力（那些工具是按 skill 目录找的）。
# 只有几个文件，带上不亏。
if (Test-Path (Join-Path $apiSource "skills")) {
    Copy-Item (Join-Path $apiSource "skills") $apiDest -Recurse -Force
}

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("完成：{0}   （{1:N1} MB）" -f $publish, $size) -ForegroundColor Green
Write-Host "把整个 publish 文件夹拷走即可，目标机器什么都不用装。" -ForegroundColor Green
