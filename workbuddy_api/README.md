# workbuddy-api

**把 WorkBuddy 桌面 App 的登录态接出来，变成一个标准的 OpenAI 接口，给任何 CLI / 编辑器 / SDK 用。**

不依赖 DSH（DeepSeek Harness），不依赖 WorkBuddy 的任何 SDK，**零第三方运行时依赖**——只用 Node 内置模块。

```bash
node src/bin.js serve
# base_url : http://127.0.0.1:8787/v1
# api_key  : <自动生成>
```

然后任何支持 OpenAI `base_url` 的工具都能直接用上 WorkBuddy 里的模型（GLM-5.3、DeepSeek-V4-Pro、Kimi-K3、MiniMax-M3、Hy3 ……）。

---

## 它到底做了什么

WorkBuddy 桌面 App 登录后，会把凭据写在自己目录下的一个文件里。本工具：

1. **只读**打开那个文件，拿到 `accessToken` / `refreshToken`；
   若 App 是 **5.6+**（凭据已加密存储），会用 App 自己的二进制取回
   at-rest 密钥并在内存中解密，**不落盘、不打日志**；
2. 需要时用 `refreshToken` 走官方刷新接口换新 token，**把刷新结果写进自己的目录**，绝不改动 App 的文件；
3. 把请求按 WorkBuddy 上游的私有协议打包（客户端标识、专用请求头、流式强制等），转发到上游；
4. 在本地起一个 OpenAI 兼容的 HTTP 服务，把上游的流式响应原样或用标准格式交给调用方。

App 里换了账号 / 重新登录，本工具自动跟随，不用重配。国内版 **WorkBuddy** 与国际版 **WorkBuddy AI** 都支持，各自独立。

```
你的 CLI ──OpenAI 协议──▶ 127.0.0.1:8787 ──WorkBuddy 私有协议──▶ 上游
                              ▲
                              └── 读 App 的登录文件（只读，必要时解密）
                                  + 自己缓存的刷新结果
```

---

## 文档

| 文档 | 内容 |
|---|---|
| **README.md**（本文） | 安装、接入、命令、常见问题 |
| [`docs/ARCHITECTURE.md`](./docs/ARCHITECTURE.md) | 模块地图、依赖方向、数据流；改代码前必读 |
| [`docs/UPSTREAM.md`](./docs/UPSTREAM.md) | 上游协议契约：端点、请求头、错误码、协议坑位 |
| [`docs/MAINTENANCE.md`](./docs/MAINTENANCE.md) | 维护手册：与 DSH 插件同步流程、验证清单、同步历史 |

维护者请从 `docs/MAINTENANCE.md` 开始。

---

## 前置条件

- **Node.js 20.10+ / 22 / 24**（用到 `AbortSignal.any`、`fetch`）
- 已安装并**登录** WorkBuddy 桌面 App（国内版或国际版其一即可）
  - App **5.6+** 的加密凭据需要能访问 App 自己的可执行文件（自动探测；
    探测不到时用 `WORKBUDDY_ELECTRON_BIN` 指定）

验证环境：

```bash
node src/bin.js doctor
```

---

## 快速开始

```bash
git clone <this-repo> workbuddy-api
cd workbuddy-api

node src/bin.js doctor       # 检查登录态与环境
node src/bin.js models       # 看当前可用模型
node src/bin.js serve        # 起服务（前台运行，Ctrl+C 停止）
```

`serve` 启动时会打印 base_url 与 api_key，直接复制给客户端用。

### 常用启动参数

| 参数 | 说明 |
|---|---|
| `--port <n>` | 监听端口，默认 `8787`；`0` 表示由系统分配 |
| `--host <h>` | 绑定网卡，默认 `127.0.0.1`；绑非回环地址时**强制要求** api_key |
| `--api-key <k>` | 指定固定密钥（默认每次启动随机生成） |
| `--no-auth` | 不校验密钥（**仅允许**绑定回环地址时使用） |
| `--provider workbuddy-ai` | 使用国际版 WorkBuddy AI 的账号 |
| `--quiet` | 不打印每次请求的日志 |

---

## 一个网关，四种协议

网关对外同时讲三种 HTTP 协议，所以同一份账号能同时给四个客户端用：

| 客户端 | 说哪种协议 | 走哪个端点 |
|---|---|---|
| **pi** / Cline / Roo / Aider / 一切「OpenAI 兼容」 | OpenAI Chat Completions | `POST /v1/chat/completions` |
| **Claude Code**（可直接配，也可经 CC Switch） | Anthropic Messages | `POST /v1/messages` |
| **Codex**（`wire_api = "responses"`） | OpenAI Responses | `POST /v1/responses` |
| **DeepSeek Harness** | DSH provider | 用 [`dsh-workbuddy-connect`](https://github.com/corrinehu/dsh-workbuddy-connect) 插件，不走本网关 |

后两种在网关内部翻译成 Chat Completions，再交给上游，新增三个模块：

```
src/protocol.js    模型别名解析、token 估算、SSE 写出、stop_reason 映射
src/anthropic.js   Anthropic Messages ←→ Chat（含 tool_use 双向、count_tokens）
src/responses.js   OpenAI Responses ←→ Chat（含 output item 的事件流顺序）
```

### 模型别名（重要）

Claude Code 会报 `claude-sonnet-4-5-20250929`，Codex 会报 `gpt-5.6-sol`，
这两个名字在 WorkBuddy 的花名册里都不存在。解析顺序：

1. 花名册里有的 → 原样用（pi 可以直接填 `glm-5.3`）
2. 带前缀的写法会规整（`workbuddy/glm-5.3` → `glm-5.3`）
3. 都不是 → 换成默认模型（**优先选 `x0.00` 的免费模型**），并在日志里写明

想固定用某个模型：

```bash
node src/bin.js config --set defaultModel=glm-5.3
# 或临时指定
node src/bin.js serve --default-model glm-5.3
# 或环境变量
set WORKBUDDY_DEFAULT_MODEL=glm-5.3
```

### Claude Code

```bash
set ANTHROPIC_BASE_URL=http://127.0.0.1:8787
set ANTHROPIC_AUTH_TOKEN=<serve 打印的 api_key>
claude
```

> 用 `ANTHROPIC_AUTH_TOKEN`（会发 `Authorization: Bearer`），
> **不要**用 `ANTHROPIC_API_KEY`（它发的是 `x-api-key`，本网关不校验那个头）。
>
> Claude Code 对不认识的模型名会提一句自动压缩窗口的告警，"\n> `set CLAUDE_CODE_MAX_CONTEXT_TOKENS=1000000` 或在模型名后加 `[1m]` 可以消掉。

### Codex

`~/.codex/config.toml`：

```toml
model = "glm-5.3"
model_provider = "workbuddy"

[model_providers.workbuddy]
name = "WorkBuddy"
base_url = "http://127.0.0.1:8787/v1"
wire_api = "responses"     # 也可以是 "chat"，两条路本网关都支持
```

密钥写到 `~/.codex/auth.json` 的 `OPENAI_API_KEY` 字段。

### 自动转向本网关的坑位

**Claude Code 的 system 里有一行客户端指纹**，上游拿它做渠道识别，
命中就回 `400 / code 11128 · Illegal API invocation from an unapproved channel`：

```
x-anthropic-billing-header: cc_version=2.1.250.7b8; cc_entrypoint=sdk-cli;
```

`src/anthropic.js` 的 `stripClientFingerprint()` 会把这行剔掉。
实测（逐块逐行二分）：整份 Claude Code 系统提示词里**只有这一行**会触发，
它是纯遥测元数据，不含任何提示词语义。
**改适配层时别把它删了**，删了 Claude Code 就又进不来了。

---

## 接各种客户端

### OpenAI SDK（Python / Node）

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:8787/v1", api_key="<serve 打印的 api_key>")

resp = client.chat.completions.create(
    model="deepseek-v4.1-flash",
    messages=[{"role": "user", "content": "你好"}],
    stream=True,          # 流式、非流式都支持
)
for chunk in resp:
    print(chunk.choices[0].delta.content or "", end="")
```

工具调用（function calling）同样可用，流式与非流式都返回标准 `tool_calls` 结构。

### 环境变量通用接法

绝大多数 CLI 认这几个环境变量，不用改配置：

```bash
export OPENAI_BASE_URL=http://127.0.0.1:8787/v1
export OPENAI_API_KEY=<serve 打印的 api_key>
```

Windows PowerShell：

```powershell
$env:OPENAI_BASE_URL = "http://127.0.0.1:8787/v1"
$env:OPENAI_API_KEY  = "<serve 打印的 api_key>"
```

### 各家工具的具体写法

不同工具的环境变量名不一致，下面按常见工具给出对应变量（base_url 都填
`http://127.0.0.1:8787/v1`）：

**Aider**

```bash
export OPENAI_API_BASE=http://127.0.0.1:8787/v1
export OPENAI_API_KEY=<key>
aider --model openai/deepseek-v4.1-flash
```

**Continue / Cline / Roo 等 VS Code 插件** — 在设置里把 provider 选成
"OpenAI Compatible"，Base URL 填 `http://127.0.0.1:8787/v1`，API Key 填生成的密钥，
模型名填 `models` 命令列出的 id。

**Cursor** — Settings → Models → 覆盖 OpenAI Base URL，填入同一地址；
注意 Cursor 部分功能会强制走它自己的后端，自定义 base_url 的可用范围以它当前版本为准。

**任意自写脚本** — 就是普通 HTTP：

```bash
curl http://127.0.0.1:8787/v1/chat/completions \
  -H "Authorization: Bearer <key>" \
  -H "Content-Type: application/json" \
  -d '{"model":"glm-5.3","messages":[{"role":"user","content":"你好"}],"stream":false}'
```

> 提示：工具如果**只**支持填一家厂商的 base_url、且会改写路径，那么指向本地服务通常不可行——
> 这种情况请确认该工具支持 "OpenAI Compatible / Custom Provider"。

---

## 命令一览

```
workbuddy-api <serve|status|models|doctor|logout|config> [选项]
```

| 命令 | 作用 |
|---|---|
| `serve` | 启动 OpenAI 兼容网关（主命令） |
| `status` | 登录账号、token 到期时间、剩余积分、模型列表来源 |
| `models` | 模型清单（含积分倍率、是否支持图片、上下文窗口） |
| `doctor` | 无敏感信息的诊断：登录文件位置、App 版本、目录权限 |
| `accounts` | 多账号：列出 / `add` / `use <id>` / `clear` / `remove <id>` / `rename <id> <名>` / `renew [id]` / `export` / `import <file>` |
| `checkin` | 给保险库里的账号签到（默认全部，或 `--id <id>`） |
| `logout` | **只**删除本工具自己的凭据副本，不动 App 的登录 |
| `config` | 读写持久化配置 |

加 `--json` 可输出机器可读格式（`status` / `models` / `doctor` / `accounts` / `checkin`）。

### 多账号

桌面 App 一次只能登一个号，登录文件里也就一份凭据。想把几个号同时挂着：

```bash
node src/bin.js accounts                    # 看每个号的积分、签到、还能自动续多少天
node src/bin.js accounts add --label 主号   # 把桌面 App 现在登着的号存进保险库
# （去 App 里换成第二个号，再跑一次）
node src/bin.js accounts add --label 小号
node src/bin.js accounts use 346e           # 写前几位就行；网关立即改用这个号
node src/bin.js accounts clear              # 取消选定，退回桌面 App 那个号
node src/bin.js accounts renew              # 续期（窗口不足 45 天才会真刷）
node src/bin.js accounts renew --force      # 无条件刷一遍
node src/bin.js checkin                     # 续期 + 签到（默认全部账号）
node src/bin.js checkin --id 346e           # 只处理某一个
node src/bin.js accounts export --out D:\acc.json   # 导出成加密文件（跨机搬运）
node src/bin.js accounts import D:\acc.json          # 在另一台机器导入
```

#### 跨机搬运

**token 是服务端的，跟机器无关** —— 导出拷过去就能接着刷新，不用重新扫码。

```bash
# 老机器：导出口令提示在终端里输（至少 8 位，输两遍）
node src/bin.js accounts export --out D:\acc.json

# 新机器：导入会自动对每个号真刷一次，能刷通才叫接上了
node src/bin.js accounts import D:\acc.json
# ✔ 主号    可以继续使用，还能自动续 59 天
# ✔ 少钦号  可以继续使用，还能自动续 59 天
```

文件是 `scrypt` 派生密钥 + `AES-256-GCM` 认证加密，只能看到
`kdf / salt / iv / tag / payload` 五个字段，**搜不到任何 token 明文**。
口令错、文件被改、传输截断都会被认证标签拦下。

> **这个文件等同于账号密码。** 里面有 refreshToken，而且窗口是滑动的、能一直续。
> 导入完立刻删掉，别留在微信/网盘里。口令忘了只能重新扫码。

口令走 stdin 而不进命令行参数 —— 参数会出现在进程列表里。
`--pass <口令>` 也支持，但那是给脚本用的，会泄露。

可能碰到的情况：客户端代码里有 `RESPONSE_CODE_IP_LIMIT`，
同一个号在新 IP 上首次刷新被拦是可能的；导入会把每个号的验证结果逐条报出来。
实测服务端允许同一个号存在多个有效 refreshToken，两台机器可以同时用。

保险库在 `~/.workbuddy-api/accounts.cn.json`（权限 `0600`，里面有 refreshToken）。
**切换账号不需要重启网关** —— 保险库每个请求都重新读文件。

#### 登录为什么不会过期

| | 时长 |
|---|---|
| `accessToken` | 55 天 |
| `refreshToken` | 60 天 |

`refreshToken` 的 60 天是**滑动窗口**：刷新响应里带 `refreshExpiresIn = 5184000`，
每次都重新给满额，并换发新的 `refreshToken`。所以在窗口内刷一次就又能续 60 天。

实测（拿一个已被客户端换发过的旧 refreshToken 去刷）**照样能成功**，
说明服务端允许同一个号存在多个有效 refreshToken，不会与桌面 App 互踢。

两道保障：`checkin` 内部会先续期再签；启动时也会检查窗口。
真正重要的是**定期**跑 —— 配套的 WPF 界面能注册一个每日计划任务，
跑的就是 `WorkBuddyPanel.exe --maintain`。

### 每日签到

```
POST {billingBase}/v2/billing/meter/checkin-activity-status   查状态
POST {billingBase}/v2/billing/meter/daily-checkin             签到
```

接口是从桌面客户端产物里扒出来的，不是官方文档。客户端还会带一个 `X-Device-Token`
（腾讯 Turing 风控指纹），**实测不带也能通**，所以本工具不发——拿不到的东西不伪造。

### 持久化配置

```bash
node src/bin.js config                          # 查看
node src/bin.js config --set port=8787          # 改端口
node src/bin.js config --set apiKey=sk-fixed    # 固定密钥，省得每次抄
node src/bin.js config --set provider=workbuddy-ai
node src/bin.js config --set authFile="D:\path\to\workbuddy-desktop.info"
```

配置存在 `~/.workbuddy-api/config.json`。**优先级**：命令行参数 > 环境变量 > 配置文件 > 内置默认值。

---

## 环境变量

| 变量 | 作用 |
|---|---|
| `WORKBUDDY_API_HOME` | 改本工具的状态目录（默认 `~/.workbuddy-api`） |
| `WORKBUDDY_AUTH_FILE` | 国内版 App 登录文件的显式路径 |
| `WORKBUDDY_AI_AUTH_FILE` | 国际版 App 登录文件的显式路径 |
| `WORKBUDDY_ELECTRON_BIN` | App 5.6+ 加密凭据的 key helper 二进制（探测不到时指定） |

登录文件的默认探测位置（按顺序）：

- **Windows**
  - `%LOCALAPPDATA%\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info`
  - `%APPDATA%\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info`
- **macOS**
  - `~/Library/Application Support/CodeBuddyExtension/Data/Public/auth/workbuddy-desktop.info`
- **Linux / WSL**
  - `~/.config/CodeBuddyExtension/Data/Public/auth/workbuddy-desktop.info`
  - WSL 下还会先探测挂载进来的 Windows 用户目录

国际版只是把文件名换成 `workbuddy-desktop-ai.info`。

找不到时用 `WORKBUDDY_AUTH_FILE` 显式指定即可。

---

## 登录态是怎么维护的

- **读**：每次请求都看 App 的登录文件与工具自己的副本，取**更晚过期**的那个。
  所以你在 App 里重新登录后，本工具下一次请求就会用新账号。
- **解密**：App **5.6+** 把 token 加密存储。本工具会用 App 自己的可执行文件
  跑一次 Node 助手取回 at-rest 密钥（`ELECTRON_RUN_AS_NODE=1`），在**内存中**
  用 AES-256-GCM 解密，进程退出即消失——不写盘、不打日志。密钥按 key id 缓存，
  同一次运行只 spawn 一次。只解密 token 字段，不动 `nickname` 等个人信息。
- **刷新**：access token 快过期（默认提前 5 分钟）时，用 refresh token 请求
  官方刷新接口，新 token 写进 `~/.workbuddy-api/auth.cn.json`（权限 `0600`）。
  并发请求共享同一次刷新，不会打出多个刷新。
- **不写 App 文件**：任何情况下都不会修改 WorkBuddy 自己的登录文件。
- **刷新失败不等于立刻不可用**：只要旧 token 还没过期，请求照常发出去。

状态目录结构：

```
~/.workbuddy-api/
├── config.json          # 你的配置
├── auth.cn.json         # 刷新后的凭据副本（0600）
├── catalog.workbuddy.json    # 上次成功的模型列表缓存
└── workbuddy-api.log    # 预留
```

`logout` 只删 `auth.*.json`；`rm -rf ~/.workbuddy-api` 可以彻底清理。

---

## 已实测的行为

在本机（Windows + 国内版 WorkBuddy **5.6.2**，凭据为**加密**格式）对真实账号验证过：

- `status` / `models` / `doctor` 正确读取登录态、剩余积分与 16 个模型
- **5.6+ 加密凭据解密**：从 App 二进制取 at-rest 密钥，AES-256-GCM 解开
  `accessToken` / `refreshToken`，并确认 App 的文件未被改动
- `POST /v1/chat/completions` 流式输出，`data: [DONE]` 正常收尾
- 非流式请求：本地把流式响应重新组装成 `chat.completion`
- 工具调用（function calling）流式与非流式均返回标准 `tool_calls`
- `tool_choice` 传对象（OpenAI 风格）不会被上游拒绝
- `role: "developer"`（部分 SDK 默认发这个）不会被上游拒绝
- token 自动刷新成功，且**校验了 App 的登录文件哈希未变**
- 促销标签从 Console 文档合并（`/v3/config` 本身不携带）
- 错误透传：模型名错误 → 上游 400 原样带回来；缺密钥 → 401；坏 JSON → 400；跨域 Host → 403

一键复验：

```bash
node scripts/verify.mjs     # 39 项检查
```

---

## 上游协议里几个必须绕开的坑

这些都写进了代码，改的时候别"顺手清理掉"：

| 现象 | 上游行为 | 本工具的处理 |
|---|---|---|
| `stream: false` | HTTP 400 `code 11101`，明确不支持非流式 | 一律流式，需要整段回答时本地重组 |
| `role: "developer"` | HTTP 400 `code 11128` | 自动改写为 `role: "system"` |
| `tool_choice` 传对象 | HTTP 400 | 拍平成字符串（`auto` / `required` / 函数名） |
| 国际版首条不是 system | HTTP 400 | 自动补一条 system 前置消息 |
| 每一段 SSE 都带空的 `function_call` | 严格客户端会臆造并行调用 | 丢弃空字段，保留真实 `tool_calls` |
| 上游 `credits` 有时带 "credits" 后缀 | 显示会中英混杂 | 归一化成裸倍率（`x0.79`） |
| 目录文档 `/v3/config` 不带促销标签 | 徽章会全部丢失 | 额外读一次 Console 文档按 id 合并（失败可降级） |
| App 5.6+ 的 token 字段是加密的 | 直接读会拿不到字符串 | 用 App 自身二进制取密钥，内存中解密 |

排查上游变更的完整对照表见 [`docs/UPSTREAM.md`](./docs/UPSTREAM.md)。

---

## 作为库使用

不想起服务、想直接嵌进自己的程序：

```js
import { CredentialStore, WorkBuddyClient, CN_VARIANT } from "workbuddy-api";

const client = new WorkBuddyClient(CN_VARIANT);
const store = new CredentialStore({
  variant: CN_VARIANT,
  refresh: (credential) => client.refreshToken(credential),
  // App 5.6+ 加密凭据才需要；探测不到时用 electronPath 或
  // WORKBUDDY_ELECTRON_BIN 指定 App 可执行文件
});

const credential = await store.resolve();          // 自动刷新
const result = await client.chatStream(credential, JSON.stringify({
  model: "deepseek-v4.1-flash",
  messages: [{ role: "user", content: "你好" }],
}));
// result.ok === true 时 result.response 是原始 SSE Response
```

`src/index.js` 导出了认证、上游客户端、目录、网关等全部模块，可自由组合。

---

## 常见问题

**`status` 说 signed out**
先在桌面 App 里登录一次。App 从来没登录过、也没留下副本时，本工具不会伪造一个账号。
路径不对时用 `doctor` 看候选路径，再用 `WORKBUDDY_AUTH_FILE` 指定。

**`doctor` 说凭据是 `encrypted` 但读不出来**
App 5.6+ 的 token 是加密的，需要一个可执行的 App 二进制来取密钥。
`doctor` 会列出探测过的候选路径；都不对时：

```bash
WORKBUDDY_ELECTRON_BIN="D:\sqsoftware\workbuddy\WorkBuddy.exe" node src/bin.js doctor
```

（把它写进系统环境变量即可长期生效。）

**端口被占用**
`serve` 会直接报绑定失败并退出。换 `--port`，或 `--port 0` 让系统分配。

**一定要填 api_key 吗**
服务只绑回环地址，本机其他程序本来就能访问，所以默认随机生成一个密钥防误用。
要完全免密就 `--no-auth`（仅回环允许）。

**能不能局域网给别的机器用**
技术上 `--host 0.0.0.0` 可以，但**强烈不建议**：这等于把你的 WorkBuddy 账号额度
开放给整个网络。真要用就必须配 `--api-key`，并自行确认网络环境可信。

**模型选了报错**
先用 `models` 确认 id。上游目录会变，缓存过期的跑一次 `models` 刷新即可。

**跑 `serve` 时 App 没开着，能用吗**
能。登录文件和刷新都是文件 + HTTP，不依赖 App 进程本身。
只有 App **5.6+ 加密凭据**在**首次读取时**需要它的二进制取一次密钥——
用 `WORKBUDDY_ELECTRON_BIN` 指向安装路径即可，不需要 App 处于运行状态。

**模型清单和 App 里看到的不一样**
正常。国内版拿到的是 CLI 花名册（正是本工具能调用的那批），App 界面用的是另一份。
详见 [`docs/UPSTREAM.md`](./docs/UPSTREAM.md)。

---

## 已知限制

- **国际版（WorkBuddy AI）代码就绪但未实测**——开发机只装了国内版。国际版走
  App 形态 UA 读同一份 `/v3/config`，链路依据参照实现编写但无真实账号验证。
- **macOS / Linux / WSL 未实测**——路径探测与 key helper 默认路径照搬参照实现，
  本机（Windows）无法验证。Windows 上已验证 `WorkBuddy.exe` 可作 key helper。
- **企业账号计费未实测**——开发机为个人账号。
- **`status` 在加密凭据下不显示昵称**——5.6+ 把 `nickname` 也加密了，本工具
  只解密发请求必需的 token 字段，不解密个人信息。

---

## 免责声明

- 本项目**仅供个人学习和研究使用**，只驱动使用者自己的 WorkBuddy 账号在本机调用。
- 依赖 WorkBuddy 客户端的**非公开接口**，上游改动可能导致失效；请自行遵守 WorkBuddy 服务条款。
- 因使用本项目产生的任何后果（包括但不限于账号被限制、额度被清空、服务中断）由使用者自行承担。
- 本项目与腾讯、WorkBuddy、DeepSeek 均无关联，未获其授权或认可；相关名称仅用于描述兼容关系。

## 致谢

- [Sliverkiss/workbuddy2api](https://github.com/Sliverkiss/workbuddy2api)（MIT）— WorkBuddy 上游协议的参照实现。
- [corrinehu/dsh-workbuddy-connect](https://github.com/corrinehu/dsh-workbuddy-connect)（MIT）— 本项目的认证与上游协议提取自该 DSH 插件的同一条链路。

## 许可证

[MIT](./LICENSE)
