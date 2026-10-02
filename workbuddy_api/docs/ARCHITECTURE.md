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
   ├── 账单 (usage.js) ───── 用量账本（记上游给的真实 credit）
   ├── 联网 (search.js) ──── 搜索 / 抓网页（/agenttool/v1/*）
   ├── 多账号 (accounts.js) ─ 保险库 + AccountRouter
   ├── 签到 (checkin.js) ─── 每日签到
   ├── 搬运 (transfer.js) ── 账号跨机加密导出/导入
   ├── CC Switch (ccswitch.js) ─ 镜像进 CC Switch 供应商列表
   ├── 指纹 (build-id.js) ── /healthz 的 buildId + stale
   │
   └── 网关 (gateway.js) ─── 本地 OpenAI 兼容 HTTP 服务
            ├── 协议适配 (anthropic.js) ── Anthropic Messages ←→ Chat
            │        └── 公共零件 (protocol.js)  ← 模型别名 / token 估算 / SSE
            ├── 协议适配 (responses.js) ── OpenAI Responses ←→ Chat
            ├── 限流规避 (ratelimit.js) ── 429 后自动换模型
            ├── 联网端点 (/v1/search · /v1/webfetch → search.js)
            ├── agent 循环 (agent.js) ─── 网关自己执行注入的媒体工具
            │        └── 工具定义 (media-tools.js) ← 判断"是不是我们的工具"
            └── 画图 / 视频 (miora.js) ── 注入 miora 实例给 agent（走 ctx，不是 import）
```

**依赖方向是单向的**：`gateway` 依赖 `catalog`/`upstream`/`auth`/`agent`/`miora`
等；`upstream` 依赖 `client-identity`；`auth` 依赖 `desktop-credential-protection`；
`catalog` 依赖 `paths`/`variants`/`upstream`；`agent` 依赖 `upstream`/`media-tools`。
没有反向依赖，也没有循环。新增模块时请保持这个方向。

> **`agent.js` 不 import `miora.js`**：它通过调用方给的 `ctx.miora` 使用，
> 所以 `agent` 可测试（测试里塞一个假的 miora）。别为了"省参数"改成直接 import。

> **`variants.js` 是唯一允许出现"两个版本行为不同"的地方**——其他模块不许写
> `if (region === "global")`，要加就加描述符字段。

**唯一的外部依赖是 Node 内置模块。** 这是刻意选择的：项目要能被直接 `git clone`
运行，不需要 `npm install`。不要为了省事引入第三方包。
（唯一的例外是 `ccswitch.js` 的 `node:sqlite`——仍是内置模块，但需要 Node 22.5+。）

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
Windows 先查注册表 `UninstallString`（能覆盖装在任意盘的情况），再查常规安装根；
macOS 用固定路径；Linux 返回空——因为没验证过布局，猜错会执行错误的二进制。

> ⚠️ 这是**本地简化版**：只认 `WORKBUDDY_ELECTRON_BIN`、且注册表正则
> 可能跨产品误匹配。与上游的差距见 [`PATCHES.md`](./PATCHES.md) 3.3 / 6.1。

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

### `src/agent.js` — agent 循环（网关自己执行工具）

客户端发来的请求里被注入了生图/生视频工具（见 `media-tools.js`）。模型回
`tool_calls` 时有个分叉：

- **客户端的工具**（`Read` / `Bash` / `get_weather`）→ 原样交回客户端自己执行
- **我们的工具**（`workbuddy_` 前缀）→ **网关自己执行**，因为只有网关拿着 Miora
  的凭证，而且 Claude Code 根本不认识这些工具名

后者的完整循环在 `runAgentNonStreaming` / `runAgentStreaming` 里跑掉，
对客户端来说就是一次普通的聊天请求。

> **维护要点**：流式路径有一个**缓冲窗口**（`FLUSH_THRESHOLD`）。模型可能先说
> 「好的我来画」**然后**才发 tool_calls；看到 tool_calls 时那半句已经发给客户端
> 并被渲染了，工具结果插不进去。所以正文先攒够 N 个字符（或流结束）才放行，
> 这期间发现是我们的工具就**整轮丢掉**。
>
> 阈值不能改成「看到第一个字符就放行」——模型的前言通常很短，正好落在阈值内，
> 改成立即转发那半句就会漏出去（实测踩过）。

### `src/media-tools.js` — 媒体工具的定义与注入

定义那 4 个工具（文生图 / 改图 / 生视频 / 图生视频）并注入到请求里。
`description` 直接照抄 WorkBuddy `app.asar` 里 `miora-mcp` 插件的官方措辞，
再补上「什么时候不该用」的边界说明。

> **维护要点**：只注册客户端**没自带同名工具**的那些。误判会白花额度，
> 所以 description 里写了大量排除条款。测法见 `scripts/test-tool-intent.mjs`。

### `src/miora.js` — 画图 / 视频（Miora 服务）

生成能力**不是聊天模型**，是独立服务（域名 `public.miora.qq.com`）。
链路：用保险库里的 JWT 去 `{billingBase}/v2/api-keys` 换一个 15 分钟的临时凭据
→ 调 Miora 的 submit 端点 → 上游返回 1 小时失效的签名 URL → 下载下来存本地
→ 客户端通过 `/v1/images/file/<hash>` 取。

> **维护要点**：临时凭据按账号缓存，**切号要重取**，上游说失效要自动重取一次。
> 文件名是内容的 SHA256 前 24 位，所以同一张图重复下载会覆盖、不会越积越多。

### `src/usage.js` — 用量账本

把上游 `usage` 里的 `credit` 字段（**本次真实扣费**，输入价/输出价/缓存折扣都已算好）
追加记到 `~/.workbuddy-api/usage.jsonl`。

> **维护要点**：记的是**上游给的**数字，不是本地按 token 估算的——两者不是一回事，
> 别"优化"成本地计算。文件超 10MB 会轮转成 `usage-*.jsonl`。
> 写盘失败**不抛异常**（记不上账绝不能让请求失败），但设 `WORKBUDDY_DEBUG=1`
> 可以把原因打到 stderr。

### `src/ccswitch.js` — CC Switch 集成

CC Switch 切换供应商时是**整体覆盖** agent 配置文件的。如果它的列表里没有本网关，
用户切一下就把本网关写的配置冲掉、而且列表里也看不见它。所以本模块把当前 agent
配置里**已有的那份**原样 upsert 进 CC Switch 的库（claude / codex / pi 三个 app_type）。

两条硬约束：**不动 `is_current`**（用哪个供应商是用户的决定）、
**不改别人的条目**（只动自己那条，id 固定 `workbuddy-local`）。

> **维护要点**：只做**镜像**，不重算配置内容——这样两边永远一致，也不会有
> "JS 和 C# 各算一遍算不一样"的问题。写之前先备份。
> 读写走 `node:sqlite`（**需要 Node 22.5+**，Node 20 上这条命令不可用）。

### `src/config.js` / `src/paths.js` — 配置与状态目录

`paths.js` 管 `~/.workbuddy-api`（`WORKBUDDY_API_HOME` 可覆盖），并提供
`writeFileAtomic`：**并发安全的关键**，写临时文件再 `rename`，读方永远不会读到半截 JSON。
`config.js` 读写 `config.json`。

> **维护要点**：改刷新/写盘逻辑时别绕过 `writeFileAtomic`。
> 状态目录下都有什么，见 `README.md` 的「状态目录结构」。

### `src/build-id.js` — 代码指纹

算 `src/` 下所有 .js 的 **mtime + 大小**的短哈希，**只在模块加载时算一次**
（代表"这个进程启动时加载的代码"）。`/healthz` 回 `buildId` + `stale`，
面板靠它判断端口上那个网关是不是旧代码，不是就换掉。

> **维护要点**：不要改成每次请求重算——那样代码一改指纹就跟着变，反而失去了
> "这个进程加载的是哪份代码"的语义。也不用 git commit 当版本：用户机器上可能
> 没有 git、可能不是仓库、而且未提交的改动 git 反映不出来。

### `src/index.js` — 库入口

把上面这些模块重新导出，供「作为库使用」的场景。新增对外可见的模块/函数时在这里加一行。

---

### `src/search.js` — 联网搜索 / 网页抓取

WorkBuddy 的第三块能力（前两块是聊天模型和 Miora 生成）。反代官方 Agent 的
两个联网工具 `/agenttool/v1/search` 与 `/agenttool/v1/webfetch`，让任何
CLI / 编辑器 / harness 都能用上账号的联网额度。

和 `miora.js` 一样用**惰性 credential**（`() => store.resolve()`），所以
换号、token 续期都自动跟上 —— **不需要**自己维护 token 文件。

> **维护要点**：
> 1. `includeDomains` 是**本地过滤**的 —— 上游的 `allowed_domains` 实测不生效
>    （限定 `docs.python.org` 仍返回 CSDN）。有过滤时要把 `max_results` 拉满再筛，
>    否则筛完不够数。域名匹配用「相等或子域」，别退回 `endsWith`（`a.com` 会误配
>    `nota.com`）。
> 2. 响应是**扁平**的（业务字段和 `code` 同级），不是套在 `data` 里 ——
>    别照抄计费端点的 `readEnvelope` 取法。
> 3. 401/403 必须是 `UNAUTHORIZED`，**绝不降级成空结果**：那会让"登录失效"
>    伪装成"没搜到"。

### `skills/workbuddy-web-search/` — 可分发到其他 harness 的 skill

一个**自包含的 skill bundle**：`SKILL.md` + `scripts/wbsearch.mjs`。
它不直连上游，而是打**本机网关**的 `/v1/search` / `/v1/webfetch`，
所以不需要任何 token，只要网关在跑 —— 这正是不把 token 随 skill 分发的原因。

> **维护要点**：改网关端点路径或 `config.json` 的字段名时，记得同步
> `scripts/wbsearch.mjs` 的 `resolveTarget()`（它从 `~/.workbuddy-api/config.json`
> 读 `host` / `port` / `apiKey`，环境变量 `WORKBUDDY_GATEWAY_URL` /
> `WORKBUDDY_API_KEY` 优先）。

### `src/ratelimit.js` — 限流规避

上游的限流原文（实测抓到的）：

```
HTTP 429  {"code":6004,
  "msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，
         您也可以切换其他模型继续使用。"}
```

这一句把三件事都说了：

1. 限制**按「账号 + 模型」算** —— 上游自己建议“切换其他模型继续使用”
2. 有**明确的恢复时间**，可以解出来
3. 这是**周期性配额**，不是封号

> **维护要点**：`code 6004` 和 `code 10081`（客户端里的 `RESPONSE_CODE_IP_LIMIT`）
> **不是一回事** —— 前者是模型配额，后者是 IP 层拦截。
> 识别不能只看状态码：上游有时把它包在 400 里发过来，所以还要匹配文案里的
> “使用量已超出频率限制”。
>
> `parseRateLimitReset` 要同时认 `UTC+8` 和 `+08:00` 两种写法。

`RateLimitTracker` 记一张「哪个账号+模型被限流到什么时候」的表。
网关拿到 429 后：换一个模型重试（优先挑 x0.00 的免费模型），
把被拦的模型记进冷却表，窗口内直接跳过不再拿它白试。

**能安全重试的前提**：`chatStream` 是在**响应头阶段**就判定失败的（还没开始吐内容），
所以重试不会产生半截输出。这个前提变了就得重新考虑重试策略。

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

## 常见改动落在哪（路由表）

想改一件事时，**先看这张表**——它能省掉一轮全仓库搜索。
表里"要动"的列是**必须**改的，"顺带"的列是容易漏的。

| 想做的事 | 要动 | 顺带（容易漏） |
|---|---|---|
| **新增一个客户端协议** | 新 `src/<proto>.js`（仿 `anthropic.js`）+ `gateway.js` 的 `handle()` 加路由 + 适配层常量 | `verify.mjs` 加一节；`README.md` 的协议表；媒体工具注入必须走共用层 |
| **新增一个 variant（产品）** | `src/variants.js` 加描述符 + `src/models.<id>.json` | `bin.js` 的 `--provider` 校验；`index.js` 导出；加密凭据要处理二进制定位（见 `PATCHES.md` 6.1） |
| **上游加了新请求头** | `upstream.js` 的 `chatHeaders()` / `commonHeaders()` | 确认它该不该出现在 refresh/catalog/billing 上（`X-IDE-*` 只属于 chat）；`UPSTREAM.md` 的头契约表 |
| **上游改了错误分类** | `upstream.js` 的 `classifyUpstreamError()` | `gateway.js` 的 `KIND_STATUS` 映射；`anthropic.js`/`responses.js` 的 kind→错误类型映射；`UPSTREAM.md` |
| **上游目录/UA 规则变了** | `variants.js` 的 `catalogPath` + `upstream.js` 的 `fetchModels()` | `client-identity.js` 的 UA 构造；`models.*.json` 兜底；`UPSTREAM.md` |
| **新的加密凭据方案** | `desktop-credential-protection.js` | `auth.js` 的 `plaintextDesktopAuth()`；`bin.js` 的 `doctor` 诊断；`UPSTREAM.md` |
| **新增 CLI 命令** | `bin.js` 加 `commandXxx()` + `known` 数组 + `printHelp()` 的 Usage 行 | `README.md` 命令表；`--json` 支持 |
| **新增环境变量** | 读它的那个模块 | `README.md` 环境变量表；`MAINTENANCE.md` 的环境变量救急表 |
| **新增状态文件** | `paths.js`（`configPath`/`logPath` 式）+ 用它的模块 | `README.md` 的状态目录结构树；`logout` 要不要一起删 |
| **改模型别名/默认模型挑选** | `protocol.js` 的 `createModelResolver` | `config.js` 的 `defaultModel`；`WORKBUDDY_DEFAULT_MODEL`；`README.md` 的别名一节 |
| **改限流识别** | `ratelimit.js` 的 `parseRateLimitReset` / `isRateLimited` | `gateway.js` 的换模型重试；`README.md` 的限流一节 |
| **加一个媒体工具** | `media-tools.js` 定义 + `miora.js` 的 submit | `agent.js` 的执行分派；description 的排除条款；`test-agent.mjs` |
| **改画图/视频链路** | `miora.js` | 临时凭据的缓存与切号重取；`README.md` 的链路图 |
| **改联网搜索/抓取** | `search.js` | `gateway.js` 的 `/v1/search`、`/v1/webfetch` 路由；`skills/` 里的 `wbsearch.mjs`；`UPSTREAM.md` |
| **改用量记录字段** | `usage.js` 的 `summarizeUsage` / `recordUsage` / `aggregate` | `bin.js` 的 `commandUsage` 输出；`test-usage.mjs` |

### 改动的通用检查清单

```
1. 先读 PATCHES.md 确认这块是上游核心还是本地 feature（决定能不能"照抄上游"）
2. 保持依赖方向单向（见上文架构图）
3. 零第三方依赖 —— 只用 node: 内置模块
4. 新增"对外可见"的东西 → index.js 加导出
5. 跑离线三件套：test-agent / test-miora / test-usage
6. 改了对话主链路 → 加跑 verify.mjs（会花钱）
7. 更新对应文档（规则见 MAINTENANCE.md 的"文档同步规则"）
```

---

## 新增一个 variant 需要改哪里

现有设计让这件事是"加数据"而不是"改逻辑"：

1. `src/variants.js` — 加描述符
2. `src/models.<id>.json` — 加内置花名册
3. 若有变体专属的上游差异 → 加描述符字段，并在 `upstream.js` 里按字段分支
4. **不要**在其他模块里散落 `if (id === ...)`

> 注意：加密凭据的**二进制定位**目前是简化版，只认 CN 一套；
> 真要上线第二个产品前必须先做 `PATCHES.md` 6.1。

---

## 相关文档

- [`PATCHES.md`](./PATCHES.md) — 本地补丁 vs 上游核心对照（**同步上游前必读**）
- [`UPSTREAM.md`](./UPSTREAM.md) — 上游协议契约与变更排查
- [`MAINTENANCE.md`](./MAINTENANCE.md) — 日常维护手册与同步流程
