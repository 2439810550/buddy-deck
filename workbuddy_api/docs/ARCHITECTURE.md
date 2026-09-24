# 架构与模块地图

本文档说明 `workbuddy-api` 的内部结构，供后续维护使用。**改代码前先读这里**，
它能回答"这个逻辑为什么在这儿"和"改一处会影响哪里"。

---

## 一句话架构

```
CLI 入口 (bin.js)
   │
   ├── 认证 (auth.js) ────── 读桌面 App 登录文件 / 管理自己的凭据副本
   │        └── 解密 (desktop-credential-protection.js)  ← 5.6+ 加密凭据
   │
   ├── 上游 (upstream.js) ── WorkBuddy 私有协议：chat / refresh / catalog / billing
   │        └── 客户端身份 (client-identity.js)  ← UA 版本号解析
   │
   ├── 目录 (catalog.js) ─── 模型清单：上游实时 → 按账号缓存 → 内置兜底
   │        └── 数据 (models.cn.json / models.ai.json)
   │
   └── 网关 (gateway.js) ─── 本地 OpenAI 兼容 HTTP 服务
            ├── 协议适配 (anthropic.js) ── Anthropic Messages ←→ Chat
            │        └── 公共零件 (protocol.js)  ← 模型别名 / token 估算 / SSE
            └── 协议适配 (responses.js) ── OpenAI Responses ←→ Chat
```

**依赖方向是单向的**，从左到右：`gateway` 依赖 `catalog`/`upstream`/`auth`，
`upstream` 依赖 `client-identity`，`auth` 依赖 `desktop-credential-protection`。
没有任何反向依赖，也没有循环。新增模块时请保持这个方向。

**唯一的外部依赖是 Node 内置模块。** 这是刻意选择的：项目要能被直接 `git clone`
运行，不需要 `npm install`。不要为了省事引入第三方包。

---

## 模块逐个说明

### `src/paths.js` — 状态目录与原子写

管 `~/.workbuddy-api`（可用 `WORKBUDDY_API_HOME` 覆盖）。所有本工具自己产生的
文件都在这个目录下，卸载就是删一个目录。

`writeFileAtomic` 是**并发安全的关键**：凭据刷新时先写临时文件再 `rename`，
保证读方永远不会读到半截 JSON。改刷新逻辑时别绕过它。

### `src/variants.js` — 两个产品的差异集中地

国内版 `workbuddy` 与国际版 `workbuddy-ai` 的**全部差异**都在这两个描述符里：
认证文件名、App 名、区域、上游 base URL、目录路径、内置模型文件名。

> **维护要点**：任何"两个版本行为不同"的新需求，都应该在这里加字段，
> 而不是在其他模块里写 `if (region === "global")`。这是这个文件存在的唯一理由。

`fallbackRoster()` 按需加载 `models.*.json`，不要在模块顶层 `import` JSON
（会拖慢启动，且 Node 的 JSON import 需要 import attributes）。

### `src/auth.js` — 凭据解析与刷新

核心是 `CredentialStore` 类。职责：

1. **发现**登录文件 —— `desktopAuthCandidates()` 按平台给出探测顺序
2. **读取** —— `readDesktop()` 读第一个存在的文件
3. **解密** —— `plaintextDesktopAuth()` 处理 5.6+ 的加密文件（见下）
4. **选新** —— `current()` 在"App 的文件"与"自己的副本"之间取更晚过期的
5. **刷新** —— `resolve()` 在快过期时换新 token，单飞（single-flight）防并发重复刷新

> **维护要点**：`current()` 里有一条容易忽略的规则——若两边的 `uid` 或
> `enterpriseId` 不同，**直接采用 App 的文件**，不比较过期时间。这是为了让
> "用户换了账号"立刻生效，而不是等旧 token 过期。改动这条逻辑要非常小心。

**刷新策略**：提前 5 分钟（`refreshMarginMs`）刷新；刷新失败时，只要旧 token
还没真正过期就继续用它——刷新端点抖动不该让正在工作的会话断掉。

### `src/desktop-credential-protection.js` — 5.6+ 加密凭据

WorkBuddy 5.6 起，App 把 `accessToken` / `refreshToken` 加密存储，文件里是
`{$wbEncrypted: 1, envelope: "<base64>"}` 包装对象。解开它需要三样东西：

| 要素 | 来源 |
|---|---|
| `atRestSecretKey` | 用 App 自己的二进制跑一次 `ELECTRON_RUN_AS_NODE=1`，调它的私有绑定 `workbuddyStorage.loggerGet()` |
| `protectorKey` | `sha256(atRestSecretKey)` 的 UTF-8 字节 |
| AAD 框架 | `WB-AAD\0` + 长度前缀的 `WBEV1` / `sym-v1` / keyId 等，即 `buildAuthenticatedContextAad` |

> **维护要点**：`buildAuthenticatedContextAad` 是从 App 产物里**逐字节转写**的，
> 不是推导出来的。上游改加密格式时这里必然要跟着改，症状是解密抛
> `encrypted-credential-unreadable`。改之前先用 `scripts/` 里的临时脚本
> 单独验证 helper 输出，确认是"格式变了"而不是"key 不对"。

**只解密 token 字段**，不解密 `nickname` / `phoneNumber`：前者是发请求的必需品，
后者是个人数据。这个边界是有意划的，别顺手扩大。

**二进制定位顺序**（`defaultElectronPaths`）：
Windows 先查注册表 `UninstallString`（能覆盖装在任意盘的情况，本机即
`D:\sqsoftware\workbuddy`），再查常规安装根；macOS 用固定路径；Linux 返回空
——因为没验证过布局，猜错会执行错误的二进制。

### `src/client-identity.js` — UA 版本解析

WorkBuddy 网关**按 User-Agent 分流**，所以 UA 长什么样是有功能意义的，不是装饰。

聊天请求的 UA 形如 `WorkBuddy/<v> WorkBuddy/<v> CLI/<cli>`，版本号从已安装的
App 解析（macOS 读 Info.plist / Windows 读注册表 `DisplayVersion`），失败则退回
编译常量。版本号本身不影响路由，但**形状必须是这个**。

> **维护要点**：`validAppVersion` / `validCliVersion` 的严格正则是注入防线
> （值会进 HTTP 头），不要放宽成"差不多就行"。

### `src/upstream.js` — 私有协议客户端

`WorkBuddyClient` 一个实例服务一个 variant。请求都显式接收 credential，
这样刷新后的 token 下一次调用自然生效。

**文件里的协议坑位表（README 也有）是改代码时的检查清单**，每条都有实测依据。
最容易踩的是：

- 上游**只支持流式**，`stream: false` 直接 400。我们是本地重组成整段回答的。
- `role: "developer"` 会被拦，必须改写为 `system`。
- `tool_choice` 传对象会 400，必须拍平。

`collectChatCompletion()` 把 SSE 重组成非流式响应；`sanitizeChunk()`（在
`gateway.js` 里）清理每一段 SSE 的杂音字段。

**目录解析**：`fetchModels()` 现在两个 variant 都读 `/v3/config`，
**差异只在 UA**——国内版发 CLI UA 拿到 CLI 花名册（正是自己请求能用的模型），
国际版发 App UA。`fetchPromoBadges()` 是**国内版专属**的第二次请求，因为
`/v3/config` 不携带 `badge:*` 促销标签，那些标签只在
`/console/enterprises/personal/models` 里。

> **维护要点**：`fetchPromoBadges` 是 best-effort 的，任何失败都返回
> `undefined` 而不是抛错。促销标签丢失绝不能让整个目录刷新失败。

### `src/catalog.js` — 三层模型清单

优先级：**上游实时 → 按账号的本地缓存 → 编译内置**。`Catalog` 持有当前清单，
`CatalogStore` 负责磁盘缓存（按 `uid:enterpriseId` 分账号）。

> **维护要点**：`current()` 每次都对促销做**时间重算**
> （`modelWithCurrentPromotion`）。因为目录在进程内是长期缓存的，如果解析时
> 就把折扣固定下来，促销过期后还会一直显示"限时免费"。别把它优化成
> "解析一次缓存起来"。

### `src/accounts.js` — 多账号保险库

桌面 App 一次只能登一个号，登录文件里也就一份凭据。想同时挂着多个号，
就得把每个号单独存下来。`AccountVault` 就是那个库：

```
~/.workbuddy-api/accounts.cn.json   （0600，里面有 refreshToken）
~/.workbuddy-api/accounts.ai.json
{
  "activeId": "<uid>:<enterpriseId>",
  "accounts": [ { id, label, accessToken, refreshToken, expiresAtMs, ... } ]
}
```

`AccountRouter` 把保险库和 `CredentialStore` 拼成一个 `resolve()/status()` 接口：
**保险库里选了号就用它，没选就退回桌面 App 当前登录的那个号**。
因为网关拿到的就是一个普通的 store，所以 `gateway.js` 一行都没改。

> **维护要点**：保险库每个请求都重新读文件，所以 `accounts use` 切换账号
> **不需要重启网关**。别为了“性能”把 `load()` 结果缓存起来——那就切不掉了。
>
> 刷新失败时不抛：只要旧 token 还没真正过期就继续用。

### `src/checkin.js` — 每日签到

接口是从客户端产物里扒出来的，不是官方文档：

```
POST {billingBase}/v2/billing/meter/checkin-activity-status   查状态
POST {billingBase}/v2/billing/meter/daily-checkin             签到
```

空 body，用和计费接口同一套头（`billingHeaders`）。
客户端还会带 `X-Device-Token`（腾讯 Turing 风控指纹）；**实测不带也能通**，
所以本模块不发——那个 token 要靠客户端原生模块拿，拿不到也不该伪造。

> **维护要点**：响应是 `{code, msg, requestId, data}`，`code === 0` 才算成功；
> `data` 里是 snake_case，`normalizeCheckinStatus` 统一转 camelCase。
> 上游改了字段名的话，只需要改那一个函数。

### `src/protocol.js` — 三种协议共用的零件

模型别名解析、token 粗估、SSE 事件写出、`finish_reason` → `stop_reason` 映射。

> **维护要点**：`createModelResolver` 存在的唯一理由是——Claude Code 报的是
> `claude-*`，Codex 报的是 `gpt-*`，两个都不在花名册里，**必须换掉否则必然 400**。
> 没显式配置时优先挑 `x0.00` 的免费模型，因为用这个适配层的典型场景就是省额度。

### `src/anthropic.js` — Anthropic Messages 适配

Claude Code / CC Switch 走 `POST /v1/messages`，这里是翻译层：
请求（messages / system / tools / tool_choice）、流式响应（命名事件流）、
非流式响应、以及 `/v1/messages/count_tokens` 的粗估。

> **维护要点（改之前先看）**：`stripClientFingerprint()` 是 Claude Code 能进来的前提。
> Claude Code 会在 system 里塞一行 `x-anthropic-billing-header: cc_version=…; cc_entrypoint=…;`，
> WorkBuddy 上游拿它做渠道识别，命中就回 `400 / code 11128`。
> 已逐块逐行二分确认：整份系统提示词里只有这一行会触发，其余 6000+ 字符全部安全。
>
> 另一个要点是 content block 的顺序契约：`message_start` 必须第一个且只发一次，
> 每个 block 要 `start → delta* → stop` 三段齐全，index 从 0 连续递增，
> text 和 tool_use 共用一套编号。`stop_reason` 以**实际内容**为准——
> 有些模型发了 tool_calls 却把 finish_reason 报成 `stop`，而 Claude Code 靠
> `stop_reason` 决定要不要执行工具，所以只要有 tool block 就报 `tool_use`。

### `src/responses.js` — OpenAI Responses 适配

Codex（`wire_api = "responses"`）走 `POST /v1/responses`。
请求侧把 `instructions` 变 system、把 `input` 数组里的
`function_call` / `function_call_output` 还原成 Chat 的 `tool_calls` / `role:"tool"`；
响应侧把 Chat 的 delta 拼成 Responses 的事件流。

> **维护要点**：Responses 的 `output_item` 必须严格「先 added、后 done」，
> `output_index` 连续，且上一项关掉才能开下一项；每个事件都带 `sequence_number`。
> 文本项和 function_call 项共用同一套 index 计数，所以状态机里用 `closeOpen()`
> 统一收口——新增 output item 类型时别忘了在里面补对应的 done 事件。

### `src/transfer.js` — 跨机搬运

把保险库导出成**加密**文件，在另一台机器导入。

```
scrypt(N=16384, r=8, p=1) 派生密钥  →  AES-256-GCM

文件结构：format / version / provider / createdAt / cipher / kdf{salt} / iv / tag / payload
```

导入时会**对每个账号真刷一次**来验证 —— 能刷通才叫“接上了”，不是写进去就算完。

> **维护要点**：token 是服务端的、跟机器无关，所以搬过去就能继续刷新 ——
> 导出的目的是“换台电脑不用重新扫码”，**不是**备份用途。
>
> 加密是必须的：文件里是 refreshToken，而服务端是滑动窗口、能一直续，
> 明文拷到微信/网盘就等于把额度公开了。
>
> `openAccounts` 故意把“口令错 / 文件被改 / 传输截断”统一报成同一句话，
> 不泄露是哪一步出的问题。
>
> 口令**只走 stdin**，不走命令行参数 —— 参数会出现在进程列表里。
> `--pass` 保留给脚本用，但文档里要标清楚风险。

### `src/gateway.js` — 本地 OpenAI 网关

`createGateway()` 返回 `{server, listen, close}`，不自己决定端口，方便测试。

**安全边界有三层，都在 `handle()` 里，删任何一层都要想清楚：**
1. **只绑回环** —— 默认 `127.0.0.1`
2. **Host 必须是回环** —— 防 DNS rebinding（恶意网页把域名解析到 127.0.0.1）
3. **Bearer 密钥** —— 默认随机生成；`--no-auth` 仅限回环绑定

> **维护要点**：`sanitizeChunk()` 丢弃空的 `function_call` 字段。上游每段 SSE
> 都带 `"function_call": null` 或全空对象，严格的客户端会据此臆造一次并行
> 工具调用。这是实测踩出来的，不是洁癖。

---

## 数据流：一次对话请求

```
1. 客户端 POST /v1/chat/completions  (Bearer 密钥)
2. gateway: 校验 Host 回环 → 校验密钥 → 校验 Content-Type / JSON
3. gateway: prepareBodyFor(variant, body)
      ├─ 强制 stream: true
      ├─ developer → system
      ├─ tool_choice 拍平
      └─ 国际版: 确保首条是 system
4. auth: store.resolve()
      ├─ 读 App 文件（必要时解密）
      ├─ 读自己的副本
      ├─ 取更晚过期者
      └─ 快过期则刷新（单飞）
5. upstream: client.chatStream(credential, body)
      └─ POST {base}/v2/chat/completions  带 CLI/App 形态 UA
6. 分支：
   ├─ 客户端要流式 → 逐段 sanitize 后转发，以 data: [DONE] 收尾
   └─ 客户端要整段 → collectChatCompletion() 重组为 chat.completion
```

### 非 OpenAI 协议走的是同一条路

`/v1/messages` 和 `/v1/responses` 走 `translated()`，只是多了一层翻译：

```
1. 客户端 POST /v1/messages   或   POST /v1/responses
2. gateway: 校验 Host / 密钥 / JSON
3. protocol: resolveModel(request.model)
      └─ claude-* / gpt-* 都不在花名册 → 换成一个真实存在的模型
4. adapter.toChat(request, ctx)      ← 把客户端协议翻成 Chat Completions
5. 往下与上面第 3~5 步完全相同（prepareBodyFor → resolve → chatStream）
6. 回程：
   ├─ 流式 → adapter.createStream(res).push() 逐段翻译，最后 finish()
   └─ 整段 → collectChatCompletion() 后交给 adapter.fromCompletion()
```

关键是 **第 5 步完全复用**：适配层不碰认证、不碰上游协议、不碰 UA 构形。
新增一个客户端协议 = 加一个文件 + 在 `handle()` 里加一条路由，
不动任何已有的东西。

---

## 新增一个 variant 需要改哪里
现有设计让这件事是"加数据"而不是"改逻辑"：

1. `src/variants.js` — 加描述符
2. `src/models.<id>.json` — 加内置花名册
3. 若有变体专属的上游差异 → 加描述符字段，并在 `upstream.js` 里按字段分支
4. **不要**在其他模块里散落 `if (id === ...)`

---

## 相关文档

- [`UPSTREAM.md`](./UPSTREAM.md) — 上游协议契约与变更排查
- [`MAINTENANCE.md`](./MAINTENANCE.md) — 日常维护手册与同步流程
