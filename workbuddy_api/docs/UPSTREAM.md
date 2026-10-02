# 上游协议契约

本文档记录 `workbuddy-api` 依赖的 WorkBuddy **非公开接口**行为。这些不是稳定
契约，上游随时可能改；这里写的是**已实测的现状**以及**改坏了会怎样**。

> ⚠️ 这些接口是逆向得到的，不是官方 API。改动前请先读
> [`ARCHITECTURE.md`](./ARCHITECTURE.md) 的模块地图。

---

## 端点总览

| 用途 | 方法 | 路径 | Base |
|---|---|---|---|
| 对话 | POST | `/v2/chat/completions` | `copilot.tencent.com`（CN）/ `www.workbuddy.ai`（AI） |
| 刷新令牌 | POST | `/v2/plugin/auth/token/refresh` | 同上 |
| 模型目录 | GET | `/v3/config` | 同上 |
| 促销标签 | GET | `/console/enterprises/personal/models` | 同上（**仅 CN**） |
| 个人积分 | POST | `/v2/billing/meter/get-user-resource` | `www.codebuddy.cn`（CN）/ `www.workbuddy.ai`（AI） |
| 企业积分 | POST | `/v2/billing/meter/get-enterprise-user-usage` | `www.codebuddy.cn`（**仅 CN**） |

**区域判定**：`domain` 为空或非 `*.workbuddy.ai` → CN；否则 AI。见 `regionOf()`。

---

## 请求头契约

### 通用头（所有请求）
```
Accept: application/json, text/plain, */*
X-Requested-With: XMLHttpRequest
Origin: <上游站点>
Referer: <上游站点>/
User-Agent: CLI/2.63.2 CodeBuddy/2.63.2
```

### 对话请求额外头
```
Authorization: Bearer <accessToken>
Content-Type: application/json
X-User-Id: <uid>            # uid 为空时改发 X-No-User-Id: 1
X-Enterprise-Id: <id>       # 无企业时改发 X-No-Enterprise-Id: 1
X-Domain: <domain>          # domain 为空时改发 X-No-Department-Info: 1
X-IDE-Type: WorkBuddy       # IDE 归属标识（插件 0.6.3 / issue 54）
X-IDE-Name: WorkBuddy
X-IDE-Version: <app 版本>    # 与 UA 里的版本号同源
X-Product: SaaS
```

> `X-IDE-*` 三个头只出现在**对话与探测**请求上。刷新、目录、计费**不带**——
> 这是上游测试里钉住的不变量，别顺手加到 `commonHeaders()` 里去。
> `X-IDE-Version` 必须与 UA 中的 `WorkBuddy/<v>` 同源；两者由**同一次**
> 身份解析产生（`chatIdentity()`），不允许各算各的。

**User-Agent 在对话请求里会被替换**为桌面客户端形态
（`WorkBuddy/<v> WorkBuddy/<v> CLI/<cli>`，国际版产品段是 `WorkBuddy AI`）。
这是必须的——裸的 OpenAI 风格 UA 会被拒。

### 刷新请求额外头
```
X-Refresh-Token: <refreshToken>      # 只在这个端点出现
X-Auth-Refresh-Source: workbuddy
X-Enterprise-Id: <id>                # 有企业时
```

### 计费请求额外头
```
Authorization: Bearer <accessToken>
X-User-Id / X-Enterprise-Id / X-Tenant-Id / X-Domain
```
注意计费走 `Authorization`，**不是** `X-Refresh-Token` 那套。

---

## 各端点的坑位

### `/v2/chat/completions`

| 输入 | 上游行为 | 我们的处理 |
|---|---|---|
| `stream: false` | **HTTP 400, `code 11101`** `Non-stream chat request is currently not supported` | 一律强制 `stream: true`，需要整段时用 `collectChatCompletion()` 本地重组 |
| `role: "developer"` | **HTTP 400, `code 11128`** `Illegal API invocation from an unapproved channel` | 改写为 `role: "system"` |
| `tool_choice` 为对象 | **HTTP 400** | 拍平为字符串：`none` → 删除（并删掉 tools）；`auto`/`required` → 原样；`function` → 函数名 |
| 国际版首条非 `system` | **HTTP 400, `code 11128`**（同一个 code 在 CN 是另一个含义） | 前置一条 `system` |
| 模型名不存在 | `code 11102` `model [...] service info not found` | 原样透传给客户端（400） |

**响应形态**：标准 OpenAI SSE，**但有两处噪音**：

1. 每段 `delta` 都带 `function_call`，值是 `null` 或全空对象 `{name:"",arguments:""}`。
   严格的客户端会据此发一次伪造的并行工具调用 → 我们在 `sanitizeChunk()` 里删掉。
2. 末段 `usage` 带一堆厂商专有计数（`prompt_cache_hit_tokens`、
   `completion_thinking_tokens`、`credit` 等）→ 只保留
   `prompt_tokens`/`completion_tokens`/`total_tokens`。

**工具调用**是标准 OpenAI 增量形态：首个 chunk 带 `id`/`type`/`function.name`，
后续 chunk 只带 `function.arguments` 分片，按 `index` 归并即可
（`collectChatCompletion()` 已实现）。

**首段**可能带 `role: "assistant"`，内容为空。

### `/v3/config`（模型目录）

**两个 variant 都读这个路径**，因为它是桌面产品自己用的文档。差异**只在 UA**：

- CN 发 **CLI UA**（`CLI/2.63.2 CodeBuddy/2.63.2`）→ 网关下发的 `cli` agent 花名册，
  正是我们发聊天请求能用的那批模型
- 国际版发 **App UA**（`WorkBuddyAI/<version>`，**无空格**）→ App 内部花名册

> 历史上 CN 读的是 `/console/enterprises/personal/models`，那份文档落后于产品
> 文档，导致模型清单和 App 选择器不一致。0.6.1 起改为 `/v3/config`。

**响应解析**：`data.models` 是模型定义表，`data.agents` 里 `name === "cli"` 的
那一项的 `models` 是**有序 id 列表**——最终清单 = `cli` 顺序 ∩ 可用行。
行内特征：`disabled` 为 true、`maxInputTokens`/`maxOutputTokens` 非正的都丢弃。

**UA 形状**：`WorkBuddyAI/<version>`，**不能有空格**。带空格的
`WorkBuddy AI/<version>` 会被拒（400, `code 12403`）。版本号本身网关不校验。

### `/console/enterprises/personal/models`（促销标签）

`/v3/config` **不携带** `badge:<标签>:<颜色>` 标签，折扣文案只在 Console 文档里。
所以 CN 的目录刷新会**额外发一次**这个请求，按模型 id 合并标签。

**best-effort**：任何失败（网络、信封、解析）都返回 `undefined`，模型照常下发，
只是没有促销徽章。不要在 `fetchPromoBadges()` 里抛错。

### 计费端点

CN 企业账号（`enterpriseId` 非空）走 `/v2/billing/meter/get-enterprise-user-usage`：
空 body，身份全在头里，返回单个周期额度。字段有**两套拼写**
（`limitNum`/`credit` 与 `limit_num`/`used_num`），两套都要认。

`limit === -1` 表示不限量。字段认不出来时**抛错而不是返回 0**——把"没看懂"
渲染成 0 正是历史上一次 bug 长期不可见的原因。

**区域门是承重的**：企业端点在国际区未经验证，所以国际版即使带了
`enterpriseId` 也留在个人版路径上。

---

## 响应信封

除 SSE 外，所有 JSON 响应都是 `{code, msg, data, ...}`。`code === 0` 表示成功。
非 0 时按 HTTP 状态 + `msg` 分类错误（`classifyUpstreamError`）：

| 分类 | 触发条件 | 网关对外状态码 |
|---|---|---|
| `hard_credit` | HTTP 402，或 msg 含"积分不足/额度不足/payment required"等 | 402 |
| `session_dead` | **HTTP 401（无条件）**，或 msg 含 `Offline user session not found` / `12153` | 401 |
| `soft_rate` | HTTP 429 | 429 |
| `not_found` | HTTP 404 | 502 |
| `server` | HTTP ≥ 500 | 502 |
| `client` | 其余 4xx | 400 |

> **裸 401 无条件算 `session_dead`**（上游 PR #58）。401 在这个上游没有业务
> 含义，所以不看 body；判断放在积分标记**之前**，避免一个"body 里恰好提到积分"
> 的 401 被误判成 `hard_credit`。`Offline user session not found` / `12153`
> 这两个标记仍然有用——它们还能抓住**以 403 回复**的死会话。

`session_dead` 意味着需要用户去 App 重新登录。

### 错误文案：优先 `displayMsg`

业务错误（安全审核、非法请求）上游回的是结构化 `displayMsg`：

```jsonc
{ "code": 11128, "msg": "Illegal API invocation",
  "displayMsg": { "zh": "该渠道未获授权", "en": "unapproved channel" } }
```

`extractDisplayErrorMessage()` 按 `displayMsg.zh` → `displayMsg.en` → `msg` 顺序
取第一段非空文本；非 JSON 或非对象返回 `undefined`，调用方退回原始摘录。
网关用它替换掉"整包 JSON 糊在错误里"的旧行为。

> **本地与上游 DSH 插件的分歧（有意）**：插件会在 401/403 且非 `session_dead`
> 时**删掉** `(http N)`，因为 DSH 宿主按错误文本里的 `401`/`403` 字样把业务错误
> 误判成 AUTH。**本网关按 `kind` 分类、不解析文本**，所以保留 `(http N)`——
> 这里它是诊断信息。同步时不要照抄插件那行。

---

## 凭据文件契约

### 位置（按探测顺序）
- **Windows**：`%LOCALAPPDATA%` → `%APPDATA%`，下接
  `CodeBuddyExtension\Data\Public\auth\`
- **macOS**：`~/Library/Application Support/CodeBuddyExtension/Data/Public/auth/`
- **Linux**：`$XDG_CONFIG_HOME`（默认 `~/.config`）→ `$XDG_DATA_HOME`
  （默认 `~/.local/share`）
- **WSL**：先探测挂载的 Windows 用户目录，再探原生 Linux 路径

文件名：CN `workbuddy-desktop.info`，国际版 `workbuddy-desktop-ai.info`。

### 文档结构
```jsonc
{
  "auth": {
    "accessToken":  "...",   // 5.6+ 起是 {$wbEncrypted:1, envelope} 包装
    "refreshToken": "...",   // 同上
    "expiresAt":    1794962273785,   // 毫秒；也可能给秒，按数量级判断
    "domain":       "copilot.tencent.com"
  },
  "account": {
    "uid": "df135e4a-...",
    "nickname": "...",       // 5.6+ 起加密（我们不解密）
    "enterpriseId": "..."    // 可选
  }
}
```

同时兼容**扁平结构**（没有 `auth` 外层，字段直接在顶层）。

### 5.6+ 加密（重要）

`accessToken` / `refreshToken` 变成：
```jsonc
{ "$wbEncrypted": 1, "envelope": "<base64 of {suite,keyId,nonce,authTag,ciphertext}>" }
```

解密要素：
- `suite` 必须是 `1`
- `keyId` 是 16 位十六进制（`sha256(protectorKey)` 的前 16 字符）
- `nonce` 12 字节，`authTag` 16 字节
- 算法 AES-256-GCM，AAD 为 `WB-AAD\0` + 长度前缀框架（见
  `buildAuthenticatedContextAad`）
- `protectorKey = sha256(atRestSecretKey)`，UTF-8 字节

`atRestSecretKey` 的获取：用 App 自己的二进制执行
```
ELECTRON_RUN_AS_NODE=1 <WorkBuddy 二进制> -e 'process.stdout.write(String(process._linkedBinding("electron_browser_workbuddy_storage").loggerGet()))'
```
返回 `{version:1, atRestSecretKey:"<base64 32字节>", atRestDeveloperPublicKey:{...}}`。

### 二进制定位：本地是**简化版**

解密算法忠实于上游，但**找二进制这一步本地简化了**，且它与上游的差距是
已知技术债（见 [`PATCHES.md`](./PATCHES.md) 的 3.3 / 6.1）：

| 能力 | 上游插件 v0.7.1 | 本地 |
|---|---|---|
| 每产品独立环境变量 | `WORKBUDDY_ELECTRON_BIN` + `WORKBUDDY_AI_ELECTRON_BIN` | 只有前者 |
| Windows 候选校验 | exe basename + 同目录 `version` + `resources/app.asar` 布局 | **只读注册表 `UninstallString`，不校验** |
| 注册表 DisplayName | 按产品各自的严格正则 | 宽松 `WorkBuddy\b` |

> ⚠️ **已知跨产品误匹配**：`/DisplayName\s+REG_SZ\s+WorkBuddy\b/u` 也会匹配
> 国际版的 `WorkBuddy AI 5.6.2`（`y` 与空格之间是词边界）。在同时装了国内版与
> 国际版的 Windows 机器上，国际版变体可能解析到**国内版**的二进制。
> 本机只装国内版所以没暴露；**要支持国际版加密凭据前必须先修这里**。

---

## 上游变更了怎么办

按这个顺序排查：

1. **`doctor`** —— 先看是登录态、凭据格式还是目录的问题
2. **`status`** —— 确认能否拿到积分（说明认证与计费链路通）
3. **`models`** —— 确认目录能否刷新；`--json` 看 `catalog.source`
   （`live` / `saved` / `builtin`）
4. **`serve` + curl** —— 单独验证对话链路

常见症状对照：

| 症状 | 可能原因 | 处理位置 |
|---|---|---|
| `status` 说 signed out，但 App 明明登录了 | 凭据格式变了（比如新的加密方案） | `desktop-credential-protection.js` |
| 解密报 `encrypted-credential-unreadable` | AAD 框架或算法变了；或 key 不匹配 | 同上，先用临时脚本验证 helper 输出 |
| `models` 一直是 `builtin` | 目录端点变了 / UA 分流规则变了 | `variants.js` 的 `catalogPath` + `upstream.js` |
| 模型清单和 App 里不一致 | UA 形态不对（拿到的是另一个 agent 的花名册） | `client-identity.js` |
| UA 里的版本号永远是 `5.5.6` | 身份解析的字段名错位（本地已修，别改回去） | `client-identity.js` 的 `resolveAppVersion` |
| 对话 400 `11101` | 有人去掉了强制流式 | `prepareChatBody` |
| 对话 400 `11128` | developer 改写失效；或国际版缺前置 system | `prepareChatBody` / `prepareInternationalChatBody` |
| 客户端凭空多出工具调用 | `sanitizeChunk` 没删空 `function_call` | `gateway.js` |
| 促销徽章不显示 | Console 文档请求失败（这是允许的降级） | `fetchPromoBadges` |
| 积分显示 0 但实际有 | 企业账号走了个人端点 | `fetchCredits` 的区域门 |
| 401 被报成 400 | 裸 401 分类被改回落到 `client` | `classifyUpstreamError` |
| 错误里只有一坨 JSON，看不到原因 | `displayMsg` 提取失效 | `extractDisplayErrorMessage` |

**改完一定要跑 `node scripts/verify.mjs`**，它会覆盖上面表里的大部分路径。
只想零成本验证时：`scripts/test-agent.mjs` + `test-miora.mjs` + `test-usage.mjs`，
再加 `doctor` / `models`（都不消耗积分）。
