# 本地补丁与上游对照（同步前必读）

本文档回答一个问题：**`workbuddy_api/` 里哪些是上游核心，哪些是我自己的改动？**

上游 = [`corrinehu/dsh-workbuddy-connect`](https://github.com/corrinehu/dsh-workbuddy-connect)
（DSH 插件，TypeScript）。本工具是它的**独立 JS 重写**：认证与上游协议提取自
同一条链路，但网关、多协议适配、多账号、画图等是本项目自己的东西。

> **为什么要这份文档**：上游更新时，你不能整目录覆盖——那样会抹掉本地 feature；
> 也不能凭感觉挑——那样会漏掉协议修复。先看这里的分类，再动手。

---

## 0. 三条铁律

1. **上游核心只做"移植"，不改语义。** 改了就要在第 3 节登记，否则下次同步会
   把它当"上游又变了"而覆盖回去。
2. **本地 feature 绝不依赖上游文件内部结构。** 只通过 `src/index.js` 导出的
   公共接口使用上游核心，这样上游重构时本地 feature 不用跟着改。
3. **同步只按第 5 节的流程走**，不要 `git checkout` 覆盖整个目录。

---

## 1. 当前同步基线

| 项目 | 值 |
|---|---|
| 上次完整同步的上游版本 | **v0.6.1**（2026-09-24） |
| 本轮同步 | **v0.7.1 的协议部分**（3 项，见第 3.1 节） |
| 本工具版本 | `0.2.0`（`package.json`） |
| 尚未跟的上游改动 | 见第 6 节 |

`docs/MAINTENANCE.md` 的"同步历史"记录每次同步的版本与内容；本文档记录
**结构性的归属关系**，两者配合看。

---

## 2. 文件对照表

### 2.1 上游核心（有上游对应文件，移植而来）

改动这些文件 = 可能偏离上游。同步时**逐行对比**，不要整文件覆盖。

| 本地文件 | 上游对应 | 移植程度 |
|---|---|---|
| `src/auth.js` | `src/auth.ts` | 高度忠实；本地去掉了 DSH 状态目录，换成 `~/.workbuddy-api` |
| `src/desktop-credential-protection.js` | `src/desktop-credential-protection.ts` | 解密算法逐字节忠实；**二进制定位被简化**（见 3.3） |
| `src/client-identity.js` | `src/client-identity.ts` + `src/app-version.ts` | 忠实 + 本地补了 Windows 注册表/安装树读取（上游走 DSH 缓存文件） |
| `src/upstream.js` | `src/upstream.ts` | 协议部分忠实；**本地加了重组 SSE、effort 探测原语**（见 3.4） |
| `src/variants.js` | `src/variants.ts` | 描述符思路一致；字段不同（本地是 `chatBase`/`billingBase`，上游是 `statusPath`/`probePath`，因为本地没有 DSH Web 路由） |

### 2.2 本地独有（上游**没有**对应文件，不会被上游覆盖）

这些是本项目自己的 feature，同步时**完全不用管**，也不可能从上游"同步"到：

| 本地文件 | 作用 |
|---|---|
| `src/gateway.js` | 本地 OpenAI 兼容网关（含回环/Host/密钥三层安全边界） |
| `src/anthropic.js` | Anthropic Messages ←→ Chat 适配（Claude Code 用） |
| `src/responses.js` | OpenAI Responses ←→ Chat 适配（Codex 用） |
| `src/protocol.js` | 三种协议共用零件：模型别名、token 估算、SSE |
| `src/ratelimit.js` | 限流识别 + 自动换模型 |
| `src/accounts.js` | 多账号保险库 |
| `src/transfer.js` | 账号跨机加密搬运 |
| `src/checkin.js` | 每日签到 |
| `src/agent.js` | agent 循环（工具执行） |
| `src/media-tools.js` | 媒体工具注入 |
| `src/miora.js` | 画图（文生图 / 改图） |
| `src/usage.js` | 用量账本 |
| `src/ccswitch.js` | CC Switch 集成 |
| `src/config.js`、`src/paths.js`、`src/build-id.js` | 配置、状态目录、构建指纹 |
| `src/bin.js` | CLI（上游 `bin.ts` 只是 DSH 诊断入口，本地扩展成完整 CLI） |
| `scripts/verify.mjs`、`scripts/test-*.mjs` | 本地验证脚本（上游没有 `verify.mjs`） |
| `docs/*.md` | 本文档体系 |

### 2.3 上游有、本地**故意不跟**的部分

这些是 DSH 宿主专属，本地作为独立工具没有对应物，**永远不用同步**：

| 上游文件/概念 | 为什么不跟 |
|---|---|
| `src/index.ts`（cordis 插件）、`src/adapter.ts` | DSH 插件宿主接入层 |
| `src/shim.ts`、`src/web-status.ts`、`src/probe-route.ts`、`src/status-paths.ts` | DSH Web 路由与状态页 |
| `src/client/*`（`.tsx`） | DSH 浏览器 UI |
| `src/update.ts`、`src/update-route.ts`、`src/client/update-store.ts` | 插件自身的更新提醒（npm/GitHub 元数据） |
| `src/legacy-settings.ts` | DSH 设置服务版本兼容垫片 |
| `src/host-heartbeat.ts`、`src/visibility-store.ts`、`src/probe-store.ts` | DSH 宿主生命周期与偏好存储 |
| `src/loopback.ts` | 本地在 `gateway.js` 内实现了等价逻辑 |

---

## 3. 本地 patch（改了上游语义的地方）

> **同步时最危险的一节。** 上游若在这几处又改了，你要判断的是
> "上游的新逻辑对不对"，而不是"把它抄回来"。

### 3.1 本轮同步的三项（已对齐 v0.7.1，无需重复）

上游提交 `6059b71`（issue 54，首发于 **v0.6.3**）与 `ad0306f`（PR #58，
首发于 **v0.6.4**），本地按以下方式落地：

| 上游改动 | 本地落点 | 备注 |
|---|---|---|
| `X-IDE-Type/Name/Version` 聊天与探测请求头 | `upstream.js` 的 `chatHeaders()` | 本地签名多了 `clientVersion` 参数；缺版本时不发明该头 |
| 裸 401 → `session_dead` | `upstream.js` 的 `classifyUpstreamError()` | 本地把它放在**积分标记之前**：裸 401 即使 body 提积分也判 `session_dead` |
| `extractDisplayErrorMessage()` 优先 `displayMsg.zh/en` | `upstream.js`（已导出）+ `gateway.js` 的 `upstreamErrorMessage()` | 见下 |

**本地对错误文案的偏离（重要）**：
上游 `shim.ts` 在 401/403 且非 `session_dead` 时**去掉** `(http N)`，
因为 DSH 宿主会按错误文本里的 `401`/`403` 字样把业务错误误判成 AUTH。
**本网关是按 `kind` 分类的，不解析文本**，所以本地**保留** `(http N)`
——它在这里是诊断信息，不是误判来源。不要照抄上游那行 `statusNote`。

### 3.2 `client-identity.js`：修掉一个上游也没有的解包 bug

上游 `resolveAppVersion()` 返回的对象字段名是 `version`，而 `chatUserAgent()`
读的是 `identity.clientVersion`——本地移植时**继承了同一个错位**，后果是
**已安装 App 的真实版本号一直被丢弃**，永远退回编译常量 `5.5.6`。

- 症状：UA 恒为 `WorkBuddy/5.5.6 …`，即使本机装的是 5.6.2；装 5.6+ 加密凭据
  的机器上，UA 形态与 App 不符。
- 本地修法：`resolveAppVersion()` 的三个返回点统一改成 `clientVersion`，
  与 `fallbackChatIdentity()` 对齐。
- 另外 `chatIdentity()` 包了一层：**resolver 抛错或版本非法都退回 fallback**，
  绝不让一条消息因为读不到安装树而失败。

> 上游 v0.7.1 的 `app-version.ts` / `client-identity.ts` 里没有这个问题
> （上游用 `AppVersionInfo` 显式接口）。这是本地移植引入的差异，同步时
> **不要**被上游的字段名"改回去"。

### 3.3 `desktop-credential-protection.js`：二进制定位被本地简化

解密算法（`buildAuthenticatedContextAad`、AES-256-GCM、protectorKey 派生）
**逐字节忠实**，这部分上游改了就得跟。但**二进制定位是本地简化版**：

| 能力 | 上游 v0.7.1 | 本地现状 |
|---|---|---|
| 每个产品自己的环境变量 | `WORKBUDDY_ELECTRON_BIN` + `WORKBUDDY_AI_ELECTRON_BIN` | **只有** `WORKBUDDY_ELECTRON_BIN` |
| Windows 候选验证 | 校验 exe basename（`workbuddy.exe`/`workbuddyai.exe`）+ 同目录 `version` 文件 + `resources/app.asar` | **只读注册表 `UninstallString`，不做 basename/布局校验** |
| 注册表 DisplayName 匹配 | 按产品各自的严格正则 | 宽松：`/DisplayName\s+REG_SZ\s+WorkBuddy\b/` |
| macOS 默认路径 | 按产品各自的 bundle id | 只有 CN 的固定路径 |

**已知风险（重要）**：本地的 `WorkBuddy\b` 正则**也会匹配到国际版**的
`WorkBuddy AI 5.6.2`（`y` 与空格之间是词边界）。也就是说在同时装了国内版和
国际版的 Windows 机器上，国际版变体可能解析到**国内版**的二进制去解锁。
本机只装国内版，所以没暴露；**若将来要支持国际版，这里必须先修**。
这正是上游 issue #59/#60 解决的问题 → 见第 6 节。

### 3.4 `upstream.js`：本地加的东西

- `collectChatCompletion()`、`iterateSseData()`、`normalizeCredits()`
  —— SSE 重组与规范化。上游把重组放在 `adapter.ts`（pi-ai 库内部），
  本地因为要服务多种客户端协议，自己实现了一份。
- `probeEffort()` + `errorCodeOf()` —— effort 探测**原语**（单次请求）。
  上游 `probe.ts` 在其之上有完整的 `probeModel()` 扫描状态机，
  本地**没有移植**——没有 DSH 设置页需要它。见第 6 节。
- `EFFORT_VALUES` —— 线上合法的 effort 拼写表。
- `originReferer()` —— 本地保留的小函数；注意它当前的两个分支返回同一个值
  （`variant.billingBase`），是移植时留下的死分支，**行为正确但看着可疑**。

### 3.5 `gateway.js`：`sanitizeChunk()` 是本地实现，但**逻辑必须保留**

上游在 `shim.ts` 里做同样的清理。上游每段 SSE 都带 `function_call: null`
或全空对象，严格客户端会据此臆造并行工具调用。**这是实测踩出来的，不是洁癖**，
重构网关时不要顺手删。

---

## 4. 本地独有 feature 清单

这些在同步时**不需要任何操作**，列在这里是为了让你放心跳过它们：

| Feature | 入口 | 说明 |
|---|---|---|
| 四种协议一个网关 | `gateway.js` + `anthropic.js` + `responses.js` | Chat / Anthropic Messages / OpenAI Responses |
| 三层安全边界 | `gateway.js` `handle()` | 只绑回环 + Host 必须回环 + Bearer 密钥 |
| 多账号保险库 | `accounts.js` | 每请求重读，`accounts use` 不用重启 |
| 跨机搬运 | `transfer.js` | scrypt + AES-256-GCM；导入时真刷一次验证 |
| 限流自动换模型 | `ratelimit.js` | 优先换 `x0.00` 免费模型 + 冷却表 |
| 每日签到 | `checkin.js` | 空 body，不发 `X-Device-Token`（实测不带也能通） |
| 画图 | `miora.js` + `media-tools.js` | `/v1/images/generations`、`/v1/images/edits` |
| agent 工具循环 | `agent.js` | 本地工具 vs 客户端工具的分派 |
| 用量账本 | `usage.js` | 记上游真实 `credit` 扣费 |
| CC Switch 集成 | `ccswitch.js` | |
| 完整 CLI | `bin.js` | `serve/status/models/doctor/logout/config/accounts/checkin/ccswitch/usage` |

---

## 5. 下次同步上游的操作步骤

```
1. 读上游 CHANGELOG / commit log，确认上次同步版本 → 本次目标版本
   （基线记在第 1 节和 MAINTENANCE.md「同步历史」）

2. 逐文件 diff，只看第 2.1 节那 5 个上游核心文件：
   src/auth.*  src/desktop-credential-protection.*  src/client-identity.*
   src/upstream.*  src/variants.*

3. 对每个改动判断落在哪一类：
   ├─ 上游核心语义变了 → 移植，并保持本地的输入输出契约
   ├─ 命中第 3 节已登记的本地 patch → 判断上游新逻辑是否更好，再决定
   └─ 第 2.3 节（DSH 宿主专属）→ 跳过

4. 落代码（保持依赖方向单向）

5. 更新内置花名册（如需）：models.cn.json / models.ai.json
  
6. 跑离线验证（零成本、可重复）：
   node scripts/test-agent.mjs     # 117 项协议测试（假上游）
   node scripts/test-miora.mjs     # 27 项画图测试
   node scripts/test-usage.mjs     # 39 项用量测试
   node src/bin.js doctor          # 不花钱
   node src/bin.js models          # 不花钱

7. 需要完整回归时再跑（会真实消耗少量积分）：
   node scripts/verify.mjs         # 端到端，必须全绿

8. 更新本文档第 1/3/6 节 + MAINTENANCE.md「同步历史」
```

### 同步时不要做的事

- ❌ 用上游目录覆盖 `workbuddy_api/`
- ❌ 因为"上游没有"就删掉第 2.2 节的本地模块
- ❌ 把 `docs/` 换成上游的（上游 v0.6.2 起已从公开仓库移除 `docs/`）
- ❌ 引入第三方依赖（`package.json` 的 `dependencies` 必须保持为空）
- ❌ 照抄上游 `shim.ts` 去掉 `(http N)` 的改动（见 3.1）

---

## 6. 尚未跟上的上游改动（技术债）

按优先级排列。这些**不影响本机当前使用**（本机只装国内版），但记在这里
以免下次重新调研：

### 6.1 每产品 Electron 定位（上游 issue #59/#60，提交 `de2c795` 等）—— **优先级最高**

- 上游：按产品区分 bundle id / 注册表名 / exe basename / 环境变量，且 Windows
  候选要通过 `version` 文件 + `resources/app.asar` 布局校验；`null` 与
  "看不出来" 严格区分。
- 本地：见 3.3，只有 CN 一套，且正则可能跨产品误匹配。
- **触发条件**：要支持国际版（`--provider workbuddy-ai`）的加密凭据时，必须先修。

### 6.2 Windows 注册表发现的 asar 兼容（上游 #66，提交 `8a26464`）

上游在 Electron 宿主里发现 `fs.stat` 会把 `app.asar` 当目录，导致所有注册表
候选被排除；改用 `readdirSync` 列表判断。本地目前不做布局校验，所以**没有这个
bug**——但如果跟着 6.1 一起做校验，**必须用 `readdirSync`，不要用 `stat`**。

### 6.3 effort 探测的完整状态机（上游 `probe.ts`）

上游有 `probeModel()` 扫描 + 按区域区分拒绝码
（`invalid_reasoning_effort` / `model_param_invalid`，提交 `0347ebb`）。
本地只有单次请求原语 `probeEffort()`，**没有**扫描逻辑，也没有按区域收集拒绝码。
- **触发条件**：想在本地自动发现"某个模型支持哪些思考档位"时再做。
- 花名册里的 `reasoning.supportedEfforts` 目前来自上游目录，够用。

---

## 相关文档

- [`ARCHITECTURE.md`](./ARCHITECTURE.md) — 本地模块地图与依赖方向
- [`UPSTREAM.md`](./UPSTREAM.md) — 上游协议契约与变更排查
- [`MAINTENANCE.md`](./MAINTENANCE.md) — 维护手册与同步历史
