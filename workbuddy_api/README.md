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

## 画图端点（OpenAI 图片接口）

网关不只转发聊天 —— **还能画图**。

```
POST /v1/images/generations     文生图
POST /v1/images/edits           改图 / 图生图
GET  /v1/images/file/<name>     取回生成结果
```

按 OpenAI 的图片接口规范做，所以 Cherry Studio、各种画图面板、
自己写的脚本都不用改代码。

### 用法

```bash
curl http://127.0.0.1:8787/v1/images/generations \
  -H "Authorization: Bearer $KEY" \
  -H "Content-Type: application/json" \
  -d '{"prompt":"一只戴墨镜的橘猫，扁平插画风格","size":"1024x1024"}'
```

```json
{
  "created": 1790872167,
  "data": [{ "url": "/v1/images/file/22c241711fec596ca8900aa8.png" }]
}
```

拿到的 `url` 是**网关自己的路径** —— 拼上 base_url 就能取图：

```bash
curl http://127.0.0.1:8787/v1/images/file/22c241711fec596ca8900aa8.png \
  -H "Authorization: Bearer $KEY" -o cat.png
```

### 请求参数

| 字段 | 说明 |
|---|---|
| `prompt` | **必填**。描述文字 |
| `size` | 可选，如 `1024x1024` / `1024x1536` / `1536x1024` |
| `n` | 可选，要几张（1-4）。上游一次一张，这里并发多跑几次 |
| `response_format` | `url`（默认，给网关路径）或 `b64_json`（直接给 base64） |
| `quality` | 可选，透传给上游 |

改图 `POST /v1/images/edits` 多一个字段：

| 字段 | 说明 |
|---|---|
| `image` | **必填**。可以是 https URL、`data:image/png;base64,...`，或者裸 base64 |

### 它走的是哪条路

**不是聊天模型。** WorkBuddy 把生成能力做成了独立的服务（内部叫 Miora），
域名 `public.miora.qq.com`。链路：

```
网关
  ↓ 用保险库里的登录 JWT
POST https://www.codebuddy.cn/v2/api-keys     换一个 15 分钟的临时凭据
  ↓
POST https://public.miora.qq.com/api/ai/workbuddy-proxy/image/generate/submit
  ↓
{model: "hy-image-3.5-wb", status: "completed", resultFiles: [{signedUrl}]}
  ↓ 下载下来存本地（上游给的是 1 小时失效的签名 URL）
GET /v1/images/file/<hash>.png                客户端随时来取
```

代码在 `src/miora.js`。临时凭据有缓存（同一个账号复用，切号自动重取，
上游说失效会自动重取一次）。

## 聊天里直接生图 / 生视频（工具调用）

**跟模型说「画一只猫」就会出图**，不用调上面那个端点。

而且 —— **四种协议都支持**。不管客户端走哪条路，接上就能生图：

| 协议 | 端点 | 谁在用 |
|---|---|---|
| OpenAI Chat | `POST /v1/chat/completions` | pi、Cherry Studio、各种面板 |
| Anthropic Messages | `POST /v1/messages` | Claude Code |
| OpenAI Responses | `POST /v1/responses` | Codex |
| 面板自带的对话 | 走 Chat | WorkBuddy 控制台 |

> **踩过的坑**：第一版只在 Chat 那条路注入了工具。结果 Claude Code 说
> 「画一只企鹅」模型回「我无法直接绘制图片」，Codex 说「画一个蓝色三角形」
> 它写了段 SVG 代码 —— 因为 Anthropic / Responses 共用的 `translated()`
> 里忘了加。现在 `verify.mjs` 有一节专门盯这个（三条协议各测一遍）。

### 怎么做的

第一版这里写的是**关键词匹配**：命中「画」「生成」就去生图。两个死穴：

* **死板**。「生成一个视频」命中「生成」，被当成生图处理 ——
  用户要视频拿到一张图。补词表只能修一个个案，修不了面。
* **一接外挂客户端就废**。那段代码前面有个 `!hasToolUse(request)` 判断 ——
  一看客户端带了 tools 就跳过关键词。于是 Claude Code 里说「画一只猫」，
  模型只会回「我没办法直接画图哦」。

现在照 **WorkBuddy 自己的做法**（挖 `app.asar` 里那个 `miora-mcp` 插件看出来的）：
把生成能力做成**工具**注册给模型，**模型自己判断**该不该调、调哪个。

```
客户端发请求
  ↓ 网关注入 4 个工具（客户端没自带同名工具时）
     workbuddy_generate_image      文生图
     workbuddy_edit_image          改图 / 图生图
     workbuddy_generate_video      文生视频
     workbuddy_image_to_video      图生视频（可带尾帧）
  ↓ 模型回 tool_calls
  ↓ **网关自己执行**（只有网关拿着 Miora 的凭证）
  ↓ 结果作为 role:"tool" 消息回填
  ↓ 带新对话再问一次模型
  ↓ 模型给出带图片 markdown 的最终答复
客户端拿到的就是一次普通的聊天回复
```

**为什么必须网关执行**：Claude Code 不认识 `workbuddy_generate_image`，
交给它也没用。这条只有网关能走。

### 几个关键取舍

**流式下先缓冲再决定。** 模型可能先说「好的我来画」**然后**才发 tool_calls。
等看到 tool_calls 时那半句已经写给客户端了，而客户端已经把前半句渲染出来了，
后面的工具结果插不进去。所以网关**先攒着不急着往外写**：攒够 40 个字符
或流结束才放行；这期间发现是调我们的工具就整轮丢掉，只给最终的干净答复。

**工具执行期间保活。** 生图要 10~30 秒，一个字节都不发。有些客户端有 30 秒
空闲超时。所以执行期间每 10 秒发一个 SSE 注释行（`: keepalive`）。

**客户端的工具原样交回。** 只有 `workbuddy_` 前缀的工具网关执行。
客户端自己的（`Read` / `Bash` / `get_weather`）原样返回，让它自己跑 ——
标准 OpenAI 流程。*（踩过：非流式路径一开始没做这个区分，客户端调
`get_weather` 结果模型收到「未知工具」，回了一句「天气工具好像不可用」，
用户看到一切正常，只是永远查不出数据。）*

**视频提交就走。** 图片等 10~30 秒没问题，视频要几分钟，等下去客户端早超时了。
所以视频工具返回 taskId + 查询地址，让模型转告用户。

### 关掉

想让它老老实实当个纯聊天网关：

```bash
WORKBUDDY_MEDIA_TOOLS=0
```

### 实测

```
scripts/test-agent.mjs         117 项协议测试（假上游，零成本、可重复）
scripts/test-tool-intent.mjs   真的问模型「要不要调工具」（会生图，花钱）

  node scripts/test-tool-intent.mjs          免费用例 + 2 条生图
  node scripts/test-tool-intent.mjs --all    全跑（8 条生图）
  node scripts/test-tool-intent.mjs --video  再加视频（慢）
```

意图测试里「不该调」的用例占多数 —— 它们才是防误判的关键。
**漏判只是用户再说一次，误判会白花额度还答非所问。**

### 生成的文件放哪

```
~/.workbuddy-api/media/<内容哈希>.png
```

文件名是内容的 SHA256 前 24 位 —— **同一张图重复下载会覆盖，不会越积越多**。
权限 0600（和账号保险库一致）。

想清空直接删这个目录。

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
| `WORKBUDDY_MEDIA_TOOLS` | 设 `0` 关掉聊天里的生图/生视频工具（变回纯聊天网关） |

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

## 限流（429 / code 6004）

**上游的原文**（实测）：

```
HTTP 429  {"code":6004,
  "msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，
         您也可以切换其他模型继续使用。"}
```

重要的一点：**这是按「账号 + 模型」算的，不是封号。**
上游自己就提示「切换其他模型继续使用」。

网关的处理：

1. 解析出重置时刻（实测能精确到秒，和原文一致），记进一张冷却表
2. **自动换一个模型重试**，优先挑 `x0.00` 的免费模型 —— 用户完全无感
3. 被限流的模型在冷却窗口内**直接跳过**，不再拿它白试
4. 没有可用备用模型时，把真实错误交给客户端，并在消息里附上
   「已试过 a → b → c」方便排查

`code 6004`（模型配额）和 `code 10081`（IP 层拦截，客户端里叫 `RESPONSE_CODE_IP_LIMIT`）
不是一回事。识别也不能只看状态码 —— 上游有时把它包在 400 里。

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

## 网关进程的版本识别（`/healthz` 的 `buildId`）

`GET /healthz` 除了 `ok` 还回三个字段：

```json
{
  "ok": true,
  "provider": "workbuddy",
  "models": 16,
  "buildId": "b32cf0a26cab",
  "startedAt": "2026-10-02T00:34:18.894Z",
  "stale": false
}
```

**`buildId` 是这个进程启动时加载的代码的指纹**（`src/` 下所有 .js 的
mtime + 大小做 SHA-256，取前 12 位）。

### 为什么需要它

真实事故：

```
8787 上跑着几小时前启动的网关（孤儿进程，父面板已退出）。
代码后来改了好几轮 —— 视频从「当成生图处理」改成了真正的视频生成。
但那个进程不会自动重载，它还在跑旧代码。

用户重启面板 → 面板看到「端口上有人在监听」→ 采用它
→ 功能还是旧的。用户看到的是「视频还是生成不了」，
完全不知道为什么，排查方向全跑偏。
```

现在控制台启动时会拿它的 `buildId` 跟磁盘上的代码比对，
**不一样就自动把旧网关换掉**（`ProcessReaper` 做三重匹配之后才动手：
`node.exe` + `bin.js serve` + `--port N`，任何一条对不上就不碰）。

### 为什么不用 git commit 当版本

- 用户机器上可能没有 git
- 也可能不是 git 仓库（解压安装的场景）
- 而且「工作区有未提交改动」时 git 反映不出来 —— 但代码确实变了

mtime + 大小已经足够区分「代码变了没有」。

### `stale` 字段

磁盘上的代码和正在跑的这份是不是同一个。开发时改了文件但没重启
服务就会是 `true`。面板不只看 `buildId` 比较，也看这个 —— 因为
指纹比较在极端情况下（改了文件又改回来）可能一致。

---

## 已知限制

- **国际版（WorkBuddy AI）代码就绪但未实测**——开发机只装了国内版。国际版走
  App 形态 UA 读同一份 `/v3/config`，链路依据参照实现编写但无真实账号验证。
- **macOS / Linux / WSL 未实测**——路径探测与 key helper 默认路径照搬参照实现，
  本机（Windows）无法验证。Windows 上已验证 `WorkBuddy.exe` 可作 key helper。
- **企业账号计费未实测**——开发机为个人账号。
- **`status` 在加密凭据下不显示昵称**——5.6+ 把 `nickname` 也加密了，本工具
  只解密发请求必需的 token 字段，不解密个人信息。
- **生图/生视频依赖模型判断该不该调工具**——不像关键词匹配那样确定。
  实测 14 条常见说法全对（含 12 条「不该调」的），但模型判断本身有波动。
  测法见 `scripts/test-tool-intent.mjs`。漏判只是用户再说一次；误判会白花
  额度，所以工具的 description 里写了大量排除条款。
- **混合同轮调用时只认一边**——如果模型在同一轮里既调我们的生图工具、
  又调客户端的工具（`Read` / `Bash` 之类），网关只执行我们那个，其余
  给一条说明性回复。因为网关没法替客户端执行本地工具，而客户端不认识
  我们的工具。实际很少见，遇到会在日志里记一条。
- **流式首字节有延迟**——为了处理「先说话后调工具」，网关要缓冲约 40
  个字符才放行。正常聊天感觉不到（模型很快超过 40 字），但极短的回复
  会晚一点出现。

---

## 免责声明

- 本项目**仅供个人学习和研究使用**，只驱动使用者自己的 WorkBuddy 账号在本机调用。
- 依赖 WorkBuddy 客户端的**非公开接口**，上游改动可能导致失效；请自行遵守 WorkBuddy 服务条款。
- 因使用本项目产生的任何后果（包括但不限于账号被限制、额度被清空、服务中断）由使用者自行承担。
- 本项目与腾讯、WorkBuddy、DeepSeek 均无关联，未获其授权或认可；相关名称仅用于描述兼容关系。

## 致谢

- [Sliverkiss/workbuddy2api](https://github.com/Sliverkiss/workbuddy2api)（MIT）— WorkBuddy 上游协议的参照实现。
- [corrinehu/dsh-workbuddy-connect](https://github.com/corrinehu/dsh-workbuddy-connect)（MIT）— 本项目的认证与上游协议提取自该 DSH 插件的同一条链路。

## 这个子项目后来被用在哪

[BuddyDeck](https://github.com/2439810550/buddy-deck) 把本网关当作地基，
在上面加了 Anthropic Messages / OpenAI Responses 两个协议适配、
多账号保险库、每日签到、滑动窗口续期、跨机加密搬运，
以及一个 Windows 桌面端（托盘、一键接客户端、计划任务）。

本目录的代码**不因被引用而改变归属**；引用请保留 [znahovo](https://github.com/znahovo) 的署名。

## 许可证

[MIT](./LICENSE)
