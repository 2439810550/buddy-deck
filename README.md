# BuddyDeck

**把 WorkBuddy 桌面 App 里的账号，变成你自己的本地 AI 网关。**

多账号、自动签到、自动续期、四种协议、Windows 图形界面 —— 目标机器上什么都不用装。

```
Claude Code ─┐
Codex       ─┼─► 127.0.0.1:8787 ─► 你选的那个 WorkBuddy 账号
pi          ─┘
```

---

## 它能做什么

| | |
|---|---|
| **一个网关，四种协议** | 同一份账号同时给 Claude Code（Anthropic 协议）、Codex（Responses 协议）、pi / Cline / Aider（OpenAI Chat 协议）、DeepSeek Harness 用 |
| **多账号** | 同时存好几个 WorkBuddy 号，随时切换用哪个号的积分。**切换不用重启网关** |
| **自动签到** | 一键给所有号签到，已签过的自动跳过。实测每个号每天 +100 积分 |
| **登录不会过期** | token 是 60 天滑动窗口，每次刷新重新给满额 —— 只要不断就永远不用再扫码。自带每日计划任务 |
| **跨机搬运** | 账号打包成加密文件，新电脑导入即用，不用重新扫码 |
| **图形界面 + 托盘** | 关窗口收进托盘、网关继续跑；可开机自启、静默进托盘 |

---

## 三块东西

| 目录 | 是什么 | 在本仓库里？ |
|---|---|---|
| **`WorkBuddyPanel/`** | Windows 桌面端（C# / WPF）。界面、托盘、账号管理、一键接客户端、每日维护。零第三方依赖 | ✅ |
| **`workbuddy_api/`** | 网关本体（Node.js）。认证、上游协议、协议翻译。零第三方依赖 | ✅ |
| **`dsh-workbuddy-connect/`** | DeepSeek Harness 的插件（上游参照实现） | ❌ 别人的仓库，见下方链接 |

分工：`WorkBuddyPanel` 管界面和生命周期，`workbuddy_api` 管协议和凭据。
面板不重写任何协议 —— 它只是把网关拉起来、读写配置文件、把状态显示出来。

---

## 快速开始

```bash
# 1. 起网关（需要 Node 20.10+）
cd workbuddy_api
node src/bin.js status        # 确认读到了登录态
node src/bin.js serve         # 打印 base_url 和 api_key

# 2. 或者直接用界面（需要 .NET 10；publish 版自带运行时）
cd WorkBuddyPanel
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

界面里点「启动网关」→ 首页会显示 Base URL 和 API Key → 复制给任何 OpenAI 兼容客户端。

---

## 四个客户端怎么接

界面「**一键接入**」页会自动写好配置，配之前备份、随时能还原。

| 客户端 | 协议 | 端点 |
|---|---|---|
| pi / Cline / Roo / Aider / 任何「OpenAI 兼容」 | OpenAI Chat Completions | `POST /v1/chat/completions` |
| Claude Code | Anthropic Messages | `POST /v1/messages` |
| Codex（`wire_api = "responses"`） | OpenAI Responses | `POST /v1/responses` |
| DeepSeek Harness | DSH provider | 装 `dsh-workbuddy-connect` 插件，不走本网关 |

手动配也都是一行：

```bash
# Claude Code
set ANTHROPIC_BASE_URL=http://127.0.0.1:8787
set ANTHROPIC_AUTH_TOKEN=<serve 打印的 api_key>
```

模型名会自动对齐：Claude Code 报的 `claude-*`、Codex 报的 `gpt-*` 都不在 WorkBuddy 花名册里，
网关会换成你指定的默认模型（不指定就挑 `x0.00` 的免费模型），并在日志里写明。

---

## 账号：多开、签到、续期、搬家

```bash
cd workbuddy_api

node src/bin.js accounts add --label 主号    # 把桌面 App 当前登录的号存进保险库
node src/bin.js accounts                     # 看每个号的积分 / 签到 / 还能自动续多久
node src/bin.js accounts use 346e            # 网关立即改用这个号
node src/bin.js checkin                      # 续期 + 签到（全部账号）

node src/bin.js accounts export --out D:\acc.json   # 加密导出，跨机搬运
node src/bin.js accounts import D:\acc.json
```

界面「**账号**」页有对应的按钮，另外能一键注册 **Windows 每日计划任务**：
每天跑一次 `WorkBuddyPanel.exe --maintain`（不弹窗），续期 + 签到。

### 为什么登录不会过期

服务端给的是**滑动窗口**：accessToken 55 天、refreshToken 60 天，
但**每次刷新都会重新发一个满额 60 天的 refreshToken**：

```
服务端刷新响应: accessToken, expiresIn(4752000), refreshExpiresIn(5184000), refreshToken, ...
```

所以在窗口内刷一次就又能续 60 天。只要定期跑，就永远不用再扫码。

---

## 验证状态

| | |
|---|---|
| `workbuddy_api` 自带回归 | **39 项全绿**（`node scripts/verify.mjs`） |
| 三种协议结构校验 | **38 项**（含流式事件序列、tool_use 往返、多轮回传） |
| Claude Code 真 CLI | 打通，含工具调用 |
| 多账号 / 签到 / 续期 / 跨机搬运 | 都在真实账号上端到端跑过 |
| 每日计划任务 | 实测把续期窗口篡改成「已失效」后触发，两个号都被自动续回 |

详细清单在各子项目的 README 里。

---

## 目录结构

```
buddy-deck/
├── WorkBuddyPanel/          Windows 桌面端（C# / WPF）
│   ├── Services/            网关子进程、客户端配置读写、计划任务、账号迁移
│   ├── ViewModels/          全部状态与命令
│   ├── PasswordInputDialog.xaml  口令弹窗（导出/导入用）
│   └── build.ps1            打成免安装包
├── workbuddy_api/           网关（Node.js）
│   ├── src/anthropic.js     Anthropic Messages ←→ Chat
│   ├── src/responses.js     OpenAI Responses  ←→ Chat
│   ├── src/accounts.js      多账号保险库
│   ├── src/checkin.js       每日签到
│   ├── src/transfer.js      跨机搬运（加密）
│   └── scripts/verify.mjs   39 项自带回归
└── 调用示例.py              调网关的最小 Python 示例
```

第三方项目不在本仓库：
[corrinehu/dsh-workbuddy-connect](https://github.com/corrinehu/dsh-workbuddy-connect)。

### 什么不会被提交

`.gitignore` 挡掉了：构建产物（`bin` / `obj` / `publish`）、
**账号保险库与所有凭据文件**（`accounts*.json` / `auth*.json` / `*.info`）、
本地设置、以及那个第三方目录。
仓库里只有源码和文档，0.6 MB。

---

## 环境要求

- **Node.js** 20.10+ / 22 / 24（`workbuddy_api` 用它跑）
- **.NET 10** 桌面运行时（`WorkBuddyPanel` 用它；`publish` 出来的自包含版不需要）
- Windows（面板是 WPF；网关本身跨平台）
- **已登录的 WorkBuddy 桌面 App**（国内版 / 国际版都行）

---

## 致谢

- **`workbuddy_api/`** —— 由**我的朋友**独立实现。
  它把 WorkBuddy 桌面 App 的登录态接出来、变成标准的 OpenAI 接口，
  并处理了协议层的所有坑（加密凭据解密、token 刷新、上游 UA 构形、流式强制）。
  本项目的多账号、签到、滑动窗口续期、跨机搬运和三个协议适配，都建在这份工作之上。
  他的名字/主页：**（待补）**
- [corrinehu/dsh-workbuddy-connect](https://github.com/corrinehu/dsh-workbuddy-connect)（MIT）
  —— 认证链路与上游协议的**参照实现**，`workbuddy_api` 的协议细节大量来自它。
- [Sliverkiss/workbuddy2api](https://github.com/Sliverkiss/workbuddy2api)（MIT）
  —— WorkBuddy 上游协议的更早参照。

---

## 免责声明

- 本项目**仅供个人学习和研究使用**，只驱动使用者自己的 WorkBuddy 账号在本机调用。
- 依赖 WorkBuddy 客户端的**非公开接口**，上游改动可能导致失效。
- 因使用本项目产生的任何后果（包括但不限于账号被限制、额度被清空、服务中断）由使用者自行承担。
- 本项目与腾讯、WorkBuddy、DeepSeek 均无关联，未获其授权或认可；
  文中出现的名称仅用于描述兼容关系，商标权利归各自所有。

## 许可证

MIT
