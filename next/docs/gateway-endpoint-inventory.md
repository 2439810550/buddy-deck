# 网关端点完整性盘点（vs 旧版 workbuddy_api）

> 审计日期：2026-10-07 · 执行：captain（原属主 gateway 会话连续打掉 7 次，非其能力问题）
> 性质：**只读审计**，未改动任何 `src/` 代码

## 一、当前已注册的端点族

`IEndpointFamily.AddAll()`（`src/BuddyDeck.Gateway/IEndpointFamily.cs:126`）是**唯一的注册顺序真源**：

```csharp
services.AddSingleton<IEndpointFamily>(sp => sp.GetRequiredService<AdminEndpoints>());  // :134 转发
services.AddSingleton<IEndpointFamily>(sp => new UsageFamily(...));                     // :138 适配器
Register<AnthropicEndpoints>(services);   // :141
Register<MediaEndpoints>(services);       // :142
Register<SearchEndpoints>(services);      // :143
Register<ResponsesEndpoints>(services);   // :144
```

另有 `GatewayApp` 自身处理的 `/v1/chat/completions` 与 `/v1/models`。

## 二、逐族路由明细

| 端点族 | 路由 | 状态 |
|---|---|---|
| **GatewayApp** | `GET /v1/models` | ✅ 已实现（t53 修复后能从上游刷到 32 个） |
| | `POST /v1/chat/completions` | ✅ 已实现（含两层降级链） |
| | `GET /healthz` | ✅ 已实现（免鉴权） |
| | `GET /metrics` | ✅ 已实现（11 字段） |
| **AdminEndpoints** | `GET /admin/accounts` | ✅ 已实现 |
| | `POST /admin/accounts/import` | ✅ 已实现 |
| | `GET /admin/pools` | ✅ 已实现（t58 改为真多池） |
| | `POST /admin/pools` | ✅ 已实现（t58，原为 501） |
| | `POST /admin/pools/{id}/state` | ✅ 已实现 |
| | `POST /admin/pools/{id}/remove` | ✅ 已实现（t58，默认池 400） |
| | `GET /admin/logs` | ✅ 已实现 |
| | `GET/POST /admin/settings` | ✅ 已实现（内存态，M14 的 SettingsStore 未接线） |
| | `/admin/keys` | ❌ **缺失** ← t75 的密钥页在等 |
| | `/admin/credits` | ❌ **缺失** ← t70 在做，ui 的账号卡在等 |
| **AnthropicEndpoints** | `POST /v1/messages` | ✅ 已实现并真网关验过（t26） |
| | `POST /v1/messages/count_tokens` | ❌ **缺失**（旧版有，见 §三） |
| **MediaEndpoints** | `POST /v1/images/generations` | ✅ 已实现（t20） |
| | `POST /v1/images/edits` | ✅ 已实现（t20） |
| | `GET /v1/images/file/{name}` | ✅ 已实现（t39 加 HMAC 签名放行） |
| | `POST /v1/video/generations` | ✅ 已实现（只提交，**轮询未接** ← t73 在做） |
| | `GET /v1/video/status/{id}` | ✅ 已实现（返回上游原始状态） |
| | `GET /v1/videos` | ✅ 已实现（列表） |
| **SearchEndpoints** | `POST /v1/search` | ✅ 已实现（返回 Markdown 文本） |
| | `POST /v1/webfetch` | ✅ 已实现（返回 Markdown 文本） |
| **ResponsesEndpoints** | `POST /v1/responses` | ✅ 已实现并真网关验过（t33，58 条测试） |
| **UsageEndpoints** | `GET /usage/summary` 等 | ✅ 已实现（t29 接线，前缀是 **`/usage/`** 不是 `/v1/` 或 `/admin/`） |

> ⚠ **审计陷阱记录**：我第一次 grep `"/v1/..."` 和 `"/admin/..."` 时，
> `UsageEndpoints` 报「0 条路由」—— 因为它用的是 **`/usage/`** 前缀。
> **只按已知前缀扫会漏掉整族。** 这条值得记：盘点要用「族名」而不是「前缀」当入口。

## 三、与旧版对照：我们缺什么

旧版 `workbuddy_api/src/*.js` 的全部路由（grep 所得）：

```
/v1/chat/completions        ✅ 有
/v1/images/edits            ✅ 有
/v1/images/file/            ✅ 有
/v1/images/generations      ✅ 有
/v1/messages                ✅ 有
/v1/messages/count_tokens   ❌ 缺
/v1/models                  ✅ 有
/v1/responses               ✅ 有
/v1/search                  ✅ 有
/v1/video/generations       ✅ 有
/v1/video/status/           ✅ 有
/v1/videos                  ✅ 有
/v1/webfetch                ✅ 有
/v2/api-keys                ⚠️ 内部端点（Miora tempKey 链，新版在 MioraClient 内部调用，无需暴露）
/v2/billing/meter/*         ⚠️ 上游端点（不是我们提供的，是我们调用的）
/v2/chat/completions        ⚠️ 上游端点
/v2/plugin/auth/token/refresh ⚠️ 上游端点
/v3/config                  ⚠️ 上游端点（目录刷新用）
```

**结论：对外路由只缺 `POST /v1/messages/count_tokens` 一条。**

## 四、缺失项优先级

| 缺失 | 用户影响 | 归属 |
|---|---|---|
| `/admin/credits` | **高** —— 账号卡看不到积分，而积分到期是真金白银损失 | t70 进行中 |
| `/admin/keys` | 中 —— 多客户端分权需要 | t75 依赖它 |
| `/v1/messages/count_tokens` | 中 —— Claude Code 会在提交前估 token，缺了它可能降级 | 未派 |
| 视频轮询 | 中 —— 生视频只能拿到 `pending`，拿不回成品 | t73 进行中 |
| `/admin/settings` 持久化 | 中 —— 现在重启即丢 | M14（SettingsStore 已写，未接线） |
| checkin 路由 | 低 —— 签到是面板行为，不是客户端 API | 未派（设计上可能不需要） |

## 五、本次审计的方法学记录

1. **入口用族名，不用前缀** —— 否则会漏掉用别的前缀的整族（见 §二 陷阱记录）
2. **区分「我们提供的路由」与「我们调用的上游路由」** —— 旧版文件名里两者混在一起
3. **状态分三档**：已实现 / 桩实现 / 未实现。本次没有"桩实现"档
   （所有列出的都已实现），但 t73 的「视频只提交不轮询」属于**半链路**，单列在 §四
