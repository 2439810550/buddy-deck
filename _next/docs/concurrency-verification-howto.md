# 并发与限流验证的操作说明

> 作者：ui。起因：为验证 `AccountPool` 在并发下是否把多个请求压到同一个号上，我在真网关上做了一次观测，
> 过程中推翻了队长的一条推断（"三个请求全挤同一个号"），也确认了自己的一条误判（"离线通过 = 在线也通过"）。
> 这份文档把**方法**留下来 —— 结论会过期，方法不会。
>
> 适用场景：任何要验证「并发下系统实际怎么分派」的问题 —— 账号池分流、限流、重试、会话粘性。

---

## 0. 三条最重要的原则（先读这个）

### ① 判据必须从**系统外部**取，不能问被测对象

这是这次最值钱的一条。

我最初想用 `AccountPool.SelectionCount` 当判据，但它是**历史累计**：

```csharp
picked.SelectionCount++;   // 每次 Pick 都加一，但从不减
```

它只能回答「这个号被选过多少次」，**回答不了「此刻有几个请求正压在这个号上」**。
用它判断并发重叠，等于**问被测对象"你重叠了吗"** —— 内部计数器是历史累计，看不出瞬时状态。

**正确的判据**：网关转发时，会把选中那个号的凭据放进 `Authorization` 头。
所以去**假上游侧**看每个请求带的是哪个 token —— 那是"网关实际用了哪个号"的硬证据。

> **换个说法：要验证 A 对 B 做了什么，就去 B 那边取证，不要在 A 内部找计数器。**

### ② 跑**多轮**，不要拿单次结果当结论

并发调度里，一次巧合和一条规律长得一模一样。

我每个场景跑 5 轮。这次的结果 5 轮完全一致（说明是确定性行为，不是巧合）；
但如果某轮不一样，那本身就是重要信息 —— **不跑的代价是把巧合写进结论**。

### ③ 让并发窗口足够大，否则测的是串行

如果假上游立刻返回，第一个请求可能在第二个 `Pick` 之前就结束了 —— 那根本没有重叠的机会，
测出来永远是"无重叠"，而这是**假的**。

我的做法：假上游里**占住请求约 300ms**（真实上游的聊天请求本来就要几百毫秒到几秒）。
用 `Stopwatch` + `Thread.SpinWait` 忙等，**不用 `Start-Sleep` / `Task.Delay`**（团队约定：不引入"睡"语义）。

---

## 1. 怎么搭这个实验（照抄可用）

用现成的两个夹具，不需要新写基础设施：

| 夹具 | 位置 | 作用 |
|---|---|---|
| `GatewayHost` | `tests/BuddyDeck.TestSupport/GatewayHost.cs` | 起**真 Kestrel 网关**，端口 **0**（系统分配），自带探活 |
| `FakeUpstreamHandler` | `tests/BuddyDeck.TestSupport/FakeUpstreamHandler.cs` | 假上游，**记录每个请求的完整头** |

**为什么用它们而不是手搓**：
- 端口 0 → **绝不占用 8787 / 3080**（8787 是全队的模型来源，杀了它全队断线）
- 假上游 → **不烧真配额、不碰真账号**
- `StartReadyAsync` 会轮询 `/healthz` 判就绪，**没有 `Start-Sleep` 猜时间**

### 骨架

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;

// ① 池子里放几个号（每个号的 accessToken 是后面认身份的凭据）
var vault = new TestVault();
for (var a = 0; a < accounts; a++)
    vault.Add(id: $"acc-{a}", uid: $"uid-{a}", accessToken: $"token-{a:D3}");

// ② 假上游：既是上游，也是"探针"
var inFlight = new ConcurrentDictionary<string, int>();   // 当前在途数
var peak = new ConcurrentDictionary<string, int>();       // 历史峰值

var fake = new FakeUpstreamHandler(req =>
{
    // ★ 认出这个请求用了哪个号 —— 判据就在这一行
    var auth = req.HeaderOrNull("Authorization") ?? "";
    var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth["Bearer ".Length..].Trim()
        : auth;

    // ★ 维护"此刻有几个在途请求用着这个 token"
    var now = inFlight.AddOrUpdate(token, 1, (_, v) => v + 1);
    peak.AddOrUpdate(token, now, (_, v) => Math.Max(v, now));

    // ★ 占住请求，把并发窗口撑开（否则测到的是串行）
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 300) Thread.SpinWait(100_000);

    inFlight.AddOrUpdate(token, 0, (_, v) => Math.Max(0, v - 1));
    return UpstreamResponses.SseText("ok");
});

// ③ 起真网关（端口 0，上游接假上游）
await using var host = await GatewayHost.StartReadyAsync(fake, vault);

// ④ 真并发打请求
var codes = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async i =>
{
    var body = new JsonObject
    {
        ["model"] = "hy3",
        ["stream"] = true,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = $"hi-{i}" }),
    };
    using var c = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    using var r = await host.Client.PostAsync("/v1/chat/completions", c);
    return (int)r.StatusCode;
}));

// ⑤ 取证：从假上游侧统计分布
var tokens = fake.Requests                                   // CapturedRequest 列表
    .Where(r => r.Path.Contains("/chat/", StringComparison.OrdinalIgnoreCase))
    .Select(r =>
    {
        var a = r.HeaderOrNull("Authorization") ?? "";
        return a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : a;
    })
    .GroupBy(t => t)
    .ToDictionary(g => g.Key, g => g.Count());

Console.WriteLine($"用到号数 = {tokens.Count}/{accounts}");
Console.WriteLine($"最忙号承担 = {(tokens.Count == 0 ? 0 : tokens.Values.Max())} 个请求");
Console.WriteLine($"峰值并发 = {(peak.IsEmpty ? 0 : peak.Values.Max())}");
```

**跑法**（探针放 `docs/` 下时要用 ProjectReference 引 TestSupport）：

```bash
cd D:\workfandaiapi\_next
dotnet run --project docs\<你的探针目录>\<探针>.csproj
```

> ⚠ 探针是**一次性**的，验证完就该删。它要真起网关，**不适合进 CI**（CI 里没有上游）。
> 真正该进测试的是结论沉淀成的**不变量**（例如"号数 ≥ 并发数时不得出现重叠"），而不是这份脚本。

---

## 2. 这次实测到的数字（复核过两遍）

配置：`hy3` 模型、流式、假上游占用 300ms、每场景多轮。

| 场景 | 用到号数 | 最忙号承担 | **峰值并发** | 多轮一致性 |
|---|---|---|---|---|
| 3 个号 / 6 并发 | **3/3** | 2 个请求 | **2** | 完全一致 |
| 2 个号 / 6 并发 | **2/2** | 3 个请求 | **3** | 完全一致 |
| **6 个号 / 6 并发** | **6/6** | **1 个请求** | **1（零重叠）** | 完全一致 |

全部请求 HTTP 200，无失败。分布极其规整（如 3 号场景每轮都是 `token-000:2, token-001:2, token-002:2`）。

### 这组数字说明什么

`AccountPool.Pick` 的排序是：

```csharp
lock (_gate)
{
    var picked = candidates
        .OrderBy(...)                     // 优先快过期的
        .ThenBy(m => m.SelectionCount)    // 其次用得最少的 ← 轮转依据
        .First();
    picked.SelectionCount++;              // ← 在锁内同步递增
    ...
}
```

`SelectionCount++` 在 `lock` 内同步执行，所以**并发下确实在轮转**。

**但 `Pick` 没有"在途"概念** —— 它只看历史累计，不看"这个号此刻是否正忙"。所以：

- **号数 ≥ 并发数** → 零重叠（轮转足以覆盖）
- **号数 < 并发数** → 必然复用（3 号时每号 2 个，2 号时每号 3 个）

### 由此得到的一条判断

**"并发请求不压到同一个号"这个目标，在号数 < 并发数时是数学上不可达的** ——
请求比号多，总得有号承担两个。所以正确的目标不是"消灭复用"，而是：

> **给单号叠加设一个上限，并明确超限时的策略**（排队等待 / 换号 / 换模型 / 直接拒绝）。

这也解释了为什么加"在途计数"之前必须先定策略：**否则只是加了一个没人知道该设多少的旋钮。**

---

## 3. 踩过的坑（下次少走弯路）

### 坑 1：判据选错，白跑

见 §0 ①。用 `SelectionCount` 当判据量不出瞬时重叠。**先想清楚"证据在哪一侧"，再写代码。**

### 坑 2：假上游立刻返回 → 测到的是串行

见 §0 ③。不撑开并发窗口，结论会假阳性为"无重叠"。

### 坑 3：`IGrouping.Count` 是扩展方法，不是属性

```csharp
byToken[0].Count      // ❌ 编译错 CS0019（这是方法组）
byToken[0].Count()    // ✅
```
`Dictionary<K,V>` 的 `Count` 是属性，`IGrouping<K,V>` 的是扩展方法 —— 长得一样，编译期报错信息还不直观。

### 坑 4（不属于本实验，但同源）：**"命令返回了"不等于"成功了"**

我在另一个任务里踩过：`--selftest` 在真实网关路径下**永久挂死**，但因为我没有超时兜底，
"挂死"和"跑完了"在我眼里长得一模一样，导致我把一次挂死当成了通过。

**规矩：任何可能挂死的验证都要有看门狗。**

```powershell
$p = Start-Process -FilePath "dotnet" -ArgumentList (...) -NoNewWindow -PassThru
if (-not $p.WaitForExit(30000)) { "❌ 超时未退出 = 挂死"; $p.Kill() }
```

**超时即判失败**，不要靠"看它输出了什么"来判断。

---

## 4. 方法论小结（可搬到别的验证上）

| 原则 | 一句话 |
|---|---|
| 外部取证 | 想验证 A 对 B 做了什么，就去 B 那边取证据，别读 A 的内部计数器 |
| 多轮 | 单次结果 = 巧合；多轮一致才是规律 |
| 撑开窗口 | 测并发就要让并发真的有可能发生，否则测的是串行 |
| 看门狗 | 任何可能挂死的验证都要有超时，超时即失败 |
| 不碰共享资源 | 端口 0、假上游；绝不占 8787 / 3080，绝不烧真配额 |
| 探针即弃 | 一次性脚本验证完就删；把它沉淀成**不变量**再进测试 |

---

## 附：本次用到的夹具速查

`GatewayHost`（`tests/BuddyDeck.TestSupport/GatewayHost.cs`）：

| 成员 | 说明 |
|---|---|
| `StartAsync(fake, vault, apiKey, configure, ct)` | 起网关，返回后可读 `BaseAddress` |
| `StartReadyAsync(...)` | 同上，但会**轮询 `/healthz` 确认就绪**（推荐） |
| `BaseAddress` / `Port` / `Client` | 地址、端口（系统分配）、已配好 BaseAddress 的 HttpClient |
| `DumpLog()` | 取缓存的网关日志（探活失败时用来解释原因） |
| `BearerRequest(method, path, apiKey)` | 造一个带鉴权头的请求 |

`FakeUpstreamHandler`（`tests/BuddyDeck.TestSupport/FakeUpstreamHandler.cs`）：

| 成员 | 说明 |
|---|---|
| `new FakeUpstreamHandler(req => resp)` | 按请求定制响应 —— **并发实验要的就这个重载**（可读 `req` 并记录） |
| `Requests` | 已捕获的全部请求（`CapturedRequest` 列表，按到达顺序） |
| `Last` / `Count` / `Paths` | 便捷视图 |

`CapturedRequest`：

| 成员 | 说明 |
|---|---|
| `Method` / `Url` / `Path` / `Body` / `ContentType` / `Sequence` | 基本信息 |
| `Headers` | 完整头字典 |
| `Header(name)` / `HeaderOrNull(name)` / `HasHeader(name)` | 取头（**认身份走这个**） |
| `Json()` | 把 Body 解析成 `JsonNode` |
