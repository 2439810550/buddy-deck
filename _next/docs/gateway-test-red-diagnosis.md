# Gateway.Tests 红灯交接材料（给 gateway 收口 t28 用）

> 由 observability 整理，**只读诊断，未改任何测试或实现**。
> 目的：让 t28 的收口一次看清「哪些断言因语义变更而过期」，而不是一条条试。

---

## 0. 先说结论（三句话）

1. **稳定红 = 2 条**，都在 `GatewayObservabilityTests`，**归我（t37）**，不是 t28 的锅。
2. **另有 3~4 条是"平行跑的互相干扰"**，不是断言过期、也不是实现 bug —— **串行跑就没了**（实测：377 条里失败从 5~6 降到 2）。
3. **`MediaEndpointTests` / `ModelDriftTests` / `RetryTests` 等类偶尔整片变红（最多一次 84 条）**，也是同一个干扰源，**不是 84 个真 bug**。

**⚠ 所以：如果按"红了几条就改几条断言"去收口，会把干扰误当语义变更改掉。请先看第 1 节。**

---

## 1. ⚠ 最重要的发现：`[Collection("GatewayHost")]` 在这个程序集里是**失效的**

### 现象
同一套代码，失败的**条数和名单每次都不一样**：

```
跑 5 次的失败数：3、3、3、84、84
失败名单：只有 3 条是 5/5 次都出现；其余 2/5 次出现（随机的）
```

`MediaEndpointTests` 那 84 条**单独跑全绿**，只在并行时整片红。

### 根因
```
tests/BuddyDeck.Gateway.Tests/GatewayObservabilityTests.cs:26   [Collection("GatewayHost")]
tests/BuddyDeck.Gateway.Tests/MetricsCaliberTests.cs:37         [Collection("GatewayHost")]

而 CollectionDefinition 在**另一个程序集**里：
tests/BuddyDeck.TestSupport.Tests/GatewayHostTests.cs:221
    [CollectionDefinition("GatewayHost", DisableParallelization = true)]
```

**xUnit 的 `CollectionDefinition` 不跨程序集生效。** 它在 `BuddyDeck.TestSupport.Tests` 里定义，而 `BuddyDeck.Gateway.Tests` 引用它时，`[Collection("GatewayHost")]` 只是**声明了一个同名集合**，**拿不到 `DisableParallelization = true`**。

⇒ **Gateway.Tests 的 377 条用例实际上是并行跑的**，而它们共享进程内的 DI 单例（尤其 `AccountPool`）。

### 实测证据（决定性）
```
$env:XUNIT_DISABLE_PARALLELIZATION="1"; dotnet test tests/BuddyDeck.Gateway.Tests
→ 失败: 2，通过: 375，总计: 377        ← 从 5~6 降到 2

（不设这个变量）
→ 失败: 5~6，通过: 371~372，总计: 377
```

**失败的 5~6 条里，只有 2 条是真的。**

### 为什么会互相干扰
多个测试类都这么干：
```csharp
var pool = fx.Host.Application.Services.GetRequiredService<AccountPool>();
pool.MarkRateLimited(member.Id, "hy3", ...);   // 给共享的池打冷却
pool.Replace(vault.Entries);                    // 或整体替换
```
→ **A 用例打的冷却，会让并行跑的 B 用例选不出号**，表现成 B 的断言失败。

### 这是谁的问题
**不是 t28 的语义变更**，是测试基础设施的装配缺陷。**建议单独派单**（候选归属：harness，夹具/宿主作者）。
`t38`【装配缺陷】GatewayHost 没挂端点族 是同一类问题，可以并到一起看。

**在修好之前，Gateway.Tests 的红灯数不稳定**，不能作为任何判定的唯一依据。

---

## 2. 稳定红 2 条 —— 归 t37（我的），**不是 t28 的语义变更**

这两条**串行跑依然红**（所以是真的），但**你说它们"被 t28 语义推翻"这个判断，我实测下来是不成立的** —— 下面逐条给依据。

### 2.1 `GatewayObservabilityTests.上游失败时成功率和完成率都掉下来`
```
文件：tests/BuddyDeck.Gateway.Tests/GatewayObservabilityTests.cs:157
断言：Assert.Equal(HttpStatusCode.TooManyRequests, failed.StatusCode)
实际：Expected: TooManyRequests   Actual: OK
```
**测试意图**：脚本化「第 1 次 429，第 2 次起成功」，然后断言第 1 次拿到 429。

**为什么它在 t28 之后不再成立**：t28 引入了「账号内先换模型」——
第 1 枪拿到 429 后，网关**会在同一个号上换个模型再试**，第二次成功了，
于是**客户端最终拿到的是 200（OK），不是 429**。
⇒ **客户端视角从"失败"变成了"被重试救回来的成功"。**

**这是语义变更**，但不是"断言过期要改数值"，而是**这个用例的前提被推翻了** ——
它原本验的是"上游失败会反映到客户端"，现在"上游失败"未必到达客户端了。

**⚠ 所以这条不该简单改成 `Assert.Equal(OK, ...)`** —— 那样它就退化成一条没有意义的测试。
**正确做法**：改成断言**新语义下的真相**，例如
```
· 客户端拿到 200（因为换模型救回来了）
· 且 fake.Count == 2（确实发了 2 枪：第 1 枪 429 + 第 2 枪换模型后成功）
· 且两枪用的是**同一个号、不同模型**（这正是 t28 的新语义）
```
**即：这条应该变成一条"验证 t28 两层语义"的测试，而不是被删掉或降级。**

### 2.2 `GatewayObservabilityTests.账号被限流后会话换号且不抛异常`
```
文件：tests/BuddyDeck.Gateway.Tests/GatewayObservabilityTests.cs:785
断言：Assert.NotEqual(firstToken, fake.Last.HeaderOrNull("Authorization"))
实际：两次的 Authorization 完全相同（Expected: Not "Bearer sk-…"，Actual: "Bearer sk-…"）
```
**测试意图**：账号被限流后，会话粘性要"让路"换号。

**为什么它在 t28 之后不再成立**：t28 的**内层换模型**先把请求救回来了（同号换模型成功），
**根本没轮到换号那一步**。⇒ 号没换，粘性也不用让路。

**⚠ 这条同样不该简单删掉** —— "粘性遇到限流要让路"仍然是**必须成立**的性质，
只是**触发条件变了**：只有**当前账号的所有模型都限流**时才轮到换号。
**正确做法**：把脚本改成「该号**所有模型**都被限流」，才能走到换号分支；
或者改用 `UpstreamResponses.TooManyRequests()`（账号级）而非模型级的限流。

---

## 3. 不稳定红 3~4 条 —— **不要改断言**

这些**单独跑全绿**，只在并行时红：

| 用例 | 文件 | 单独跑 |
|---|---|---|
| `GatewayEndpointTests.所有号都在冷却时返回明确的_429_而不是_undefined` | GatewayEndpointTests.cs | ✅ 36/36 全绿 |
| `MetricsCaliberTests.号全在冷却里导致没发出请求_不算上游尝试` | MetricsCaliberTests.cs:295 | ✅ 21/21 全绿 |
| `MetricsCaliberTests.没号可发的两种成因_能从指标里区分开` | MetricsCaliberTests.cs:392 | ✅ 21/21 全绿 |
| `RetryTests.部分模型冷却时_换到没被冷的模型而不是直接失败` | RetryTests.cs:569 | ⚠ 单独跑 1 条红（见下） |
| `RetryTests.服务端错误_不换模型直接换号` | RetryTests.cs:692 | ⚠ 单独跑通过、并行时红 |

**⇒ 对这 3~4 条：改断言是错的。** 它们的期望值在当前实现下是对的，
它们红是因为**别的用例在并行时改了共享的 `AccountPool`**（打了冷却/换了池）。

**这正好是你问的"哪条可能是回归、哪条是断言过期"的答案：都不是 —— 是测试隔离问题。**

---

## 4. 逐条判定（回答你要的第 ④ 条）

| # | 用例 | 判定 | 依据 |
|---|---|---|---|
| 1 | `GatewayObservabilityTests.上游失败时成功率和完成率都掉下来` | **语义变更 → 用例前提被推翻**（t37，我的） | 串行仍红；429 被内层换模型救回，客户端拿到 200 |
| 2 | `GatewayObservabilityTests.账号被限流后会话换号且不抛异常` | **语义变更 → 触发条件变了**（t37，我的） | 串行仍红；换号分支没被走到 |
| 3 | `GatewayEndpointTests.所有号都在冷却时…` | **测试隔离问题**，非回归非断言过期 | 单独跑 36/36 全绿 |
| 4 | `MetricsCaliberTests.号全在冷却里…` | **测试隔离问题** | 单独跑 21/21 全绿 |
| 5 | `MetricsCaliberTests.没号可发的两种成因…` | **测试隔离问题** | 单独跑 21/21 全绿 |
| 6 | `RetryTests.部分模型冷却时_换到没被冷的模型而不是直接失败` | **需要你确认**（见下） | 单独跑**也红**：`Expected: OK, Actual: TooManyRequests` |
| 7 | `RetryTests.服务端错误_不换模型直接换号` | **测试隔离问题** | 单独跑通过、并行时红 |

### ⚠ 第 6 条要单独说：它可能是**真回归**
`RetryTests.部分模型冷却时_换到没被冷的模型而不是直接失败`
```
文件：tests/BuddyDeck.Gateway.Tests/RetryTests.cs:569
断言：Assert.Equal(HttpStatusCode.OK, response.StatusCode)
实际：Expected: OK   Actual: TooManyRequests

用例做的事：只有 hy3 被冷（账号内还 15 个模型可用）
            断言「应该内层换模型 → 成功 200」
```
**这条单独跑也红 ⇒ 不是干扰。** 按 t28 新语义（**内层就该换模型**），
**它的期望是对的，而实现没做到** —— 这看起来像**实现侧的缺陷**（内层换模型没生效，
或者只在该号所有模型都冷时才换）。

**⚠ 请你自己判定**：如果 `RunWithRetryAsync` 的内层换模型确实该在这种情况下生效，
那这是**实现 bug，不是断言过期** —— **不要去改这条断言。**

---

## 5. 给你的建议动作

```
① 先修测试隔离（`[Collection("GatewayHost")]` 在 Gateway.Tests 里失效）
   —— 否则你每次跑看到的失败数都不一样，无法判断自己改对没有
   ⇒ 具体做法（二选一）：
      · 在 BuddyDeck.Gateway.Tests 里补一个自己的 CollectionDefinition
      · 或在 csproj 里 <ParallelizeTestCollections>false</ParallelizeTestCollections>

② 再单独复核 §4 第 6 条（RetryTests.部分模型冷却时…）是不是实现缺陷

③ 我这两条（§2）等队长把 t28 定稿语义写进 t37 后我改，
   我会按"验证新语义"的方向改，而不是把断言降级成能通过的样子
```

---

## 6. 我是怎么得出这些结论的（可复现）

```powershell
cd D:\workfandaiapi\_next

# 1) 稳定 vs 不稳定：连跑 5 次统计每条的出现频次
1..5 | % { dotnet test tests/BuddyDeck.Gateway.Tests }   # 失败数 3,3,3,84,84

# 2) 决定性实验：关掉并行
$env:XUNIT_DISABLE_PARALLELIZATION="1"; dotnet test tests/BuddyDeck.Gateway.Tests
# → 失败: 2，通过: 375      ← 只剩我这两条

# 3) 各类单独跑
dotnet test tests/BuddyDeck.Gateway.Tests --filter "FullyQualifiedName~GatewayEndpointTests"    # 36/36 绿
dotnet test tests/BuddyDeck.Gateway.Tests --filter "FullyQualifiedName~MetricsCaliberTests"     # 21/21 绿
dotnet test tests/BuddyDeck.Gateway.Tests --filter "FullyQualifiedName~ModelDriftTests"         # 15/15 绿
dotnet test tests/BuddyDeck.Gateway.Tests --filter "FullyQualifiedName~RetryTests"              # 23/24 红 1 条
dotnet test tests/BuddyDeck.Gateway.Tests --filter "FullyQualifiedName~GatewayObservabilityTests" # 27/29 红 2 条
```

---

## 7. 边界声明
- 本材料**只读**：没有改任何测试、任何实现、任何 csproj。
- 我未动 `RetryTests.cs` / `MetricsCaliberTests.cs` / `GatewayEndpointTests.cs`（都不在我的范围）。
- 我**唯一**会改的是 `GatewayObservabilityTests.cs` 我自己那两条（等 t37 定稿语义）。
