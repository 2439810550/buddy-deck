# 新旧功能完整对照（`workbuddy_api` → `_next`）

> ## ⚡ 想要「现在还剩哪些缺口」→ 跑这个，别读本表
>
> ```bash
> cd D:\workfandaiapi\_next
> python tools\check_parity.py            # 人读
> python tools\check_parity.py --json     # 给 CI / 界面
> ```
>
> **本表是「当时发生了什么、为什么这么判」的解释性文档；脚本是「此刻事实」的唯一来源。**
> **两者冲突时，以脚本为准。**
>
> 脚本输出四态：`[已实现] [仍缺失] [待人工] [有意不做]`，并带**对照组**
> （已知已实现项）—— 若对照组也失败，说明**检查器本身坏了**，不是代码缺了。
>
> **为什么要把表降级为「解释」**：这份表初稿写完当天就有**两处结论已经过期**
> （M10 用量账本、`/admin/*` 路由族）—— 详见「修订记录」与 §2 开头的警告框。
> **一份会过期的表，价值随并发写在衰减；一个能重跑的检查，价值不会衰减。**
>
> ⚠ **下面所有「缺失」结论都可能已过期 —— 以脚本输出为准。**

---

> 作者：e2e（t21，2026-10-07）。t27 补充：可重跑检查器 `tools/check_parity.py`。
> 依据：逐文件读 `D:\workfandaiapi\workbuddy_api\src\`（**只读**）与 `D:\workfandaiapi\_next\src\`。
>
> **修订记录**
> - **初稿 2026-10-07 ~07:12**（列举清单时点）
> - **修订 ~08:30**：复核发现两处结论已过期（M10 用量账本、§2.4 `/admin/*`），
>   均因**我落笔前没有重列清单**而误报为缺失；同时独立复验了 t17 的 `/admin/*` 交付（52 项全过）。
>   **详见 §2 开头的警告框与 §6.1 的自我更正。**
> - **修订 ~08:45（t27）**：新增 `tools/check_parity.py`，**本表据此降级为解释性文档**。
>
> **这份文档的存在理由**：团队已经犯过一次「以为搬全了、实际缺一半」——用户问
> 「生图生视频读图搜索拓展都加了吗」，核实后新项目里**一件都没有**。本表就是为了
> 让同类遗漏在上线前暴露，而不是在用户提问时暴露。
>
> ⚠ **但请记住它的性质**：这是**某个时点**的快照。`src/` 一直在被并发修改，
> **任何一条「缺失」在动工前都要重新 grep 一次** —— 或者直接跑 `check_parity.py`。
>
> ## 怎么读这份文档（证据等级）
>
> 每条结论都带标记：
>
> - **【已核实】** —— 我读了那个文件、确认了具体行为（多数带行号）。
> - **【推断】** —— 我没读完那个文件，或结论靠 `grep` 命中推断出来，**未实测**。
> - **⏳ 待验** —— 暂不能自动证实，**但已写明证实路径**（见 §6.2）。
>
> ### ⚠ 团队硬规则（2026-10-07 采纳）
>
> **凡标【推断】，必须同时写清证实路径（一条命令 / 一个测试）。
> 没有证实路径的推断，不许留在文档里当依据。**
>
> 这条规则的由来：我（e2e）原来标了【推断】就以为尽责了，
> **其实只是把「没验证」写得更体面一点** —— 读者只看到「有证据分级」，
> 意识不到**这些条目从写下那天起没人验过**。
> **本轮 t27 收尾时逐条处理：已证实 3 条、推断被推翻 1 条、给出路径待验 4 条、无法证实 1 条。**
> 详见 §6.2。**其中最该记住的是 I4 —— 它挂了整整一个小时没人验，一验就发现是错的。**
>
> 团队已被自己的推断打脸三次（「localhost 会撞 403」「并发会把请求全挤到一个号」
> —— 均被 ui 实测推翻；「粘性键不等价」—— t27 复核推翻）。
> **凡标【推断】的，动工前必须先核实一遍。**

---

## §1 总览表

### 1.1 旧版 → 新版

旧版 25 个 `.js`（约 10,572 行）。统计口径：`(Get-Content).Count`，含空行与注释。

| # | 旧版文件 | 行数 | 判断 | 新版落点 / 说明 |
|---|---|---|---|---|
| 1 | `bin.js` | 1631 | **部分移植** | CLI 全部命令（serve/status/models/doctor/logout/config/accounts/checkin/ccswitch/usage/search）。新版只有 `BuddyDeck.Gateway/Program.cs`（入口 + 参数），**无 CLI 子命令**；界面替代了部分 |
| 2 | `gateway.js` | 1493 | **部分移植** | `GatewayApp.cs`（1176 行）。旧版 11 条路由 → 新版 4 条（见 §2.1） |
| 3 | `upstream.js` | 1053 | **大部分移植** | `Core/UpstreamClient.cs` + `Core/UpstreamProtocol.cs` + `Gateway/SseReader.cs` + `Gateway/SseSanitizer.cs` + `Gateway/ChatStreamCollector.cs` + `Gateway/ModelCatalog.cs`。**缺**：`probeEffort`（M17）、套餐促销/计费解析（M18）、**UA 来源（M20 —— t51 已上调优先级，见 §2.6）** |
| 4 | `agent.js` | 587 | **缺失** | 无。agent 循环 + 流式缓冲窗口 + keepalive |
| 5 | `miora.js` | 539 | **缺失** | 无（已派 t20） |
| 6 | `responses.js` | 539 | **缺失** | 无。OpenAI Responses API 翻译层 |
| 7 | `desktop-credential-protection.js` | 523 | **部分移植** | `Core/AuthEnvelope.cs`（199 行，外壳识别）。**缺**：真实解密（`AtRestKeyProvider` 调原生 helper） |
| 8 | `auth.js` | 504 | **已移植** | `Core/AuthDocument.cs` + `Core/Vault.cs` + `Core/Credential.cs` + `Gateway/IChatObserver.cs` 里的 `VaultCredentialSource` |
| 9 | `ccswitch.js` | 470 | **缺失 / 待定** | 无。CC Switch 集成（写 `~/.cc-switch/cc-switch.db`） |
| 10 | `anthropic.js` | 491 | **缺失** | 无。Anthropic Messages 翻译层 |
| 11 | `accounts.js` | 393 | **已移植** | `Core/AccountPool.cs`（316 行）+ 账号池界面 |
| 12 | `search.js` | 376 | **缺失** | 无（已派 t20） |
| 13 | `client-identity.js` | 313 | **部分移植** | `Core/UpstreamProtocol.cs` 的 `X-IDE-*` 族是**写死常量**，不读 App 版本、不探测安装 |
| 14 | `media-tools.js` | 264 | **缺失** | 无（已派 t20） |
| 15 | `usage.js` | 236 | **缺失** | 无。用量账本（JSONL + 聚合） |
| 16 | `catalog.js` | 199 | **部分移植** | `Gateway/ModelCatalog.cs`（77 行）。**缺**：按账号落盘缓存、促销徽标 |
| 17 | `ratelimit.js` | 157 | **部分移植** | `Core/AccountPool.cs` 的冷却机制在，但**只有** `ResetAtFromBody`（解析 JSON 的 `resetAt`），**缺**文案式时间解析（见 §4.2） |
| 18 | `checkin.js` | 137 | **缺失** | 无。每日签到 |
| 19 | `transfer.js` | 140 | **缺失** | 无。账号加密导出/导入（scrypt + AES-256-GCM） |
| 20 | `protocol.js` | 131 | **部分移植** | 拆分进了 `UpstreamProtocol.cs` + `ChatStreamCollector.cs`。`estimateTokens` / `count_tokens` **缺失** |
| 21 | `variants.js` | 102 | **已移植** | `Core/Variants.cs`（60 行）。**缺**：`promoBadgePath`、`modelsFilename` 的外置花名册 |
| 22 | `build-id.js` | 95 | **已移植** | `Gateway/BuildId.cs`（138 行）。新版还多修了两个 bug（排序、排除 bin/obj） |
| 23 | `paths.js` | 91 | **有意不做** | 见 §3.1 —— 状态目录由 WPF 侧与 `GatewayOptions` 各自管 |
| 24 | `config.js` | 68 | **已移植** | `Gateway/GatewayOptions.cs`（183 行）。**缺**：持久化（旧版写 `config.json`，新版只有命令行/环境变量，见 §2.3） |
| 25 | `index.js` | 73 | **不需要** | 见 §3.2 —— 库入口，新架构下是程序集引用 |

**小计**：已移植 6 · 部分移植 9 · 缺失 9 · 有意不做 1 · 不需要 1（表中 26 项因 `bin.js` 与 `gateway.js` 各算一条）。

### 1.2 新版 → 旧版（反向核对，确认没有一边凭空多出来）

| 新版文件 | 行数 | 对应旧版的什么 |
|---|---|---|
| `Core/Variants.cs` | 60 | `variants.js` |
| `Core/Credential.cs` | 71 | `auth.js`（`credentialIdentity`） |
| `Core/AuthDocument.cs` | 91 | `auth.js`（`parseWorkBuddyAuth`） |
| `Core/AuthEnvelope.cs` | 199 | `desktop-credential-protection.js`（**仅识别，不解密**） |
| `Core/Vault.cs` | 161 | `accounts.js`（`AccountVault` 的持久化部分） |
| `Core/UpstreamProtocol.cs` | 289 | `upstream.js`（`chatHeaders`/`billingHeaders`/`prepareChatBody` 等） |
| `Core/UpstreamClient.cs` | 329 | `upstream.js`（`WorkBuddyClient`） |
| `Core/AccountPool.cs` | 316 | `accounts.js`（`AccountRouter`）+ `ratelimit.js`（冷却部分） |
| `Core/ConversationAffinity.cs` | 472 | **旧版无独立文件**。旧版会话粘性散在 `gateway.js`/`upstream.js`（见 §4.3） |
| `Gateway/Program.cs` | 163 | `bin.js`（`serve` 子命令）+ `index.js` 的装配部分 |
| `Gateway/GatewayApp.cs` | 1176 | `gateway.js`（**只搬了 4 条路由**） |
| `Gateway/GatewayOptions.cs` | 183 | `config.js`（**只搬了读取，没搬持久化**） |
| `Gateway/BuildId.cs` | 138 | `build-id.js` |
| `Gateway/ModelCatalog.cs` | 77 | `catalog.js`（**只搬了内存清单与解析**） |
| `Gateway/SseReader.cs` | 101 | `upstream.js`（`iterateSseData`，行 870 附近） |
| `Gateway/SseSanitizer.cs` | 127 | `gateway.js`（`sanitizeChunk`，行 194）+ `captureUsage`（行 251） |
| `Gateway/ChatStreamCollector.cs` | 184 | `upstream.js`（`collectChatCompletion`，行 910） |
| `Gateway/IChatObserver.cs` | 159 | **旧版无对应**。新版新增的观察者挂载点（P1/P2 的接缝） |
| `Gateway/Metrics.cs` | 517 | **旧版无独立文件**（t2 新增，见 §4.4） |
| `Gateway/RequestLog.cs` | 396 | `usage.js` 的**日志侧**（**但不是账本**，见 §2.5） |
| `App/**`（42 个文件） | ~4700 | `WorkBuddyPanel`（WPF，**不在本次对照范围**，旧版 `bin.js` 的 CLI 部分能力被它取代）。⚠ 其中 `Services/IGatewayClient.cs`（676 行）调用的 **11 条 `/admin/*` 路由**曾全部缺失 —— **已由 t17 补齐（新增 `Gateway/AdminEndpoints.cs`，37,775 字节）**，见 §2.4 |

**【已核实】反向核对的结论：新版没有任何文件是「凭空多出来」的**，除两处刻意新增：
`ConversationAffinity.cs`（旧版逻辑散落，新版收敛成独立类）与 `IChatObserver.cs`/`Metrics.cs`
（旧版无对应物，是 observability 的新接缝）。

**但反向核对暴露了一个更严重的问题**：新版**内部**曾对不上 ——
界面调用的 `/admin/*` 路由族在网关侧**不存在**，且旧版也没有过。
**该缺口已由 t17 修复并经我独立复验（52 项检查全过）**，详见 **§2.4**。

---

## §2 缺失清单（按依赖排序）

> ### ⚠ 先读这段：本表的**时点**与一次自我更正
>
> 本表初稿写于 **2026-10-07 07:12 前后**（我那次文件清单列举的时间）。
> **但 `src/` 一直在被队友并发修改** —— 等到 08:28 我复核时，发现**至少两处已经不该算「缺失」**：
>
> | 我在初稿里写的 | 实际情况 | 真相 |
> |---|---|---|
> | M10 用量账本「缺失」 | `Core/UsageLedger.cs`（38,567 B）**落盘于 07:17** | 我**列举清单时它还不存在，但写表时已存在** —— 我没在落笔前重列一次 |
> | §2.4 `/admin/*`「两头都不存在」 | `Gateway/AdminEndpoints.cs`（37,775 B）**落盘于 07:11** | 同上 |
>
> **这是我的流程错误**：我在开头列了一次文件清单，之后读了大量文件、过了近一小时才落笔，
> **落笔前没有重新列一次**。并发写入的环境下，那份清单**在写下第二个字时就可能已经过期**。
>
> ⇒ **正确做法**（我此后执行）：**落笔前重列一次清单**，并且**给每条结论标核实时间**。
>
> ⇒ **给读者**：本表任何一条「缺失」在动工前**都要重新 grep 一次** ——
> 它可能已经被别人做了。**标记【已核实】只保证「我读的那一刻是这样」，不保证此刻仍如此。**

规模估计口径：**净新增行数**（不含测试），基于旧版对应文件行数 + 减去新版已有的部分。

### 2.1 【已核实】HTTP 路由对照 —— 业务面仍有多条待补

旧版 `gateway.js` 有 **11 条 `/v1/*` + 健康检查路由**（行 517–671）。
新版 `GatewayApp.cs` 的 `/v1/*` 仍是 **3 条**；**管理面 `/admin/*` 的 11 条已于 t17 补齐**（见 §2.4）。

| 旧版路由 | 旧版位置 | 新版 | 说明 |
|---|---|---|---|
| `GET /healthz` | gateway.js:517 | ✅ GatewayApp.cs:146 | 已移植 |
| `GET /v1/models` | gateway.js:568 | ✅ GatewayApp.cs:168 | 已移植 |
| `POST /v1/chat/completions` | gateway.js:581 | ✅ GatewayApp.cs:182 | 已移植 |
| `GET /metrics` | — | ✅ GatewayApp.cs:184 | **新版新增**（t2），旧版没有 |
| `POST /v1/messages` | gateway.js:587 | ❌ 缺 | Anthropic，Claude Code / CC Switch |
| `POST /v1/messages/count_tokens` | gateway.js:593 | ❌ 缺 | Claude Code 上下文管理 |
| `POST /v1/responses` | gateway.js:605 | ❌ 缺 | Codex（`wire_api = "responses"`） |
| `POST /v1/images/generations` | gateway.js:615 | ❌ 缺（t20） | 生图 |
| `POST /v1/images/edits` | gateway.js:620 | ❌ 缺（t20） | 改图 |
| `POST /v1/video/generations` + `/v1/videos` | gateway.js:630 | ❌ 缺（t20） | 生视频 |
| `GET /v1/video/status/<id>` | gateway.js:639 | ❌ 缺（t20） | 视频异步查任务 |
| `POST /v1/search` | gateway.js:651 | ❌ 缺（t20） | 联网搜索 |
| `POST /v1/webfetch` | gateway.js:656 | ❌ 缺（t20） | 网页抓取 |
| `GET /v1/images/file/<name>?s=` | gateway.js:666 | ❌ 缺（t20） | 生成结果中转 + HMAC 签名免鉴权 |

> **管理面 `/admin/*`（11 条，旧版没有、新版界面要用）已补齐** ——
> 见 §2.4。那是**新版自有需求**，不属于「旧版移植」范畴。

### 2.2 缺失项明细

每项：**规模 / 依赖 / 可否并行**。

| ID | 缺失能力 | 旧版依据【已核实】 | 规模 | 依赖 | 并行性 |
|---|---|---|---|---|---|
| **M1** | Anthropic Messages 翻译层 | `anthropic.js` 491 行；`gateway.js:587` 挂载 | ~450 | 无（只用 `/v1/chat/completions` 已有能力） | ✅ 新文件，可并行 |
| **M2** | OpenAI Responses 翻译层 | `responses.js` 539 行；`gateway.js:605` | ~500 | 同 M1 | ✅ 新文件，可并行 |
| **M3** | `count_tokens` + `estimateTokens` | `protocol.js:85`；`gateway.js:593` | ~40 | 无 | ✅ 但**依赖 M1 的路由**才能挂 |
| **M4** | 协议公共零件（SSE 命名事件等） | `protocol.js:96-146`（`beginSse`/`writeEvent`/`toAnthropicStopReason`） | ~120 | M1、M2 都要用 → **应先做** | ⚠ 与 M1/M2 同一作者更稳 |
| **M5** | Miora 生图/生视频客户端 | `miora.js` 539 行 | ~520 | 无 | ✅（已派 t20） |
| **M6** | 媒体工具定义注入 | `media-tools.js` 264 行 | ~250 | 无 | ✅（已派 t20） |
| **M7** | Agent 循环（网关代执行工具） | `agent.js` 587 行 | ~550 | **强依赖 M5 + M6** | ❌ 与 M5/M6 串行 |
| **M8** | 联网搜索 + 抓取 | `search.js` 376 行 | ~360 | 无 | ✅（已派 t20） |
| **M9** | 图片文件中转 + HMAC 签名 | `gateway.js:333-340`（`signName`/`signatureMatches`）、`:1240`（`serveGeneratedFile`） | ~120 | **强依赖 M5**（没生成就没文件） | ❌ 串行 |
| **M10** | 用量账本 —— **✅ 已实现，不必再做** | `usage.js` 236 行 → `Core/UsageLedger.cs`（38,567 B）+ `Gateway/UsageEndpoints.cs`（13,233 B） | — | 已落地（observability 线） | 【已核实】含 `credit` 字段、JSONL 落盘、by-model / by-account 聚合 |
| **M11** | 每日签到 | `checkin.js` 151 行 | ~150 | 无（用 `billingHeaders`，已有） | ✅ 新文件 |
| **M12** | 账号加密导出/导入 | `transfer.js` 151 行 | ~150 | 无（.NET 有内置 scrypt/AES-GCM） | ✅ 新文件 |
| **M13** | 429 恢复时间的**文案**解析 | `ratelimit.js:51` `parseRateLimitReset` | ~60 | 无 | ✅ 小改动 |
| **M14** | 配置持久化 | `config.js:61` `saveConfig` | ~80 | 无 | ⚠ 碰 `GatewayOptions.cs` |
| **M15** | CC Switch 集成 | `ccswitch.js` 503 行 | ~480 | 无（但要 `Microsoft.Data.Sqlite`，破坏零依赖） | ✅ 新文件，但**先决策要不要做** |
| **M16** | 桌面凭据真实解密 | `desktop-credential-protection.js:523`（`AtRestKeyProvider`） | ~400 | 无 | ⚠ 需平台原生 helper，跨平台成本高 |
| **M17** | `probeEffort`（思考档位探测） | `upstream.js:984` | ~60 | 无 | ✅ |
| **M18** | 套餐促销/计费解析 | `upstream.js:365-504`（`normalizeCredits`/`parsePromotions`/`modelWithCurrentPromotion`）、`variants.js:35` `promoBadgePath` | ~200 | 无 | ✅ |
| **M19** | 按账号落盘的模型目录缓存 | `catalog.js:19` `catalogPath`、`:38` `isSaved` | ~150 | 无 | ✅ |
| **M20** | 客户端身份探测 / **User-Agent 来源** | `client-identity.js` 313 行 | ~300（完整探测）／**~10（只补目录 UA）** | 无 | ⚠ **优先级已上调**（t51 重估，见 §2.6）；**最小修法**只是给 `/v3/config` 一个 UA |

### 2.3 关于 M14（配置持久化）—— **t17 后仍是真实缺口**【已核实】

- 旧版：`config.js` 读写 `~/.workbuddy-api/config.json`，含 `provider/host/port/apiKey/authFile/authFileAI/defaultModel`。
- 新版：`GatewayOptions.cs` 只有 `Parse(args)` + `ApplyEnvironment()`（行 53–105 读命令行、87–105 读环境变量），**没有把设置写回文件的能力**。
- **界面确实依赖设置持久化**：`App/Services/IGatewayClient.cs:684` 调 `GET /admin/settings`、
  `:687` 调 `POST /admin/settings`。该路由**已由 t17 补齐**（见 §2.4）。
- ⚠ **但 POST 只改内存、不落盘**【已核实，我自己起真网关验的】：
  `POST {"port":9999}` → 200 → 随后 `GET` 返回 `port: 9999`（内存确实变了），
  但运行目录下**只有 `gateway-*.log`，没有任何设置文件** ⇒ **重启即丢**。
- ⇒ **M14 保留在缺失清单里，是真实缺口**：用户在界面改端口/API key，重启网关就回退。
  规模 ~80 行（.NET 有现成 JSON 序列化 + 原子写，比旧版还简单）。

### 2.4 `/admin/*` 端点族 —— **t21 发现、t17 修复、已独立复验**

> **状态：已解决（2026-10-07 更新）**。本节保留发现过程与复验证据，
> 因为它记录了一个真实的验收盲区，且修复过程中暴露了两条**已知限制**（见 2.4.4）。

#### 2.4.1 当初的发现（t21 时成立）

界面（`App/Services/IGatewayClient.cs`）调用了 **11 条 `/admin/*` 路由**：

```
行 507  GET  /admin/accounts
行 514  POST /admin/accounts/{id}/activate
行 517  POST /admin/accounts/{id}/rename
行 520  POST /admin/accounts/{id}/remove
行 523  POST /admin/accounts/{id}/refresh
行 527  POST /admin/accounts/import
行 533  GET  /admin/pools
行 540  POST /admin/pools/{id}/state
行 543  POST /admin/pools
行 546  POST /admin/pools/{id}/remove
行 670  GET  /admin/logs?lines=N
行 684  GET  /admin/settings        ← 与行 687 的 POST
```

t21 当时核实（**那个时点是对的**）：
- `GatewayApp.cs` grep `/admin` → **0 命中**
- 旧版 `bin.js` / `gateway.js` grep `/admin` → **也是 0 命中**
- ⇒ 不是「旧版有、新版没搬」，而是**新版界面与网关之间的契约从未实现**

**当初真正指出的问题**是：**没有人验证过「带合法 key 打这些端点会得到什么」** ——
ui 的验收只验到「没 key 时回 401」，而 401 变成 200 之后、返回的数据能不能被界面渲染，无人验证。

#### 2.4.2 修复（t17）

新增 `src/BuddyDeck.Gateway/AdminEndpoints.cs`（**37,775 字节**），
`GatewayApp.cs:196-220` 挂载。**11 条路由全部覆盖**。

【已核实】我读到的结构：
- `AdminEndpoints.cs:90-91` 前缀判定；`:101-117` 按 `segments[1]` 分派
  `accounts` / `pools` / `logs` / `settings` 四个 area。
- 账号：`:131-136` 注释列出全部 5 个动词 + import；`:277-279` rename 走 `Vault.Write`（真落盘）。
- 分池：`:457-460` 注释列出四条行为；`:506-533` `ListPoolsAsync` 回本实例。
- 设置：`:688-738` GET/POST；`:720-738` 只改 `_settings` 内存字段。

#### 2.4.3 独立复验（**我自己跑的，不是转述**）

我没有采信 gateway 的结论，而是**自己起了一个真网关进程**验证：

```bash
# 自己造带金丝雀值的临时保险库（accessToken=sk-CURL-CANARY-MUST-NOT-LEAK）
# 起真网关：dotnet run --project src/BuddyDeck.Gateway -- --port 8793 --api-key test-key --vault <临时>
# 轮询 /healthz 就绪（无 Start-Sleep）→ 200, buildId=cbf6a0ba9cc7
python tools/verify_admin_endpoints.py 8793 test-key
```

**结果：52 项检查全部通过。** 关键几类【已核实】：
- 鉴权：无 key / 错 key 打 4 个 area → 全 401；`/healthz` 仍免鉴权 200
- 读端点：`/admin/accounts`、`/admin/pools`、`/admin/settings`、`/admin/logs` 全 200，
  且字段与 App 的 DTO 逐字对齐（`AccountSummary` 9 字段、`PoolSummary` 7 字段、`GatewaySettings` 5 字段）
- **脱敏**：`/admin/accounts` 不含 `accessToken` / `refreshToken` 的**原值**，**连键名都不存在**
  （我用的是**自己造的金丝雀值**，所以这条对任何保险库都成立）
- 写端点：`activate`/`rename`/`state` → 200；`rename` **真的改到了磁盘上的标签**
- 参数校验：`import` 畸形 JSON → 400；`settings` 非法端口 / 未知变体 → 400
- 501：`refresh`、新增池 → 501（见 2.4.4）

复验完成后已停掉临时进程、清理临时目录；**已确认 8787 仍在监听、未受影响**。

#### 2.4.4 两条**已知限制**（不是缺陷，但必须写进表）

**① 三个端点如实返回 501 而不是假装成功** —— 这是**有意设计**，后续不要当缺陷报：

| 端点 | 为什么 501 |
|---|---|
| `POST /admin/accounts/{id}/refresh` | 真刷新要打上游 token 接口 + 把新 token **原子写回**保险库（含滑动窗口），属凭证管理的完整职责；当前端点未注入 `UpstreamClient`（`AdminEndpoints.cs:328-346`） |
| `POST /admin/pools`（新增） | 进程内多池需要每池独立账号集 + 端口 + key = **多实例架构**（用户的「默认池/志强池」就是两个网关进程）（`:566-580`） |
| `POST /admin/pools/{id}/remove` | 删掉本实例唯一那个池 = 关掉网关自己，该由进程管理做（`:586-590`） |

作者的理由（`AdminEndpoints.cs:463` 注释）值得保留：
**「用户点『启动』什么也不会发生 —— 那比明确的 501 坏得多。」**

**② `/admin/settings` 的 POST 目前只改内存，不落盘。** 【已核实，我自己验的】
- `POST {"port":9999}` → 200，随后 `GET` 返回 `port: 9999`（内存变了）
- 但临时目录下**只有 `gateway-20261007.log`，没有任何设置文件** ⇒ 重启即丢
- 与 §2.3（M14）**同一条缺口**：界面改端口/API key → 重启网关会丢。
  **M14 仍应保留在缺失清单里。**

#### 2.4.5 教训（值得留在文档里）

**当初那条质疑是对的，而且它真的补上了一个缺口** —— 但**它的正确性有保质期**：
t21 写下的「全部数据操作不工作」在 t17 落地当天就失效了。

⇒ **对照表必须带时点。** 我此后给每条结论都标注了核实时间；
**凡标【已核实】的都要能被复验，而复验结果可能推翻它。**


### 2.5 关于 M10 与 `RequestLog.cs` 的区别【已核实】

`Gateway/RequestLog.cs`（396 行）记的是**请求元数据环形缓冲**（给界面「最近请求」用），
`usage.js` 记的是**成本账本**（JSONL，含 `credit` 实际扣费、缓存命中、按模型/账号聚合）。

**两者不能互相替代**：`RequestLog` 没有落盘聚合、没有 `credit` 聚合视图。
`SseSanitizer.cs:134` 的注释明确写了「将来记账拿到的就只有 token 没有 credit」——
说明作者知道要记账，但**账本本身还没做**。

### 2.6 ⚠ M20 User-Agent —— **优先级已上调**（t51 重估）

> **本条推翻 §3.3 原来的「优先级低」判断。** 触发原因是 **t48 的真机证据**：
> `/v3/config` **不带 UA 返回 400 code 12403**（`check ua, get coding copilot version error`）。
> ⇒ 「没有 UA 就拿不到模型目录」—— 这不是优化，是**功能可用性**。

#### 2.6.1 当前实际状态（读代码得出【已核实】，非实测）

| # | 事实 | 出处 |
|---|---|---|
| 1 | **`/v3/config` 现在不带 User-Agent** —— `if (!string.IsNullOrEmpty(userAgent))` 才加，而调用方传 `null` | `Core/UpstreamClient.cs:182-185`；调用点 `Gateway/GatewayApp.cs:405` |
| 2 | 该失败被**静默吞掉**（只 Debug 级日志）⇒ 回落到 `ModelCatalog.BuiltIn` 的 16 个编译内置模型 | `Gateway/GatewayApp.cs:409-418` |
| 3 | **`WorkBuddy/5.6.2` 只属于 Miora 那条线**，不是目录那条线 | `Core/MioraClient.cs:224`（用于 `/v2/api-keys`） |
| 4 | 聊天请求**也不带 UA** | `Core/UpstreamProtocol.cs:57-60`；网关传 `null` |

**⇒ 关键澄清**：「新版有 UA」这个印象是**错的**。有 UA 的是 Miora，目录与聊天都没有。

**⇒ 后果**：目录刷新必然 400，用户**永远只看到 16 个内置模型**，而真机上目录有 37 个
（t48 实测）。**而且因为静默，界面上看不出任何异常。**

#### 2.6.2 `WorkBuddy/5.6.2` 是「巧合可用」还是「契约」？

**结论：它是「巧合」，而且很可能连「可用」都不是 —— 因为它在错误的端点上。**

- **它不在目录这条线上**（上面事实 #3）。所以「真机上碰巧可用」这个描述需要修正：
  真正被验证可用的是 **Miora 的临时 key 端点**，不是 `/v3/config`。
- **旧版对目录用的完全是另一个值**：`CLI/2.63.2 CodeBuddy/2.63.2`
  （`workbuddy_api/src/upstream.js:26` 的 `CLIENT_UA`）。
- **而且旧版注释说 UA 决定拿哪份花名册**（`upstream.js:639-645`）：
  CLI 形态 → CLI 的模型集（即本工具能用的）；App 形态 → App 内部花名册。
  **⇒ 不只是「有没有 UA」，是「UA 的形态决定拿到哪份目录」。**
- **版本号本身**：旧版有探测逻辑（macOS bundle / Windows 注册表），
  **但探测失败也有编译常量兜底**（`client-identity.js:189-213`）。
  旧版注释自述「gateway ignores the version numbers for routing，
  these are shape requirements, not currency claims.」（`client-identity.js:13-15`）。
  ⇒ **旧版作者认为「形态是关键、版本号不是」—— 但这是旧版注释，不是对当前上游的实测。**

**⚠ 所以「版本号是否有意义」我判为【未知】** —— 关键实验（同形态换版本号是否仍 200）
我没有做（需真实凭据 + 消耗配额）。**证实路径已写进 `tools/check_ua_requirement.py`
的 `--live` 模式**（含 `WorkBuddy/0.0.1` 这个探针变体）。

#### 2.6.3 「UA 从哪来」的三个方案与代价

| 方案 | 代价 | 风险 |
|---|---|---|
| **A. 硬编码**（抄旧版的 `CLI/2.63.2 CodeBuddy/2.63.2`） | **~10 行**，改一处 | ⚠ **版本号会过期**。上游若某天校验版本下限，会**突然全部 400** —— 而旧版正是为此保留了探测逻辑 |
| **B. 配置项**（`--catalog-ua` / `WORKBUDDY_CATALOG_UA`） | ~20 行（加选项 + 文档） | 用户不知道填什么。**但**：出问题时可以不改代码绕过 —— 这是个真实优势 |
| **C. 探测已装 App 版本**（旧版做法） | **~300 行**（macOS bundle / Windows 注册表 / 版本号校验） | 跨平台分支多、要起子进程；**收益仅是「版本号真实一点」** |

**我的建议（决策材料，不是实现）**：**A + B 组合**。
- **A 提供默认值**（让「开箱可用」成立，这是当前最痛的：现在压根不可用）
- **B 提供逃生阀**（版本号过期时用户能改配置，不必等我们发版）
- **C 暂时不做** —— 它的收益（版本号真实）与成本（300 行跨平台探测）不成比例；
  **但如果 §2.6.2 那个「版本号是否有意义」的实验证明版本号被校验，C 的必要性立刻上升**
  —— 那时探测就不再是「让版本号好看」，而是「保证能用」。

⚠ **一个与 M20 无关但同类的问题**：`GatewayApp.cs:409-418` 把目录刷新失败**静默**吞掉，
只打 Debug 级日志。产物是对的（回落内置清单），**但「静默」让这个 400 藏了很久没人发现**。
**建议顺带把它提到 Warning 级** —— 这类「功能降级但界面无异常」的情形，日志是唯一的线索。

---

## §3 有意不做 / 不需要（含理由）

### 3.1 `paths.js` → **有意不做**

旧版 `stateHome()` 把状态都放 `~/.workbuddy-api`，理由是「卸载就是一个 `rm -rf`」。

新版不做独立模块，理由：
1. 网关侧状态只有**日志目录**（`GatewayOptions.ResolveLogDirectory`，行 171）与**保险库路径**（行 163）两处，各自已有明确归属，抽一层公共模块是过度设计。
2. 新版的保险库**刻意与旧版共用同一个文件**（`auth.cn.json` / `auth.ai.json`，`Variants.cs:44`）——
   因为要和正在运行的旧版网关、以及桌面 App 读同一份凭据。若照搬 `~/.workbuddy-api` 隔离，
   用户在新界面导入的账号旧网关看不见，**这是倒退**。
3. 所以「状态目录」这个概念在新架构下不再存在。**但注意**：`config.js` 的持久化（M14）
   仍然需要决定放哪 —— 建议与保险库同目录，而不是新造一个。

### 3.2 `index.js` → **不需要**

纯库入口，把 25 个模块的导出重新聚合，供 `import { ... } from "workbuddy-api"` 用
（`index.js:14-73`）。新架构下模块对应程序集/命名空间，引用由 MSBuild 处理，
**这个文件描述的问题在新版里天然不存在**。

### 3.3 `client-identity.js` 的版本探测部分 → **~~优先放弃~~ ⚠ 判断已修订（见 §2.6）**

> **⚠ t51 修订**：本节原来的「优先放弃」结论**只对「版本探测」这一块成立**，
> **但对「UA 本身」不成立** —— t48 的真机证据表明**没有 UA 就拿不到模型目录**（400 code 12403）。
> **完整的重估见 §2.6。** 下面保留原论证（它对于「要不要花 300 行做探测」仍然有效），
> 但**不要把「探测可以不做」误读成「UA 可以不给」**。

旧版会 `execFileSync` 跑命令读已安装 App 的版本（行 31 `tryExec`），
拼出 `WorkBuddy/<v> <product>/<v> CLI/<cli>` 形态的 UA（行 4-15 的注释解释为什么必须这个形态）。

新版做法（`UpstreamProtocol.cs:67-73`）：**写死** `X-IDE-Type: WorkBuddy` / `X-IDE-Name: WorkBuddy`，
`X-IDE-Version` 只在调用方传值时带上。

理由（**仅针对「要不要做版本探测」**）：
1. UA 的**形态**是硬要求，**版本号**不是 —— 旧版注释自己写了「The gateway ignores the version
   numbers for routing, so these are shape requirements, not currency claims.」（行 13-15）。
2. 探测要起子进程、跨平台路径各异，收益是「版本号真实一点」，**性价比极低**。
   ⚠ 但 **t51 补了一条反证**：若 §2.6.2 那个实验证明「版本号被校验」，
   探测就不再是「让版本号好看」而是「保证能用」，性价比结论会翻转。
3. **未决点（原【推断】，已补证实路径）**：若上游按 UA 分流花名册
   （`variants.js:30-33` 注释暗示 CN 的 UA 拿 CLI 花名册、国际版拿 App 花名册），
   则 `X-IDE-Version` 可能需要真实值。
   **证实路径**：起真网关（**需真实账号**）→ 用两个不同 `X-IDE-Version`
   （`5.5.6` 与 `0.0.1`）各发一次 `/v1/models` → 比对返回的模型集合。
   **若相同 ⇒ 推断不成立，M20 可永久关闭。**
   ⚠ 现状：`X-IDE-Version` **现在根本没人传**（`UpstreamProtocol.cs:69-72` 是
   `if (!string.IsNullOrEmpty(clientVersion))` 才加），所以这条即使成立也**当前无影响**。
   ⚠ **t51 补充**：旧版 `upstream.js:639-645` 的注释**已经写明**「UA 决定拿哪份花名册」
   （CLI 形态 → CLI 模型集；App 形态 → App 内部花名册）—— 这把上面那条从【推断】
   提升为**旧版作者的明确陈述**（仍不是对当前上游的实测）。
   **可直接复用的实验脚本**：`tools/check_ua_requirement.py --live --credential <token>`。

### 3.4 `bin.js` 的 CLI 子命令 → **有意不做（界面取代）**

`bin.js` 有 11 个命令（行 1594-1614：`serve`/`status`/`models`/`doctor`/`logout`/`config`/
`accounts`/`checkin`/`ccswitch`/`usage`/`search`）。

新版只保留 `serve`（`Program.cs`）。理由：新架构是 WPF 界面，用户在界面上操作而不是敲命令。
**但有两个例外值得重新考虑**：
- `doctor`（自诊断）：界面出问题时，用户能跑一个命令拿到诊断信息，比让他截图界面有用。
  ✅ **已证实（t27 复核）**：新版 `App/SelfTest.cs:8-9` 写着
  `诊断模式：BuddyDeck.App.exe --selftest <输出文件>` —— **确实是 `doctor` 的等价物**（界面版）。
- `status`：巡检用 —— 界面 Dashboard 页承担。
- ⚠ **但 `checkin` / `ccswitch` / `usage` / `search` 四个子命令没有界面等价物**
  （界面只有 9 个 ViewModel：`Accounts/Chat/Dashboard/AccountRow/Logs/Pools/PoolRow/Settings/Shell`，
  无对应页面）。其中 `checkin`(M11) 与 `ccswitch`(M15) 本身也未实现。
  **证实路径**：`grep -rn "checkin\|ccswitch" src/BuddyDeck.App/`

---

## §4 隐式能力（散落在别的文件里，最容易漏）

这一类**不在文件列表上**，所以最容易在移植时整块丢失。

### 4.1 读图 / vision —— 能力位置与覆盖度**均已核实**

- 旧版**没有** `vision.js`。图片处理散在：
  - `gateway.js:1421`：`part && typeof part === "object" && part.type === "image_url"`
    —— 【已核实】在 chat 请求体里识别图片部件。
  - `responses.js:38-46`：`input_image` → `image_url` 的转换，并处理 `file_id` 占位。
  - `upstream.js`：`prepareChatBody`（行 260）原样透传 content 数组 ——
    ✅ **已证实（t27）**：新版对应实现 `UpstreamProtocol.cs:196-340` 的 `Prepare`
    只碰 `stream` / `messages[].role` / `tool_choice` / `model`，**从不遍历 message 的 `content`**，
    所以图片部件原样通过。**验证命令**：`grep -n "content" src/BuddyDeck.Core/UpstreamProtocol.cs`
    （只出现在补 system 消息那一处）。
- 新版：`ConversationAffinity.cs:203` 的注释提到「只认文本部件。image_url 的 data URI 有几 MB」
  —— 【已核实】说明作者**知道**图片存在并**刻意跳过**它（派生会话键时）。
- **结论**：图片**透传**路径在新版是通的（`RequestPipeline.Prepare` 用可变 DOM 改，不丢未知字段
  —— `UpstreamProtocol.cs:159-163` 的注释明确说明了这个设计理由，且已随 t11 补了 `model` 参数）。
  但**没有任何一条端到端测试验证图片请求**。**建议补一条 E2E**，而不是新写模块。

### 4.2 429 恢复时间的两种形态 —— 【已核实】

旧版有**两套**解析：
1. `ratelimit.js:51` `parseRateLimitReset` —— 从**错误文案**里抠
   `2026-09-25 17:07:48 UTC+8` 这种人类可读时间（正则见行 56-57）。
2. `gateway.js` 的 `ResetAtFromBody` —— 从 JSON 的 `resetAt` 字段读。

新版 `GatewayApp.cs` 只实现了**第 2 种**（`ResetAtFromBody`）。
**第 1 种缺失**。影响：上游只回文案（实测抓到过，见 `ratelimit.js:8-12` 的原文引用）时，
新版会**退化成默认冷却 5 分钟**（`AccountPool.cs:63` `DefaultCooldown`），
而真实恢复可能是 1 小时 —— 期间该模型被白跳过。**这是 M13，值得修，改动很小。**

### 4.3 会话粘性 —— 【已核实】新版已实现，且旧版确实散落

- 旧版**没有** `affinity.js`。会话粘性散在 `gateway.js` / `upstream.js` 里
  （HANDOVER 第 281 行提到「旧版网关的做法，加 `d-` 前缀」）。
- 新版**有** `Core/ConversationAffinity.cs`（472 行），且 `GatewayApp.cs:452-466` 已接线
  【已核实】。这属于**新版优于旧版**（收敛成了可测的独立类）。
- ✅ **原担忧已被推翻（t27 复核）**：我一度怀疑「传 `rawBody` 而非 `prepared` 会导致
  同一会话换了参数就跳号」。**读了实现后确认不成立** ——
  `ConversationKey.Derive`（`ConversationAffinity.cs:81-104`）**不是对整个 body 做哈希**，
  它只取 `conversation_id`（若有）或 **`system` + 首条 `user`**（`:120-123`）。
  传 `rawBody` 无害，因为函数内部只提取它要的那两个字段。
  **且与 HANDOVER:281-282 逐条对上**（含 `d-` 前缀，`:63`）。
  > **验证命令**：`grep -n "DeriveFromMessages\|DerivedPrefix" src/BuddyDeck.Core/ConversationAffinity.cs`
  > **教训**：这条我从 t21 挂到现在，**从没验过**，而它当时看起来「标了【推断】就很规范」。

### 4.4 其他散落能力

| 能力 | 旧版位置【已核实】 | 新版 | 判断 |
|---|---|---|---|
| 空 `function_call` 清洗 | `gateway.js:212-216`（`sanitizeChunk`） | ✅ `SseSanitizer.cs:107-117` | 已移植 |
| usage 裁剪成 3 字段 | `gateway.js:227` | ✅ `SseSanitizer.cs:137-147` | 已移植（且新版**多修了一个 bug**：先 `DeepClone` 再删，见该文件行 133 注释） |
| `developer` → `system` | `upstream.js:207` `normalizeDeveloperRole` | ✅ `UpstreamProtocol.cs` `NormalizeDeveloperRole` | 已移植 |
| `tool_choice` 归一 + `none` 时删 tools | `upstream.js:217` `normalizeToolChoice` | ✅ `UpstreamProtocol.cs` `NormalizeToolChoice` | 已移植 |
| 国际版补 system 首条 | `upstream.js:283` `prepareInternationalChatBody` | ✅ `UpstreamProtocol.cs:195-199` `EnsureSystemFirst` | 已移植 |
| `X-No-*` 显式空身份 | `upstream.js:183` `billingHeaders` 一族 | ✅ `UpstreamProtocol.cs:110-138` `ApplyIdentity` | 已移植 |
| 错误文案抽取（`displayMsg`） | `upstream.js:100` `extractDisplayErrorMessage` | ✅ `GatewayApp.cs` `Summarize` | 已移植 |
| 模型名带前缀的别名解析 | `protocol.js:53` `createModelResolver`（`workbuddy/glm-5.3` → `glm-5.3`） | ⚠ `ModelCatalog.Resolve` 只做「在清单里就用、不在就落默认」 | **缺前缀剥离**（缺失本身【已核实】，`ModelCatalog.cs:83-88` 无斜杠处理）。**影响面待验**：`Resolve` 对任何不在清单里的字符串都返回 `DefaultModel` ⇒ 若默认模型恰好等于请求的那个则"看起来正常"，否则**静默换模型**。**证实路径**：发一次 `{"model":"workbuddy/hy3"}`，观察上游实际收到的 `model` |
| Anthropic 客户端指纹行剔除 | `anthropic.js:55-65`（`x-anthropic-*header:` 会让上游回 400/11128） | ❌ 无 | 随 M1 一起做；**这条是 Claude Code 能用的前提** |
| 图片 URL 1 小时失效 → 网关中转 | `gateway.js:661-665` 的注释 | ❌ 无 | 随 M9 |
| SSE keepalive 注释行 | `agent.js:62-67`（工具执行期间每 10s 发 `: keepalive`） | ❌ 无 | 随 M7 |
| 传输失败分类（`status: 0`） | `upstream.js:67` `classifyUpstreamError` | ✅ `UpstreamClient.cs` `Classify` + `Status = 0` | 已移植 |

---

## §5 建议实施顺序（按依赖排，非按重要性）

### 阶段 0：先核实，再动工（0.5 人日）

**不要跳过这一步。** 下面每条都会改变后续排期：

| # | 待核实 | 影响 |
|---|---|---|
| 0.1 | `App/SelfTest.cs` 是不是 `doctor` 的替代 | 决定 §3.4 的判定 |
| 0.2 | ~~界面 SettingsPage 如何持久化设置~~ → **已核实（§2.3）**：界面调 `/admin/settings`（t17 已补路由），**但 POST 只改内存不落盘** | **M14 仍要做** |
| 0.3 | `ConversationAffinity` 用 `rawBody` 派生键是否与 HANDOVER 语义等价（§4.3） | 可能是个 bug |
| 0.4 | ~~旧版 UA 是否真的按 variant 分流花名册~~ | ✅ **已由 t51 查证**：旧版 `upstream.js:639-645` 明确写了 UA 决定拿哪份花名册；且 CN 目录用的是 `CLI/2.63.2 CodeBuddy/2.63.2`（**不是** WorkBuddy 形态）。**§3.3 已据此修订，M20 优先级上调**（见 §2.6） |
| 0.5 | t20 的 inScope 边界（是否已含 M5–M9） | 决定 §2.2 里哪些不用重派 |
| ~~0.6~~ | ~~§2.4 的 `/admin/*` 是否已有人在做~~ | **已解决**：t17 已补齐，我已独立复验 52 项全过 |
| ~~0.7~~ | ~~界面那几个页面之前是怎么「验证通过」的~~ | **已解决**：正是这个盲区促成了 t17 |

### 阶段 0.5：~~建议插到最前面做的一项~~ → **已由 t17 完成**

**`/admin/*` 路由族（§2.4）已完成并复验。** 原判断「没有任何现存任务覆盖它」促成了 t17 立项；
现在 11 条路由全部到位（`AdminEndpoints.cs`，37,775 字节），52 项检查全过。

**剩余待办**：§2.4.4 的两条已知限制 ——
① 三个 501 端点（`refresh` / 新增池 / 删池）是**有意**的，需产品决策是否要做；
② **M14 设置落盘仍需做**（见 §2.3）。

> 注意：阶段 2/3 的 `/v1/*` 路由与已完成的 `/admin/*` **不冲突**，
> 但早期都写 `GatewayApp.cs`。**现在 t17 已把管理面拆进独立文件 `AdminEndpoints.cs`** ——
> 这是个好先例，**建议阶段 2/3 照做**（各自一个 `*Endpoints.cs`），
> 这样 §5 风险 1 的「单点瓶颈」自然化解。

### 阶段 1：无依赖、可全并行（可同时开工）

这些项**互不碰同一文件**，且都不依赖别人：

```
┌─ 线 A：~~M10 用量账本~~          → ✅ 已由 observability 完成（Core/UsageLedger.cs + Gateway/UsageEndpoints.cs）
├─ 线 B：M11 每日签到            → 新文件 Core/Checkin.cs
├─ 线 C：M12 账号加密导出/导入    → 新文件 Core/AccountTransfer.cs
├─ 线 D：M13 429 文案时间解析     → 改 Core/AccountPool.cs 或新小文件
├─ 线 E：M19 模型目录落盘缓存     → 改 Gateway/ModelCatalog.cs
├─ 线 F：M17+M18 probe/计费解析   → 改 Core/UpstreamClient.cs
└─ 线 G：M14 设置落盘            → 改 Gateway/AdminEndpoints.cs + GatewayOptions.cs
```

**并行约束**（落笔前重列过文件，确认以下三者不重叠）：
D 改 `AccountPool.cs`、E 改 `ModelCatalog.cs`、F 改 `UpstreamClient.cs` → **可并行**。
✅ **线 G（M14）的串行风险已排除（t27 复核）**：t17 状态为 `completed`，
且 `AdminEndpoints.cs` 最后一次修改是 07:11（距复核时点已 1.5 小时无变动）⇒ **可以改**。
**证实命令**：`Get-Item src\BuddyDeck.Gateway\AdminEndpoints.cs | Select LastWriteTime`
+ 查任务列表里 t17 的状态。

> ⚠ 上面这份线表**也会过期**。M10 我初稿时列为「缺失」，复核时发现 observability 已做完 ——
> **开工前请重新 grep 一次。**

### 阶段 2：协议翻译层（依赖阶段 1 的**无**，但内部有序）

```
M4（protocol 公共零件）
   ↓ 必须先做，M1/M2 都用
M1（Anthropic Messages）  ∥  M2（Responses）
   ↓                            ↓
M3（count_tokens）           （无后续）
```

- **M4 → M1/M2 是硬顺序**：`toAnthropicStopReason`、`writeEvent`、`beginSse` 是两者的公共依赖
  （`anthropic.js:20-27` 与 `responses.js:19-24` 都 `import` 自 `protocol.js`）。
- **M1 与 M2 可并行**：不同文件、不同事件流语义。
- **M1/M2 会碰 `GatewayApp.cs` 加路由** —— ⚠ **当前 `GatewayApp.cs` 被 t16 占用**。
  这两条**必须等 t16 收口**，或改为「先写独立 handler 类，路由由 t16 之后统一挂」。
  **这是本阶段最大的排期风险。**

### 阶段 3：媒体与联网（t20 覆盖，但要补顺序）

```
M5（miora 客户端）  ∥  M6（工具定义）  ∥  M8（搜索/抓取）
        ↓
   M7（agent 循环）  ← 强依赖 M5 + M6
        ↓
   M9（文件中转 + HMAC 签名）  ← 强依赖 M5
```

- **M5 / M6 / M8 可并行**（不同文件）。
- **M7 必须等 M5+M6**：它要调 Miora，且要注入 M6 定义的工具。
- **M9 必须等 M5**：没有生成结果就没有文件可中转。
- 三者都要加 `/v1/*` 路由 → **建议照 t17 的做法各拆一个 `*Endpoints.cs`**（见 §6.4），
  这样不必都堵在 `GatewayApp.cs` 上。

### 阶段 4：可选 / 待决策

| 项 | 前置决策 |
|---|---|
| **M15** CC Switch 集成 | **要不要引入 SQLite 依赖？** 旧版零依赖，新版若引 `Microsoft.Data.Sqlite` 要评估打包体积 |
| **M16** 桌面凭据真实解密 | 需平台原生 helper（Windows 上是 `WorkBuddy.exe` 的导出）。**建议先做「用户手动导入」路径，解密延后** |
| **M20** UA 来源 | ⚠ **已升级为待办，不再是「可选」** —— t48 真机证据：无 UA 则 `/v3/config` 400 code 12403。**最小修法约 10 行**（给目录一个 UA）；完整探测（~300 行）是否必要取决于「版本号是否有意义」的实验，见 §2.6 |

### 关键排期风险汇总

1. ~~**`GatewayApp.cs` 是单点瓶颈**：M1/M2/M3/M7/M9、t20 的五条、§2.4 的 11 条 `/admin/*` 全要改它。~~
   **→ t17 已经化解了管理面那一半**：`/admin/*` 拆进了独立的 `AdminEndpoints.cs`（785 行）。
   **剩余风险**：M1/M2/M3/M7/M9 + t20 仍会往 `GatewayApp.cs` 加路由。
   **建议照 t17 的先例**，业务面也按 `AnthropicEndpoints.cs` / `MediaEndpoints.cs` 等拆分。
2. **阶段 1 的六条线（B–G）都不碰 `GatewayApp.cs`** —— **建议立刻并行开工**，不必等任何前置。
3. M4 虽小（~120 行）但**是 M1/M2 的硬前置**，应尽早。
4. ~~**§2.4（`/admin/*`）不在任何现存任务的 inScope 里** ——如果不主动派，它会一直没人做。~~
   **→ 已由 t17 完成并复验（52 项全过）。** 这条当初确实是本表最有价值的发现，
   但它**只在 07:12 到 07:11 之间成立** —— 我落笔时它已经被修了（见 §2 开头的警告框）。

---

## §6 证据说明

### 6.1 【已核实】清单（我读到了具体代码）

| 结论 | 依据 |
|---|---|
| 旧版 25 个文件、行数 | `Get-ChildItem -Recurse -Filter *.js` + 逐文件 `(Get-Content).Count` |
| 旧版 11 条路由及行号 | `read gateway.js:505-672`，逐条 `if (req.method === ...)` |
| 新版 4 条路由及行号 | `grep GatewayApp.cs` → 行 146/168/176/182 |
| `checkin.js` 完整逻辑与端点 | `read checkin.js:1-151`（`/v2/billing/meter/*`） |
| `usage.js` 完整逻辑 | `read usage.js:1-236`（JSONL + `aggregate` + `summarizeUsage`） |
| `transfer.js` 加密方案 | `read transfer.js:1-55`（scrypt N=16384 + AES-256-GCM） |
| `ccswitch.js` 库结构 | `read ccswitch.js:1-60`（SQLite `cc-switch.db`，`(id, app_type)` 复合键） |
| `agent.js` 循环 + 缓冲窗口 + keepalive | `read agent.js:1-70`（`FLUSH_THRESHOLD` = 40 字符） |
| `media-tools.js` 工具注入动机 | `read media-tools.js:1-60`（`TOOL_PREFIX = "workbuddy_"`） |
| `anthropic.js` 指纹行剔除 | `read anthropic.js:1-75`（`CLIENT_FINGERPRINT_LINE` 正则，行 55） |
| `responses.js` 翻译结构 | `read responses.js:1-70` |
| `protocol.js` 全部导出 | `read protocol.js:1-147`（`estimateTokens` 行 85、`pickerDefaultModel` 行 29） |
| `ratelimit.js` 文案时间正则 | `read ratelimit.js:51-60` |
| `paths.js` 状态目录设计 | `read paths.js:1-91` |
| `config.js` 持久化 | `read config.js:1-70`（`saveConfig` 行 61） |
| `variants.js` UA 分流说明 | `read variants.js:1-50`（注释行 29-37） |
| `client-identity.js` 版本探测 | `read client-identity.js:1-45`（`tryExec` 行 31） |
| `index.js` 是纯库入口 | `read index.js:1-73`（全部是 `export ... from`） |
| `bin.js` 11 个命令 | `grep bin.js` → 行 1594-1614 的 `case` |
| 新版无 `checkin`/`usage`/`miora`/`agenttool` 等 | `grep _next/src` 搜 `checkin|usage.jsonl|recordUsage|Miora|agenttool|video` → 只命中 2 处注释 |
| 新版 `SseSanitizer` 保留 credit 的意图 | `read SseSanitizer.cs:133-147` 注释 |
| 新版 `ConversationAffinity` 跳过 image_url | `grep ConversationAffinity.cs` → 行 203 注释 |
| **界面调用 11 条 `/admin/*` 路由**（§2.4） | `grep IGatewayClient.cs` → 行 507/514/517/520/523/527/533/540/543/546/670/684/687 |
| ~~**网关侧无 `/admin` 路由**~~ | **已作废**：初稿时 grep `GatewayApp.cs` 0 命中属实，但 t17 于 07:11 已落 `AdminEndpoints.cs`（37,775 B），我的清单未重列 |
| **11 条 `/admin/*` 现已全部实现** | `read AdminEndpoints.cs:90-117`（前缀判定 + accounts/pools/logs/settings 四区）；`:131-136`、`:457-460` 路由注释；`grep 501` → `:346`、`:580`、`:589` |
| **`/admin/*` 端到端 52 项检查全过** | **我自己起真网关复验的**：临时保险库（金丝雀 `sk-CURL-CANARY-MUST-NOT-LEAK`）+ `--port 8793 --api-key test-key` + 轮询 healthz 就绪 → `python tools/verify_admin_endpoints.py 8793 test-key` → 「52 项检查全部通过」 |
| **settings POST 只改内存不落盘** | 起真网关 → `POST {"port":9999}` → 200 → `GET` 返回 `port:9999`；运行目录下**只有 `gateway-*.log`，无设置文件** |
| **M10 用量账本已实现** | `grep class UsageLedger\|UsageAggregation` → `Core/UsageLedger.cs:383`、`:862`；`Gateway/UsageEndpoints.cs:123-272`（含 `credit`、by-model/by-account） |
| **M13 文案时间解析仍缺失** | `grep AccountPool.cs` 搜 `parseRateLimitReset\|UTF\+8\|文案` → **0 命中** |
| **M1/M2/M5–M9 路由仍缺失** | `grep GatewayApp.cs` 搜 `/v1/messages\|/v1/responses\|images\|video\|search\|webfetch` → **0 命中** |
| `ModelCatalog.Resolve` 无前缀剥离 | `read ModelCatalog.cs:83-88`（只有「在清单里→原样，否则→默认」两条分支，**无斜杠处理**） |
| 反向核对：新版无凭空多出的文件 | 逐文件比对 §1.2 |

> **自我更正（两次，都写在这里以便追溯）**：
>
> 1. **I7（模型前缀剥离）**：初稿标【推断】，重读 `ModelCatalog.cs:83-88` 后确认代码里确实没有前缀处理 → 改判【已核实】。
> 2. **流程错误**：初稿的「M10 缺失」与「§2.4 `/admin/*` 两头都不存在」**都是过期结论**。
>    `AdminEndpoints.cs`（07:11）与 `UsageLedger.cs`（07:17）**都落在我写表之前**，
>    但我在开头列过一次清单后就再没重列，导致把已存在的东西写成缺失。
>    **这是并发环境下的真实失败模式，已写进 §2 开头的警告框。**

### 6.2 【推断】清单 —— 每条都带**证实路径**与**当前状态**

> **团队硬规则（队长 2026-10-07 采纳）**：
> **凡标【推断】，必须同时写清证实路径（一条命令 / 一个测试）。
> 没有证实路径的推断，不许留在文档里当依据。**
>
> 这条规则的由来：我原来标了【推断】就以为尽责了，**其实只是把"没验证"写得更体面一点** ——
> 下一个人读到时只会看到「有证据等级分级」，不会意识到**这些条目从写下那天起没人验过**。
>
> **本轮（t27 收尾）逐条处理结果：已证实 5 条（其中 2 条推断被推翻）、给出路径待验 4 条、删除 1 条。**

| # | 原推断 | 状态 | 证实路径 / 结论 |
|---|---|---|---|
| **I1** | `prepareChatBody` 对 content 数组原样透传（图片能过） | ✅ **已证实** | 读 `UpstreamProtocol.cs:196-340`：`Prepare` 只碰 `stream` / `messages[].role` / `tool_choice` / `model`，**从不遍历 message 的 content** ⇒ 图片部件（`image_url`）原样通过。**验证命令**：`grep -n "content" src/BuddyDeck.Core/UpstreamProtocol.cs`（只出现在补充 system 消息处） |
| **I2** | `App/SelfTest.cs` 可能是 `doctor` 的替代 | ✅ **已证实** | 读 `SelfTest.cs:8-9`：`诊断模式：BuddyDeck.App.exe --selftest <输出文件>` ⇒ **确实是 `doctor` 的等价物**（界面版） |
| ~~**I3**~~ | ~~界面自行持久化设置~~ | ✅ 已改为【已核实】 | §2.3：界面调 `/admin/settings`；**实测 POST 只改内存不落盘**（见 §2.3） |
| **I4** | 会话键用 `rawBody` 派生 ⇒ 与 HANDOVER 语义**不等价** | ❌ **推断被推翻** | 读 `ConversationAffinity.cs:81-104`：`Derive` **不是**对整个 body 做哈希 —— 它只取 `conversation_id`（若有）或 **`system` + 首条 `user`**（`:120-123`）。传 `rawBody` 无害，因为函数内部只提取相关字段。**与 HANDOVER:281-282 逐条对上**（含 `d-` 前缀 `:63`）。<br>**验证命令**：`grep -n "DeriveFromMessages\\|DerivedPrefix" src/BuddyDeck.Core/ConversationAffinity.cs` |
| **I5** | 上游按 UA 分流花名册 ⇒ `X-IDE-Version` 可能需要真实值 | 🔶→✅ **前半已证实（t51）／版本号部分仍未知** | **已证实（读旧版源码）**：`workbuddy_api/src/upstream.js:639-645` 的注释**明确陈述**「gateway splits `/v3/config` by client identity，CLI 形态给 CLI 花名册、App 形态给 App 内部花名册」；且 CN 目录实发 `CLI/2.63.2 CodeBuddy/2.63.2`（`upstream.js:26`）。⇒ **「UA 决定拿哪份目录」成立**（旧版作者的陈述，仍非对当前上游的实测）。<br>**仍未知**：**版本号本身是否被校验**。**证实路径**（已脚本化）：`python tools/check_ua_requirement.py --live --credential <token>` —— 对 `/v3/config` 逐个试 `不设UA` / `WorkBuddy/5.6.2` / `CLI/2.63.2 CodeBuddy/2.63.2` / `WorkBuddy/0.0.1`，看哪个 200、模型数是否随 UA 变化。⚠ **我未跑**（消耗真实配额）。 |
| **I6** | t20 的 inScope 边界 | ✅ **已证实** | `grep` 新建文件：**没有任何** `Miora*/Media*/Search*/Agent*/Anthropic*/Responses*/Image*/Video*` 命名的源文件 ⇒ **M5–M9 确实都还没实现**，t20 未覆盖 |
| **I7** | 前缀剥离缺失的「影响面小」 | ⏳ **待验（需实测客户端）** | 缺失本身【已核实】（`ModelCatalog.cs:83-88` 无斜杠处理）。**证实影响面的路径**：构造 `{"model":"workbuddy/hy3"}` 发一次 → 观察是否落默认模型。**已确认的行为**：`Resolve` 对任何不在清单里的字符串都返回 `DefaultModel` ⇒ **只要默认模型恰好等于请求的那个，就"看起来正常"；否则静默换模型**。 |
| **I8** | 界面取代了 `bin.js` 的 CLI 子命令 | ⏳ **部分证实** | 界面确有 9 个 ViewModel：`Accounts/Chat/Dashboard/AccountRow/Logs/Pools/PoolRow/Settings/Shell` ⇒ **accounts / models / config / status 有界面等价物**。**但 `doctor` 之外，`checkin`/`ccswitch`/`usage`/`search` 四个子命令的界面等价物不存在**（界面无对应页面，且 M11/M15/M10-CLI 本身也没做）。**证实路径**：`grep -rn "checkin\\|ccswitch" src/BuddyDeck.App/` |
| **I9** | `App/**` 对应旧版 `WorkBuddyPanel` | ⏳ **无法在本次范围证实** | 旧面板源码**不在本任务只读范围**。**证实路径**：读 `D:\workfandaiapi\WorkBuddyPanel\Views\*.xaml` 与 `ViewModels\*.cs`，与 `_next/src/BuddyDeck.App/` 逐页对照。**在没做之前，§1.2 那一行的对应关系只能当"结构上合理"读，不是核实过的映射。** |
| ~~**I10**~~ | ~~`/admin/*` 全部 404~~ | ✅ 已解决 | t17 补齐，我独立复验 52 项全过（§2.4.3）。**推断方向对，结论已被推翻** |
| **I11** | 三个 501 端点会被误当缺陷 | ⏳ **待验（需观察后续）** | 我在 §2.4.4 标了「有意」，且 `AdminEndpoints.cs:463` 代码注释里也有说明。<br>**证实路径**：观察是否有人把 501 当缺陷开工单；若有，说明**只在文档里标注不够**，需要在端点返回值里也带上理由字段。 |
| ~~**I12**~~（原 I7 的旧编号） | — | 🗑️ **已删除** | 重复项，无独立信息 |

### 6.2.1 本轮处理小结

```
已证实（改判【已核实】）     2 条：I1、I2、I6   ← 其中 I4 是"推断被推翻"
推断被推翻                    1 条：I4（会话键其实与 HANDOVER 一致）
给出证实路径、待验            4 条：I5、I7、I8、I11
无法在本次范围证实            1 条：I9（需旧面板源码，不在只读范围）
已解决（前轮）                2 条：I3、I10
删除（无信息量）              1 条：I12
```

**最重要的一条**：**I4 被推翻了**。我原来担心「用 `rawBody` 派生会话键会导致同一会话换了参数就跳号」——
读完 `ConversationAffinity.cs:81-104` 后确认**不成立**，因为 `Derive` 内部只提取 `system` + 首条 `user`，
不是对整个 body 做哈希。**这条我从 t21 挂到现在，从没验过，而它当时看起来"标了【推断】就很规范"。**


### 6.3 本次**没有**做的事（边界声明）

- **没有**修改 `workbuddy_api/` 与 `WorkBuddyPanel/` 下任何文件（全程只读）。
- **没有**修改 `_next/src/` 下任何代码（本任务是规划）。
- **没有**读 `WorkBuddyPanel/` 的源码 —— 所以 §1.2 里 `App/**` 的对应关系未逐文件核实（I9）。
- **没有**运行旧版网关或任何真实上游请求。
- 所有上游协议结论来自**读代码**，不是实测。

### 6.4 给下一手的一句话

**开工前先跑 `python tools\check_parity.py`** —— 本表的「缺失」项可能已经被别人做了。
M10（用量账本）与 §2.4（`/admin/*`）就是两个实例：**它们在我落笔前后已经被实现**，
我因为没重列清单而误报为缺失。**这份表的价值在于列出「旧版有什么、为什么这么判」，
而不在于「此刻还缺什么」—— 后者交给 §7 的脚本。**

---

## §7 可重跑检查器（`tools/check_parity.py`）—— t27

> **本表是解释，脚本是事实。冲突时以脚本为准。**

### 7.1 用法

```bash
cd D:\workfandaiapi\_next
python tools\check_parity.py            # 人读（默认，带颜色）
python tools\check_parity.py --json     # 给 CI / 界面读
python tools\check_parity.py --no-color # 关颜色（日志重定向时用）
python tools\check_parity.py -v         # 连已实现项也列出来
```

退出码：**0 = 没有缺口**；**1 = 有缺口**。
（⚠ `[待人工]` **不**导致非零退出 —— 它是「需要人看」，不是「检查失败」。）

### 7.2 四态输出

| 态 | 含义 |
|---|---|
| `[已实现]` | 找到实现特征，附命中位置 |
| `[仍缺失]` | 没找到，且**知道该找什么** |
| `[待人工]` | **grep 判不了**，附「为什么判不了 + 该怎么确认」 |
| `[有意不做]` | §3 判过的项，**不参与缺口统计** |

### 7.3 ⚠ 设计上最重要的两条（改脚本前必读）

**① 不能 naive grep ——「提到过这个词」≠「做过了」**

本任务开工时的实测证据：

```
$ grep "checkin" src/**/*.cs  →  4 命中
    Vault.cs:74            [JsonPropertyName("lastCheckinAt")]   ← 字段名
    Vault.cs:10            注释
    AdminEndpoints.cs:426  LastCheckinAt = ...                    ← 同为字段
真正的签到功能要调 `billing/meter/daily-checkin` → grep 得 0 命中 ⇒ 确实仍缺失
```

⇒ 所以每条检测特征必须是**它必须产生的外部效果**（上游端点路径 / 路由字面量 / 类型定义），
**绝不选模块名、类名的普通词**。每条特征下的 `why` 字段就是在回答
「为什么它不会被偶然命中」。

**这是同类错误的第三个变体**：
```
清单过期     → 对世界的「记录」没被验证
终端撒谎     → 对世界的「观测」没被验证（PowerShell 把好文件显示成乱码，被误报为编码损坏）
naive grep   → 对世界的「检索」没被验证（把"提及"当"实现"）
共同点：拿来当依据的东西，本身没被验证过。
```

**② 「沉默的判定 = 下一个假绿」**

判不了的项**必须显式出现在 `[待人工]` 那一档**，不许静默消失。
一个不报错的检查器，会让下一个人以为「没消息 = 没问题」。

### 7.4 对照组（自检）

脚本对**已知已实现**的项（聊天转发 / token 刷新 / 模型目录 / 用量账本 M10）
独立检测一遍，**它们必须报 `[已实现]`**。

若对照组失败 → **说明检查器本身可疑，先修检查器再看别的结论**。
（附：M10 作为「正向自证」—— `UsageLedger.cs` 已落盘，脚本必须能自动报出它已实现。）

### 7.5 已知边界（诚实声明）

- **命名相关的弱特征**：M13（`parseRateLimitReset`）、M18、M19 用的是旧版函数名，
  新版若实现会用不同命名 → **这些项报 `[仍缺失]` 时需人工复核**，脚本已在条目里标注。
- **判不了的控制流**：M7（agent 循环）、M14（设置落盘）、M4（公共零件）
  是**行为**而非字符串，脚本**故意不给特征**、归入 `[待人工]`。
- **跨仓库部分只列举不下结论**：旧版 25 个 `.js` 会被列出，但
  「旧版有 A.js」不等于「新版必须有 A」——判断在 ②③ 两档。
  （不过**列举旧版模块是脚本不可被 C# 测试替代的能力**：
  `dotnet test` 无法读另一个仓库的文件。）


**其次**：`GatewayApp.cs` 早期是单点瓶颈（M1/M2/M3/M7/M9 + t20 的 5 条都曾要改它），
**t17 已经示范了正确的解法** —— 管理面拆进独立文件 `AdminEndpoints.cs`。
**建议阶段 2/3 照做**，各自一个 `*Endpoints.cs`，瓶颈自然化解。

**最后**：阶段 1 的六条线（B–G）都不碰 `GatewayApp.cs`，**现在就能并行开工**。
