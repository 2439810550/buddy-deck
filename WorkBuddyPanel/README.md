# WorkBuddy 控制台（WPF）

给 [`workbuddy_api`](../workbuddy_api) 套的一层 Windows 图形界面。不用记命令、不用开终端，
点按钮就能把 WorkBuddy 桌面 App 的模型变成本机的 OpenAI 接口。

**零第三方依赖**：只用 WPF + WinForms 的托盘控件 + .NET 基础库，`PackageReference` 一个都没有。

---

## 核心设计：后台常驻，不占屏幕

这个程序的价值在于**网关一直挂着**，所以它不能要求用户盯着一个大窗口。

| 行为 | 结果 |
|---|---|
| 点窗口右上角 ✕ | **收进系统托盘**，网关继续在后台跑，屏幕上什么都不显示 |
| 双击托盘图标 | 窗口回来 |
| 右键托盘图标 | 打开面板 / 启动网关 / 停止网关 / 开机自启 / 收进托盘 / 退出 |
| 再双击一次 exe | **不会开出第二个**——把已有窗口叫出来，网关不受影响 |
| 托盘菜单「退出」 | 这才是真退出：停网关、关进程 |
| 开机自启 | 用 `--tray` 静默启动：**开机就有 API，全程看不到窗口** |

不想要托盘常驻，去「设置」取消勾选「关闭窗口时收进托盘」，关窗口就等于退出。

---

## 界面：五个标签页

| 标签页 | 干什么 |
|---|---|
| **首页** | 启动 / 停止网关，看登录状态、剩余积分、令牌到期，复制 Base URL 和 API Key，附常见软件的填法 |
| **一键接入** | 自动检测本机装了哪些客户端，一键把 Claude Code / Codex / pi 全配好（配前备份、可一键还原） |
| **账号** | 存多个 WorkBuddy 账号、一键全部签到、选定网关用哪个号的积分、看每个号的签到与额度 |
| **对话** | 直接在界面里跟模型聊天（流式输出，能展开看思考过程），用来验证账号能不能用 |
| **模型** | 表格列出所有可用模型：上下文窗口、最大输出、是否支持图片、积分倍率、推理档位、活动标签 |
| **设置** | node.exe 路径、workbuddy_api 目录、端口、版本（国内版/国际版）、API Key、后台驻留开关 |
| **日志** | 程序和网关的原始输出，出问题了看这里 |

---

## 多账号

桌面 App 一次只能登一个号，登录文件里也就一份凭据。这个页把每个号单独存进保险库，
并按账号分别查状态：

```
~/.workbuddy-api/accounts.cn.json   （0600，里面有 refreshToken）
```

| 你能做的事 | 怎么做 |
|---|---|
| **攒账号** | 点「导入当前账号」把现在登着的号存下来，去 App 里换号、回来再点一次 |
| **选哪个号的积分** | 点「设为使用中」。**切换立即生效，不用重启网关** |
| **续期 + 签到** | 点「续期 + 签到」（或单行点同名按钮）。已签过的不重复请求，令牌过期会先自动刷新，续期窗口不够会自己续 |
| **看状态** | 每个号一行：剩余积分、令牌到期、今天签了没、连续天数、**还能自动续多少天**、本周进度点、本季累计、活动名 |
| **改名 / 删除** | 行内的「重命名」「删除」。删除只删本程序存的副本，不动 App 的登录 |

底部的 `ActiveAccountText` 随时告诉你网关现在在用哪个号（保险库没选定的话就是桌面 App 当前登录的那个）。

### 签到用的是什么接口

从桌面客户端产物（`resources/app.asar`）里扒出来的，不是官方文档：

```
POST {billingBase}/v2/billing/meter/checkin-activity-status   查状态
POST {billingBase}/v2/billing/meter/daily-checkin             签到
```

客户端还会带一个 `X-Device-Token`（腾讯 Turing 风控指纹），**实测不带也能通**，所以本程序不发。

---

## 登录不会过期（自动续期）

这是排错最需要注意的一块，先说清机制：

| | 时长 | 说明 |
|---|---|---|
| `accessToken` | **55 天** | 过期无所谓，能自动刷 |
| `refreshToken` | **60 天** | **唯一决定「会不会掉登录」的东西** |

**关键：服务端给的是滑动窗口。** 刷新响应里带 `refreshExpiresIn = 5184000`（整整 60 天），
而且会换发新的 `refreshToken`：

```
服务端返回: accessToken, expiresIn(4752000), refreshExpiresIn(5184000),
            refreshToken, tokenType, notBeforePolicy, sessionState, scope
```

客服端自己的代码也印证了这点 —— 刷新时整个 token 对象被响应整体替换，
`refreshExpiresAt` 重新计算：

```js
auth = response.data.data;          // 整体替换
auth.lastRefreshTime = Date.now();
this.calculateExpiresAt(auth);
```

### 所以：**在 60 天窗口内刷一次，就又能续 60 天。**

只要不断，就永远不用再扫码。两道保障（默认都开着）：

| 保障 | 怎么工作 |
|---|---|
| **启动时自动检查** | 每次开程序，只要哪个号的续期窗口不足 45 天，就静默跑一次维护 |
| **每日计划任务** | 点「注册每日自动续期」→ Windows 计划任务每天 12:00 跑 `WorkBuddyPanel.exe --maintain`，不弹窗，跑完就退。配了「错过就下次开机补跑」 |

`--maintain` 是免登录、免界面的维护模式：续期 + 签到，完事就退。
跑完会写日志到 `%APPDATA%\WorkBuddyPanel\maintain.log`，计划任务是隐形的，出事全靠它。

### 那什么时候还会掉登录

只有这三种：

1. **超过 60 天一次都没跑过**（电脑长期关机、任务被删、程序被卸）
2. **服务端主动作废会话**（改密码、后台踢下线、安全策略）
3. **号本身被封 / 额度清空**

前一种靠上面两道保障避开；后两种不是客户端能决定的，也只能重新登录。

---

## 一键接入

一个网关可以同时给四个客户端用，但它们说的协议不一样（见 `workbuddy_api` 的 README）。
这个页面就是替你把这件事做了：

| 客户端 | 写哪个文件 | 怎么改 |
|---|---|---|
| **Claude Code** | `~/.claude/settings.json` | 往 `env` 里加 `ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_MODEL` 等。**`theme` 等原有字段不动** |
| **Codex** | `~/.codex/config.toml` + `auth.json` | 顶部设 `model` / `model_provider`，末尾追加一个 `[model_providers.workbuddy]` 表。**你原有的 `custom` provider 不动** |
| **pi** | `~/.pi/agent/models.json` | 新增一个叫 `workbuddy-local` 的供应商（16 个模型全带上下文窗口和图片支持）。**CC Switch 写的那几个不动** |
| **DeepSeek Harness** | — | 不走网关，装 `dsh-workbuddy-connect` 插件即可 |

### 三条安全设计

1. **改之前先备份**。每个文件只在第一次被本程序修改时存一份**原始快照**，
   放在 `%APPDATA%\WorkBuddyPanel\backups\`。“还原”就是把快照拷回去。
   （实测：还原后四个文件与修改前的 sha256 **逐字节一致**。）
2. **只动自己那一块**。Claude Code 的 `theme`、Codex 的其它 provider、pi 的 `cc-switch-*`
   供应商，全都原样保留。
3. **不依赖 CC Switch**。直接改各客户端自己的配置文件，CC Switch 装没装都能用。
   顶部的「打开 CC Switch」按钮只是方便你切回官方账号——切回去之后，
   想再用本网关就再点一次「全部配置」。

### 接入用哪个模型

页面上的下拉框默认**挑 `x0.00` 的免费模型**（这个功能的典型用途就是省额度）。
Claude Code 报的 `claude-*`、Codex 报的 `gpt-*` 在 WorkBuddy 花名册里都不存在，
写在配置里的这个模型就是它们被换成的那个。

> Codex 那行会提示：**会把 `~/.codex/auth.json` 的 `OPENAI_API_KEY` 换成本网关密钥**。
> 你原来的值已经备份了，点「还原」就能恢复。

---

## 怎么跑

### 方式一：直接双击（推荐）

已经发布好了：

```
D:\workfandaiapi\WorkBuddyPanel\publish\WorkBuddyPanel.exe
```

首次打开会自动找 `node.exe` 和 `workbuddy_api` 目录，并读一次账号信息。
路径没找对的话，去「设置」标签页点「自动探测」。

### 方式二：从源码运行

```bash
cd D:\workfandaiapi\WorkBuddyPanel
dotnet run -c Release
```

### 方式三：重新打包成一个 exe

```bash
cd D:\workfandaiapi\WorkBuddyPanel
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## 三步用起来

1. 打开程序（窗口会自己冒出来，或者双击托盘图标）
2. 首页点 **「启动网关」**，等状态变成绿色 **「运行中」** —— 其实默认启动时就自动开了
3. 要让别的软件用，回首页 **复制 Base URL 和 API Key**，填进那个软件的设置里

想让它彻底隐形：设置里勾上 **「开机自动启动」**，以后每次开机网关自动就绪，托盘上一个小圆点，再也不用打开界面。

---

## 命令行参数

| 参数 | 作用 |
|---|---|
| （无） | 正常启动，窗口显示出来 |
| `--tray` | 静默启动：只进托盘，不弹窗口，自动拉起网关。开机自启用的是这个 |
| `--maintain` | 无人值守维护：续期 + 签到，跑完就退，不弹窗、不启网关。每日计划任务用的是这个 |

两种情况下都会先做单实例检查：已有实例在跑就把它的窗口叫出来，自己不重复开。

---

## 程序在背后做了什么

它**不重新实现** WorkBuddy 的协议，只是管理那个 Node 网关：

```
WorkBuddyPanel.exe
   │
   ├─ 启动时：跑 `node src/bin.js status --json` / `models --json` 读账号和模型
   │
   ├─ 启动网关：把 `node src/bin.js serve --port … --api-key …`
   │             拉成子进程，解析它打印的 base_url
   │
   ├─ 聊天：POST http://127.0.0.1:<端口>/v1/chat/completions，
   │        读 SSE 流，逐段渲染到界面上（纯 C#，不走命令行）
   │
   └─ 退出：把 node 子进程一起收掉，不留孤儿进程占端口
```

**唯一的例外是强制结束**：用任务管理器杀进程时，node 会留下来占着 8787。
这时下次启动会明确提示「端口被占用」，去「设置」把端口改成 8788 即可。

---

### 换台电脑继续用

**token 是服务端的，跟机器无关** —— 所以导出拷过去就能接着刷新，不用重新扫码。

| 步骤 | 做什么 |
|---|---|
| 1 | 老机器：「账号」页点 **「导出账号」** → 设个口令（至少 8 位，输两遍）→ 存成文件 |
| 2 | 把文件拷到新机器（U 盘 / 局域网 / 自己传给自己） |
| 3 | 新机器：「账号」页点 **「导入账号」** → 选文件 → 输同一个口令 |
| 4 | **导入完把文件删掉** |

导入不是“写进去就算完”——它会**对每个账号真的刷一次令牌**，能刷通才叫接上了。
结果逐条显示：`✔ 主号：可以继续使用，还能自动续 59 天`。

### 导出文件是加密的

```
scrypt(N=16384, r=8, p=1) 派生密钥  →  AES-256-GCM 认证加密
```

文件里只有 `kdf` / `salt` / `iv` / `tag` / `payload`，**搜不到任何 token 明文**。
GCM 带认证标签，所以口令输错、文件被改过、传输截断——都会被拦下来，
不会解出一堆乱码还接着跑。

> **这个文件等同于账号密码。**
> 里面有 refreshToken，而且服务端是滑动窗口，能一直续下去。
> 拿到文件 + 口令的人 = 拿到了这些号的额度。
> 口令忘了就解不开，只能重新扫码。

### 换机器后还会遇到的事

1. **记得再点一次「一键接入 → 全部配置」**。客户端配置指向的是 `127.0.0.1`，
   新机器上得重新写一遍（走 10 秒）。
2. **可能碰一下风控**。客户端代码里有 `RESPONSE_CODE_IP_LIMIT`，
   同一个号在新 IP 上首次刷新被拦是可能的。导入失败的那些号重新登录一次就好。
3. **两台机器可以同时用同一个号**。实测服务端允许同一个号存在多个有效的 refreshToken，
   保险库和桌面 App、以及两台机器之间，不会互相踢下线。

### 命令行也行（保底路径）

```bash
cd workbuddy_api
node src/bin.js accounts export --out D:\acc.json     # 口令提示在终端里输
node src/bin.js accounts import D:\acc.json
```

口令走 stdin，不进命令行参数 —— 否则它会出现在进程列表里，机器上任何进程都看得到。

---

## 项目结构

```
WorkBuddyPanel/
├── WorkBuddyPanel.csproj
├── build.ps1                       打成免安装包（自包含 .NET + 便携 Node + api 源码）
├── App.xaml / App.xaml.cs          程序入口、单实例、全局样式、未处理异常兜底
├── MainWindow.xaml                 全部界面（六个标签页）
├── MainWindow.xaml.cs              收进托盘、滚动到底、回车发送、文件对话框
├── TrayIcon.cs                     系统托盘图标 + 右键菜单（图标是运行时画出来的）
├── Models/Dtos.cs                  JSON 数据结构
├── Converters/Converters.cs        几个 Visibility 转换器
├── Services/
│   ├── AppSettings.cs              %APPDATA%\WorkBuddyPanel\settings.json（读坏会备份）
│   ├── AutoStart.cs                开机自启（注册表 Run 键）
│   ├── ClientIntegrator.cs         四个客户端的配置读写 + 备份 + 还原
│   ├── ScheduledTask.cs            注册/删除每日自动续期的 Windows 计划任务
│   ├── ProcessLocator.cs           自动找 node.exe 和 workbuddy_api 目录
│   ├── CliRunner.cs                跑一次性命令、抠 JSON、探测端口占用
│   ├── GatewayRunner.cs            管理 `node serve` 子进程
│   └── ChatStreamClient.cs         SSE 流式对话客户端
└── ViewModels/
    ├── ObservableObject.cs         INotifyPropertyChanged 基类
    ├── RelayCommand.cs             同步 / 异步 ICommand
    ├── MainViewModel.cs            全部状态和命令
    ├── MainViewModel.Clients.cs    「一键接入」页的那部分
    └── MainViewModel.Accounts.cs   「账号」页的那部分
```

---

## 配置存在哪

| 文件 | 内容 |
|---|---|
| `%APPDATA%\WorkBuddyPanel\settings.json` | 本程序自己的设置（路径、端口、密钥、托盘开关） |
| `%APPDATA%\WorkBuddyPanel\settings.json.broken` | 配置读坏时的自动备份（正常情况下不存在） |
| `%APPDATA%\WorkBuddyPanel\backups\` | 「一键接入」改动前的客户端配置原始快照 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WorkBuddyPanel` | 开机自启项（勾了才有） |
| `C:\Users\<你>\.workbuddy-api\` | workbuddy_api 自己的状态目录，本程序不碰 |

**手工改 `settings.json` 时小心反斜杠**：JSON 里路径要写成 `"D:\\nodejs\\node.exe"`（双反斜杠）。
写错了程序不会崩，但会回退到默认设置，并在首页弹一张警告卡告诉你原文件备份在哪。

---

## 已知限制

- 只在 Windows 上跑（WPF）。
- 需要 .NET 10 桌面运行时（本机已装：`Microsoft.WindowsDesktop.App 10.0.3`）。
- 需要 `node.exe` 在 PATH 里，或在设置里指定。
- 「对话」标签页是纯文字，**没做发图**——虽然模型支持图片，但要在界面里做粘贴/拖拽上传才能用。
- 「一键接入」里 **DSH 只做检测，不自动装插件**——那要跑 `dsh plugin add`、联网拉包，交给用户手动确认更合适。
- **Codex 没做客户端级验证**：本机 PATH 里没有 `codex` CLI（装的是 Codex++ / Codex-X 两个 GUI 壳），
  所以 Codex 达到了「配置写对了、网关那侧协议结构全过」，但没跑过真 Codex。
- 国际版（WorkBuddy AI）能切，但 Windows 上读不到它的 App 版本号，模型列表会退回内置清单（这是 `workbuddy_api` 的已知限制，不是本程序的）。
- 界面上的「推理档位检测」没做——那个要发探测请求、消耗积分，`workbuddy_api` 里也没暴露成命令行。

---

## 验证记录

在 Windows + 国内版 WorkBuddy 5.6.2 上实测（用 UI Automation 驱动界面，非人工目测）：

| # | 测试 | 结果 |
|---|---|---|
| 1 | `dotnet build` / `dotnet publish` | 零警告零错误 |
| 2 | 启动自动探测 + 读账号 | 已登录 / 积分 3629 / 16 个模型 / 列表来源「上游实时」 |
| 3 | 点「启动网关」 | node 子进程拉起，Base URL = `http://127.0.0.1:8787/v1` |
| 4 | 界面内对话 | 问「1+1等于几」→ 流式返回 `2` |
| 5 | **关窗口（默认设置）** | 进程存活，可见窗口 = 0，网关仍在监听 ✓ |
| 6 | **关窗口后再次双击 exe** | 进程数仍为 1，窗口被叫回来，网关未中断 ✓ |
| 7 | **`--tray` 静默启动** | 可见窗口 = 0，网关已自动就绪 ✓ |
| 8 | **取消「收进托盘」后关窗口** | 进程退出，网关停止，端口释放 ✓ |
| 9 | **开机自启开关** | 勾选 → 注册表写入 `"…WorkBuddyPanel.exe" --tray`；取消 → 条目移除 ✓ |
| 10 | **配置损坏** | 首页显示警告卡，原文件备份为 `settings.json.broken`，未被覆盖 ✓ |
| 11 | 模型标签页 | DataGrid 渲染 16 行，倍率 / 窗口 / 档位 / 活动标签均正确 |
| 12 | 发布版单文件 exe | `--tray` 启动同样通过 |
| 13 | **「一键接入」检测** | 正确识别 Claude Code / Codex / pi，DSH 标为未装 || 14 | **「全部配置」写入** | 三个客户端的 4 个文件全部写入；原有 `theme` / `custom` provider / `cc-switch-*` 供应商均保留 |
| 15 | **Claude Code 零配置实跑** | 不带任何 `ANTHROPIC_*` 环境变量，直接 `claude -p` → 正确返回 |
| 16 | **接入模型默认值** | 默认选中 `Hy3 · x0.00`（免费），写进配置的是 `hy3` |
| 17 | **「还原所有备份」** | 四个文件与修改前 **sha256 逐字节一致** |
| 18 | **多账号导入** | 导入到 2 个真实账号（uid 不同），各自积分 / 签到状态独立显示 |
| 19 | **账号切换** | 点「设为使用中」→ 保险库 activeId 变更 → **网关不重启即改用新账号**，请求正常 |
| 20 | **批量签到** | 「小号」从 2000 → **2100 积分**（+100），未签 → 已签连续1天；「主号」已签被正确跳过 |
| 21 | **滑动窗口确认** | 刷新响应带回 `refreshExpiresIn = 5184000`（60 天）+ 新 `refreshToken`；服务端允许旧 refreshToken 共存，两边不互踢 |
| 22 | **`--maintain` 免登录模式** | 不弹窗、不启网关，跑完续期+签到自动退出；结果写入 `maintain.log` |
| 23 | **每日计划任务** | 注册成功（`schtasks` 可查，下次 12:00）；**实测把两个号的续期窗口篡改成「已失效」后触发任务，两个号都被自动续回 59 天** |
| 24 | **加密导出** | scrypt + AES-256-GCM；文件里**搜不到任何 token 明文**；口令 < 8 位被拒；错口令被认证标签拦下 |
| 25 | **跨机导入** | 把保险库搬走模拟新机器 → 导入 → **每个号真的刷了一次** → 都能用；网关三种协议全跑通 |
| 26 | 界面导出流程 | 点「导出账号」→ 口令弹窗（两框 + 告警）→ 确定 → 保存对话框弹出且默认文件名正确 |

第 10 项是测试过程中意外发现的真实缺陷：原实现读不出配置时会静默回退默认值，
**并把用户原配置直接覆写掉**。现已改成先备份再回退，并在首页明确告警。

未实测（无法在无头环境驱动）：托盘右键菜单的点击。其中的每个动作
（打开面板 / 启停网关 / 退出 / 切换开关）都走与其他入口相同的代码路径，已在 5–9 项覆盖。

「账号」页的「导入当前账号」「重命名」「删除」三个动作会弹模态对话框，
已验过弹窗能正常弹出/输入/确认、导入与重命名生效，但没做全自动回归。

**跨机导出/导入的界面路径没跑完最后一步**：原生保存/打开对话框的最终点击
（我驱动系统对话框时 UIA 定位总撞车）。点完弹窗之后跑的代码，
和上面第 24/25 项里被验穿的那条命令行路径是同一段。
