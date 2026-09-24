; ─────────────────────────────────────────────────────────────
;  BuddyDeck（WorkBuddy 控制台）安装包脚本
;
;  一键编译（推荐）：
;      powershell -ExecutionPolicy Bypass -File make-installer.ps1
;
;  或直接调 Inno Setup 编译器：
;      ISCC.exe installer.iss
;      ISCC.exe /DAppVersion=1.0.1 installer.iss
;
;  产物：dist\BuddyDeck-Setup-<版本>.exe
;
;  ## 为什么不做成「单文件自解压」
;
;  包里有 233MB（.NET 运行时 141MB + node.exe 83MB）。做成自解压单文件的话，
;  用户每次双击都要先解压到临时目录，慢且占双份空间。
;  用 Inno Setup 的 LZMA2 直接装到目标目录，装完就是解压好的，启动最快。
;
;  ## 压缩率的现实
;
;  .NET 运行时和 node.exe 本身已经是压过的东西，LZMA2 大约只能压到 30% 左右
;  （实测 233MB → 68MB）。想更小只能上 PublishTrimmed，但 WPF 不支持裁剪。
; ─────────────────────────────────────────────────────────────

#define AppName        "BuddyDeck"
#define AppNameCN      "WorkBuddy 控制台"
#define AppPublisher   "2439810550"
#define AppURL         "https://github.com/2439810550/buddy-deck"

; 版本号和源目录都允许命令行覆盖（make-installer.ps1 会传）
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "publish.next"
#endif

[Setup]
AppId={{8F3A21D4-6B7E-4C51-9A2D-5E7F1C48B903}
AppName={#AppNameCN}
AppVersion={#AppVersion}
AppVerName={#AppNameCN} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases

; 按用户安装，不弹 UAC。这是个个人本机工具，没必要装进 Program Files。
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppNameCN}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 自带的是 win-x64 的 .NET 和 Node
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
SetupLogging=yes
UninstallDisplayName={#AppNameCN}
UninstallDisplayIcon={app}\WorkBuddyPanel.exe

[Languages]
Name: "chinese"; MessagesFile: ".\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "startup";     Description: "开机自动启动（静默进托盘，网关开机即就绪）"; GroupDescription: "附加任务:"

[Files]
; 主程序 + WPF 原生库
Source: "{#SourceDir}\WorkBuddyPanel.exe";           DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\D3DCompiler_47_cor3.dll";      DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\PenImc_cor3.dll";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\PresentationNative_cor3.dll";  DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\vcruntime140_cor3.dll";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\wpfgfx_cor3.dll";              DestDir: "{app}"; Flags: ignoreversion

; 自带的运行时：便携版 Node + 一份 workbuddy_api 源码。
; 有这两个，目标机器就不需要装 Node。
Source: "{#SourceDir}\runtime\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppNameCN}";        Filename: "{app}\WorkBuddyPanel.exe"
Name: "{group}\卸载 {#AppNameCN}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppNameCN}";  Filename: "{app}\WorkBuddyPanel.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WorkBuddyPanel.exe"; Description: "立即启动 {#AppNameCN}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 程序自带的运行时整个删掉。用户数据不在这里 ——
; 账号在 ~\.workbuddy-api，界面设置在 %APPDATA%\WorkBuddyPanel，
; 那两个是“用户自己的东西”，卸载不碰。
Type: filesandordirs; Name: "{app}\runtime"

; 中文语言包是社区维护的，没跟着 Inno 更新这几条「自定义消息」。
; 不补的话这几处界面会回落成英文，编译时也会打 9 条警告。
[CustomMessages]
chinese.CreateQuickLaunchIcon=创建快速启动栏图标
chinese.ProgramOnTheWeb=访问 %1 网站
chinese.UninstallProgram=卸载 %1
chinese.LaunchProgram=运行 %1
chinese.AssocFileExtension=用 %1 打开扩展名为 %2 的文件
chinese.AssocingFileExtension=正在将扩展名为 %1 的文件关联到 %2…
chinese.AutoStartProgramGroupDescription=启动:
chinese.AutoStartProgram=自动启动:
chinese.AddonHostProgramNotFound=%1 没有安装在您所选择的文件夹中。%n%n是否仍要继续？

[Messages]
chinese.WelcomeLabel2=即将把 [name] 安装到你的电脑。%n%n它是一个本机小工具：把 WorkBuddy 桌面 App 里的账号变成本地 AI 网关，然后 Claude Code、Codex、pi 这些工具就能用上它的模型。%n%n它自带了运行环境，你不需要额外安装 .NET 或 Node。设置和账号数据都保存在你自己的用户目录里，卸载不会删。
chinese.FinishedLabel=安装完成。%n%n第一次打开会自动检测环境、读取账号。界面上的「一键接入」能帮你把 Claude Code / Codex / pi 的配置写好 —— 改之前会备份，随时能还原。

[Code]
{ 开机自启：写当前用户的 Run 键，指向 --tray（静默进托盘）。
  用 --tray 而不是普通启动，是因为这个软件平时就该在后台跑着网关。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath: String;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('startup') then
    begin
      ExePath := ExpandConstant('{app}\WorkBuddyPanel.exe');
      RegWriteStringValue(
        HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Run',
        'WorkBuddyPanel',
        '"' + ExePath + '" --tray'
      );
    end;
  end;
end;

{ 卸载时清掉开机自启（如果是我们写的）。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WorkBuddyPanel');
  end;
end;
