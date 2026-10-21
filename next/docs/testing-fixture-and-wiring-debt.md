# 测试夹具重叠与装配分叉的处置结论

> 写于 t7。**本文只出结论，不含改动** —— 涉及的目录归属 t2（observability）与 t5（harness），
> 由队长派单后由对应负责人执行。
>
> 背景：同一件事被三个人从三个角度各报了一次（新测试项目未入解决方案 → 裸 `dotnet test` 静默漏测）。
> 根因已由 t7 在 `BuddyDeck.slnx` 修掉；本文处理剩下两项**架构债**。

---

## 一、测试项目入解决方案（已完成）

`BuddyDeck.slnx` 的 `/tests/` 分组现在含 5 个项目：

```
tests/BuddyDeck.Core.Tests            143  ← t2 新增 33 个会话粘性用例，现在真的会被执行
tests/BuddyDeck.Gateway.Tests          45
tests/BuddyDeck.TestSupport              0  ← 纯库，无测试
tests/BuddyDeck.TestSupport.Tests      53
tests/BuddyDeck.DocSamples.Tests       15
                                     ────
                                      256
```

**待补**（施工中，现在加进去会让裸 `dotnet build` 变红）：
- `src/BuddyDeck.App/BuddyDeck.App.csproj`（ui）
- `tests/BuddyDeck.App.Presentation.Tests/BuddyDeck.App.Presentation.Tests.csproj`（design）
- `tests/BuddyDeck.E2E.Tests/`（尚未创建，e2e 的任务是 t4）

**教训**：.NET 的 slnx 是**显式清单**，新项目不会自动进来。
「加了测试却没进解决方案」= 绿着绿着其实没跑，是**静默**的 —— 没有任何警告。
所以新增测试项目时，**必须同时改 slnx**，这条应写进项目的贡献约定。

---

## 二、两套夹具的重叠与收敛建议（不动手，等派单）

### 现状：两套并存

| | `GatewayFixture`（t1，我） | `GatewayHost`（t5，harness） |
|---|---|---|
| 位置 | `tests/BuddyDeck.Gateway.Tests/` | `tests/BuddyDeck.TestSupport/` |
| 可见性 | 项目内（同项目测试用） | 共享库（多项目引用） |
| 装配 | `CreateSlimBuilder` + 手工 DI | `CreateSlimBuilder` + 手工 DI |
| 端口 | `UseUrls("http://127.0.0.1:0")` | 同左 |
| 客户端 | `HttpClient`（BaseAddress 已配） | 同左 |
| 假上游 | 自建 `FakeUpstream`（Core.Tests 里的共享文件） | 自建 `FakeUpstreamHandler` + `UpstreamScript` |
| 日志抓取 | 无 | `CapturedLog` / `DumpLog()` |
| 探活等待 | 无（调用方自己重试） | `WaitForHealthAsync` / `StartReadyAsync` |
| 保险库 | 内联 `StaticCredentialSource` | `TestVault` + `FakeCredentialSource`（落真文件、验格式往返） |
| 响应脚本 | 一次性 handler 闭包 | `UpstreamScript`（按次序/按路由，支持换号重试） |
| 观察者 | `RecordingObserver` | 无 |
| 指纹目录 | `MakeFingerprintDirectory()` | 无（直接用 `AppContext.BaseDirectory`） |

### 重叠在哪

**核心重叠是「起一个真端口的网关 + 配好假上游 + 给一个 HttpClient」** ——
这部分两边各写了一遍，行为等价。测试写法因此分叉：

```csharp
// GatewayFixture
await using var gw = await GatewayFixture.StartAsync(h => FakeUpstream.Sse(...));
var r = await gw.PostChatAsync(json);          // 自带 JSON/鉴权/Content-Type 处理

// GatewayHost
await using var host = await GatewayHost.StartAsync(handler, entries);
var r = await host.Client.PostAsync("/v1/chat/completions", content);  // 调用方自己拼
```

### 建议：**保留 `TestSupport.GatewayHost`，把 `GatewayFixture` 瘦身成它之上的薄适配层**

理由（按重要性）：

1. **可见性**：`GatewayHost` 在共享库里，跨项目可用；`GatewayFixture` 锁在 `Gateway.Tests` 内部。
   将来 e2e（t4）要用只能在 TestSupport 上再抄一份 —— 那就是**第三套**。现在收敛，正好卡在只有两套的时候。
2. **能力更全**：`GatewayHost` 有日志抓取、探活等待、`UpstreamScript`（能演换号重试）、真保险库往返。
   `GatewayFixture` 有的（`RecordingObserver`、`MakeFingerprintDirectory`、`PostChatAsync` 便捷方法）都是**薄封装**，
   搬到 `GatewayHost` 上成本很低；反过来把日志/探活/脚本能力搬进 `GatewayFixture` 成本高得多。
3. **`UpstreamScript` 是刚需**：t2 的会话粘性、t4 的端到端都要「第一次 429、第二次成功」这种脚本化序列，
   一次性 handler 闭包做起来很别扭。
4. **测试数不降**：`GatewayFixture` 上的 45 个用例全部是在验**网关行为**，不是验夹具。
   换底层夹具只需要改 `GatewayFixture` 的 `StartAsync` 内部实现 + 保留 `PostChatAsync` 等便捷方法签名 —— 45 个用例**一行不用动**。

### 下一个人来收时应该怎么做（照着做，别重新评估）

1. `tests/BuddyDeck.Gateway.Tests/` 加 `<ProjectReference Include="../BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj" />`
2. 把 `GatewayFixture.StartAsync` 改成内部委托给 `GatewayHost.StartAsync`，**保留现有的公开签名**：
   - `Port` / `Client` / `Pool` / `Catalog` / `ApiKey` → 从 `GatewayHost` 转发
   - `Fake`（`FakeUpstream`）→ 换成 `GatewayHost` 的 `FakeUpstreamHandler` 实例（**这个签名会变**，需要顺带改用例里的断言写法）
   - `PostChatAsync` / `PostChatWithHostAsync` / `MakeFingerprintDirectory` / `RecordingObserver` → **原样保留**（它们是纯便捷层，不重复）
3. **删除 `tests/BuddyDeck.Core.Tests/FakeUpstream.cs`** —— ⚠ **需要先报队长**：该文件在 t2（observability）的范围内，且
   `BuddyDeck.Core.Tests` 引用它（`UpstreamClientTests` 等）。删除要同时改 Core.Tests 的引用。
   它的内容与 `TestSupport.FakeUpstreamHandler` + `UpstreamResponses` **确实重复**（见下表）。
4. 跑 `dotnet test`（不带路径）确认 256 个用例仍全绿；**重点看 Core.Tests 的 143 个**（它们用的是被删的那个夹具）。

### `tests/BuddyDeck.Core.Tests/FakeUpstream.cs` 的去向说明

**已确认重复**：

| FakeUpstream.cs | TestSupport 里的对应物 |
|---|---|
| `FakeUpstream : HttpMessageHandler` | `FakeUpstreamHandler : HttpMessageHandler` |
| `CapturedRequest(Method, Url, Headers, Body)` | `CapturedRequest(Method, Url, Path, Headers, Body, Sequence, …)` —— **更强**（多 Path/Sequence/ContentType，且带 `Json()`/`Header()` 辅助） |
| `FakeUpstream.Ok/Sse/Status/Envelope` | `UpstreamResponses`（能力覆盖且更全） |

两者命名空间不同（`BuddyDeck.Core.Tests` vs `BuddyDeck.TestSupport`），**目前不冲突、不报错**。
但同义两套是认知负担，且 `FakeUpstream` 是 t1 为「让 Gateway 测试能复用」临时提出来的 ——
既然 harness 后来建了更好的共享库，**`FakeUpstream.cs` 应删、改引 `BuddyDeck.TestSupport`**。

**结论**：删除需报队长（t2 范围）；或由 t2 顺手做掉。**不建议保留两套。**

---

## 三、装配分叉：`Program.cs` 与 `GatewayHost` 的同步义务

### 问题

`Program.cs`（生产装配）与 `GatewayHost`（测试装配）**各写了一份「网关怎么组装」**：

```
Program.cs      : CreateSlimBuilder → Serilog → UseUrls → DI(12 项) → GatewayApp.Map
GatewayHost     : CreateSlimBuilder → 静默日志 → UseUrls(0) → DI(12 项) → GatewayApp.Map
```

两份 DI 注册**必须保持一致**（`GatewayOptions` / `UpstreamVariant` / `ModelCatalog` / `BuildInfo` /
`TimeProvider` / `AccountPool` / `ICredentialSource` / `UpstreamClient` / `IChatObserver` /
`ChatObservers` / `GatewayApp`）。

**风险**：`GatewayApp` 的构造函数是 DI 注入 ——
如果 `Program.cs` 里新增一个依赖（比如 t2 又要注入一个服务），而 `GatewayHost` 没同步，
**编译期不报错**（两边都是独立文件），只在测试运行时以「无法解析服务」失败，
或者更糟：如果新依赖有默认值，测试**静默地测着与生产不同的装配**。

### 建议（三选一，按推荐度排序）

**① 抽公共扩展方法（推荐）**

在 `src/BuddyDeck.Gateway/` 里加一个 `GatewayServiceCollectionExtensions.AddBuddyDeckGateway(this IServiceCollection, GatewayOptions)`，
把**除 Serilog 和 UseUrls 之外**的 DI 注册全搬进去。

- `Program.cs`：`services.AddBuddyDeckGateway(options);` + Serilog + 真实端口
- `GatewayHost`：`services.AddBuddyDeckGateway(options);` + 假上游 HttpClient + 端口 0

好处：**装配只有一份**，新增依赖自动两边生效；测试与生产的差异被压缩到「日志 + HttpClient + 端口」这三件**本来就该不同**的事上。
代价：`GatewayHost` 需要引 `BuddyDeck.Gateway`（它已经引了，因为它 `new GatewayApp()`），无额外成本。
⚠ 注意：假上游需要覆盖 `UpstreamClient` 的 `HttpClient`，所以扩展方法要允许调用方**之后**再覆盖注册（`AddHttpClient` 的注册顺序敏感，需实测）。

**② 只加双向注释（最低成本）**

在 `Program.cs` 的 DI 区块和 `GatewayHost` 的 DI 区块各加一条指向对方的注释：

```csharp
// ⚠ 这份 DI 与 tests/BuddyDeck.TestSupport/GatewayHost.cs 的装配**必须同步**。
//    改动这里之后，务必同步改那边，并跑一次 dotnet test 确认没漏注册。
```

成本近乎零，但**靠人记得**，正是「静默失配」的温床。适合先止血、后重构。

**③ 让测试复用 `Program.cs` 的装配**

理论上最彻底（测试跑的就是生产装配），但需要把 `Program.cs` 的顶层语句重构成可被测试引用的
`WebApplicationBuilder` 工厂，改动大、且 `Program.cs` 的顶层语句对测试程序集可见性有额外要求。
**当前阶段不划算**，等网关装配稳定后再考虑。

### 建议执行顺序

先做 **②**（立刻止血，成本≈0）→ 等 `Program.cs` 的 DI 形状稳定（t2 的 observability 服务进来之后）→
再做 **①**（真正消除分叉）。做 ① 的时候顺手把 ② 的注释改成「见 AddBuddyDeckGateway」。

---

## 四、`/healthz` 的 `models` 计数何时会变

### 现状

内置清单**硬编码 16 个** id（`src/BuddyDeck.Gateway/ModelCatalog.cs` 的 `BuiltIn` 字段，
移植自旧版 `models.cn.json`）。旧版 8787 实测已报 `models:17`（真实上游目录已增长）。
**这不是 bug** —— t1 的契约把 16 个 id 钉死了。

### 刷新时会变成 17 吗？会，但有几个前提

覆盖逻辑位置（**精确到方法**）：

| 步骤 | 位置 |
|---|---|
| 触发点 | `GatewayApp.ModelsAsync(...)` —— **每次 `GET /v1/models`** 都会调 |
| 拉取 + 覆盖 | `GatewayApp.TryRefreshCatalogAsync(CancellationToken)`，第 255 行：`if (models.Count > 0) _catalog.Replace(models);` |
| 解析 | `GatewayApp.ExtractModels(JsonNode)` —— 认 `models[]` / `data.models[]` / `data[]` / `list[]` 四种形状 |
| 落库 | `ModelCatalog.Replace(IEnumerable<CatalogModel>)`（第 69 行）—— **空列表会被忽略**，不会把清单刷没 |
| 读出口 | `/healthz` 的 `["models"] = _catalog.Count`（`GatewayApp.HealthzAsync`）；`/v1/models` 的 `data` 数组 |

**生效条件（三条全满足才变成 17）**：
1. 保险库里**至少有一个账号**（`TryRefreshCatalogAsync` 第一件事就是 `if (entries.Count == 0) return;` —— 没账号直接返回，不来刷）
2. `UpstreamClient.FetchCatalogAsync` 成功（打到 `variant.BillingBase + /v3/config`）
3. 返回体里能解析出**非空**的模型 id 列表

**失败时**：静默（`LogLevel.Debug` 记一条），**保留内置 16 个**，`/v1/models` 照常可用 ——
这是有意的：该端点是客户端启动第一件调的事，不能因为上游抽风就没模型可选。

**因此**：
- 无账号 / 断网 / 上游改结构 → `/healthz` 停在 **16**
- 有账号且上游正常 → `/healthz` 变 **17**（或上游实际数量）

### ⚠ 顺带发现一个真问题（在 t2 范围内，我没动手）

`TryRefreshCatalogAsync` 的文档注释写着：

```
/// 只刷一次：刷完就重挂时间戳，不给上游制造流量。
```

**但实现里没有任何时间戳或「刷过了」标志** —— 每次 `/v1/models` 都会真打一次上游 `FetchCatalogAsync`。
注释与代码不符（注释描述的是一个没实现的行为）。

后果：客户端若频繁轮询 `/v1/models`，会给上游和账号池带来**无谓的请求量**（在 8787 只有有限配额的前提下是真实成本）。

**修法**（t2 或派单后执行，二选一）：
- 加 `_lastCatalogRefreshMs` + 一个 TTL（比如 5 分钟），到点才刷；
- 或者去掉那段注释，明说「每次请求都刷」。

**推荐前者**（保留注释描述的意图，因为它是合理的），并补一条测试：「TTL 内的第二次 `/v1/models` 不再打上游」。
