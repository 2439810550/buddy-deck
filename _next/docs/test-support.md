# 共享测试夹具（`tests/BuddyDeck.TestSupport`）

> 一份夹具，两边复用。gateway（t1）和 e2e（t4）引用的是**同一套「上游行为」假设**，
> 所以不会再出现「这边认为 429 该换号、那边认为该换模型」这种漂移。

---

## 0. 三十秒上手

```bash
# 你的测试项目加一条引用（路径按你的项目层级调整）
dotnet add tests/BuddyDeck.Gateway.Tests reference ../BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj
```

```csharp
using BuddyDeck.TestSupport;

// ① 光测假上游
var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);
var result = await client.ChatStreamAsync(new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);
Assert.Equal(UpstreamErrorKind.RateLimited, result.Kind);
Assert.Equal("/v2/chat/completions", fake.Last.Path);
```

```csharp
// ② 起一个真网关打进去
await using var host = await GatewayHost.StartReadyAsync(
    fake: new FakeUpstreamHandler(_ => UpstreamResponses.SseText(chunk)),
    vault: new TestVault().Add(),
    apiKey: "test-key");

using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions", "test-key");
request.Content = JsonContent.Create(new { model = "hy3", stream = true, messages = new[] { new { role = "user", content = "你好" } } });
using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
```

---

## 1. 项目定位

| | |
|---|---|
| 路径 | `tests/BuddyDeck.TestSupport/` |
| 类型 | classlib（`IsPackable=false`），**本身不含测试** |
| 引用 | `BuddyDeck.Core`、`BuddyDeck.Gateway` |
| 谁引用它 | `BuddyDeck.Gateway.Tests`、`BuddyDeck.E2E.Tests`、以及它自己的测试项目 |
| **绝不允许** | `BuddyDeck.Gateway` / `BuddyDeck.Core` 反向引用它 —— 夹具不能进生产依赖 |

它引用 `BuddyDeck.Gateway` 只有一个理由：`GatewayHost` 要知道网关的装配方式。
其余全部类型只依赖 `BuddyDeck.Core` 和 BCL。

---

## 2. `FakeUpstreamHandler` —— 可编程假上游

一个 `HttpMessageHandler`，在测试里冒充 WorkBuddy 上游。

### 2.1 三种构造

```csharp
// ① 固定响应（回什么都行，也忽略请求）
var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"hi"}}]}"""));

// ② 由响应对象构造（语法糖）
var fake = FakeUpstreamHandler.Returning(UpstreamResponses.RefreshOk("at-new"));

// ③ 按方法 + 路径路由，未命中回 404
var fake = FakeUpstreamHandler.Routing(
    UpstreamRoute.Chat(UpstreamResponses.SseText(chunkJson)),
    UpstreamRoute.Refresh(UpstreamResponses.RefreshOk("at-new")),
    UpstreamRoute.Catalog(UpstreamResponses.Catalog("hy3", "glm-5.3")));
```

未命中路由回的是 404 —— 这点是**刻意的**。静默回 200 会让「打错路径」这类 bug 变成一条永远绿的测试。

### 2.2 记录了什么

每次请求都会记一条 `CapturedRequest`：

| 成员 | 内容 |
|---|---|
| `Method` | HTTP 方法（大写） |
| `Url` | 完整 URL（含 query） |
| `Path` | 只要路径（路由/断言基本用这个） |
| `Headers` | **全部请求头**字典，大小写不敏感，值是 `string[]` |
| `Body` | **请求体全文**（`HttpContent` 只能读一次，所以这里替你读好了） |
| `ContentType` | 解码后的 Content-Type（它在 content headers 上，不在 `Headers` 里…**现在两个地方都有**） |
| `Sequence` | 这是第几次请求（从 0 开始） |

在上面加三样便利方法：

```csharp
fake.Last                        // 最后一次请求（一次都没收到会抛，不返回 null）
fake.Requests                    // 全部请求，按到达顺序
fake.Count                       // 一共几次
fake.Paths                       // ["/v2/chat/completions", "/v2/chat/completions"]
fake.RequestedModels             // 每个请求体里的 model 字段
fake.AuthorizationHeaders        // 每个请求的 Authorization —— 断言「换号了」用
fake.Clear()                     // 清记录，脚本不动
fake.Reset()                     // 清记录 + 卸脚本
```

`CapturedRequest` 自己的帮手：

```csharp
var r = fake.Last;
r.Header("Authorization")        // 没有 / 有多个值 都会抛
r.HeaderOrNull("X-User-Id")      // 没有回 null
r.HasHeader("X-No-User-Id")
r.Json()!["model"]!.GetValue<string>()   // 解析请求体（回的是全新副本，改它不影响记录）
```

### 2.3 线程安全

网关的流式路径会并发发请求，记录用锁保护。但 `Last` 这种「读最后一次」的断言在并发场景下语义本身就不稳 —— 并发用例请自己按 `Sequence` 挑。

---

## 3. `UpstreamResponses` —— 响应工厂

每个方法对应一种**真实出现过的上游行为**。写测试需要造什么，看这个清单就够。

### 3.1 正常

```csharp
UpstreamResponses.Text("""{"code":0}""")                    // 200 + JSON
UpstreamResponses.Text("<html>…</html>", HttpStatusCode.BadGateway, "text/html")
UpstreamResponses.Json("""{"a":1}""")
UpstreamResponses.Envelope(0, new JsonObject { ["x"] = 1 })  // {code,message,data}
UpstreamResponses.RefreshOk("at-new", refreshExpiresIn: 3600)
UpstreamResponses.Catalog("hy3", "glm-5.3")
```

### 3.2 流式（`text/event-stream`）

```csharp
// 一条数据一帧，末尾自动补 data: [DONE]
UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"你"}}]}""", """{"choices":[{"delta":{"content":"好"}}]}""");

// CRLF 行结束（有些上游就是这么发的）
UpstreamResponses.SseText([payload], crlf: true);

// ★ 分块边界自己给 —— 第 2 块把一行切成两半
UpstreamResponses.SseChunks(
    "data: {\"choices\":[{\"delta\":{\"content\":\"上\"}}]}\n\n",
    "data: {\"choices\":[{\"delta\":\"con",
    "tent\":\"下\"}}]}\n\n",
    "data: [DONE]\n\n");

// 自动按事件切块（partsPerFrame=2 时边界落在事件中间）
UpstreamResponses.SseFrames(payloads, partsPerFrame: 2);

// 没有 [DONE] 就断流（上游中途挂了）
UpstreamResponses.SseBroken(payload);

// 断在一帧中间（最后半条 JSON）
UpstreamResponses.SseAborted(completePayload, partialTail);

// SSE 里混一条非法 JSON（SseSanitizer 必须原样转发，不能丢）
UpstreamResponses.InvalidSsePayload(validPayload, """{"broken":""");
```

> **为什么要能控制分块边界**：网络上一次 read 拿到半行是常态，而块边界和 SSE 事件边界**没有任何关系**。
> `SseReader` 的增量扫行逻辑只有在这种输入下才被真正测到。

### 3.3 限流

```csharp
UpstreamResponses.TooManyRequests("账号被限流", resetAtMs: 1793731164678);  // 整个账号被限 → 换号
UpstreamResponses.ModelRateLimited("hy3");                                 // 429 + code 6004 → 换模型也行
```

两者都是 HTTP 429，区别在正文里有没有 `6004` —— 这正是 `UpstreamClient.Classify` 的判据。

### 3.4 凭证 / 服务端 / 脏数据

```csharp
UpstreamResponses.Unauthorized()                // 401
UpstreamResponses.Forbidden()                   // 403
UpstreamResponses.ServerError("boom", 500)      // 5xx（可指定状态）
UpstreamResponses.BadGateway()                  // 502
UpstreamResponses.ServiceUnavailable()          // 503
UpstreamResponses.HugeErrorBody(100_000)        // 超长错误正文（验 4096 截断）
UpstreamResponses.InvalidJson()                 // 200 但不是 JSON
UpstreamResponses.EmptyBody()                   // 200 但空正文
```

### 3.5 复用同一个响应

工厂出来的每个响应都是**新对象**，所以 `FakeUpstreamHandler.Returning(template)` 可以连用多次。
反过来说：**不要**自己 `new` 一个 `HttpResponseMessage` 然后重复用 —— 第一个请求之后它就被 Dispose 了。

---

## 4. `UpstreamScript` —— 「第 N 次请求返回什么」

固定响应的假上游表达不了「上游是有状态的」这件事。脚本队列专门管这个。

```csharp
var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底，不该走到"));

fake.Script(
    UpstreamResponses.TooManyRequests(),                  // 第 1 次：账号级限流
    UpstreamResponses.SseText(chunkJson, done));          // 第 2 次：好了
```

queue 用完继续用最后一步**，不是抛异常 —— 大多数用例只关心前两步，
把第 4、第 5 步也写出来是噪声。要断言次数直接读 `fake.Count`。

每一步也可以写成一个函数，按这次请求的内容分支：

```csharp
fake.Script(
    request => request.HeaderOrNull("Authorization") == "Bearer access-token-1"
        ? UpstreamResponses.ModelRateLimited("hy3")
        : UpstreamResponses.SseText(chunkJson));
```

> ⚠ **脚本是按「请求序号」走的**。同一个 `FakeUpstreamHandler` 上先跑了几次请求
> 再装脚本 —— 或者装完脚本又复用同一个 fake —— 序号早就过了你写的那一步，
> 取到的会是队列里对应位置（或最后一步）的响应，不是你期望的那个。
> 要换脚本就**换一个 fake**，或者先 `fake.Reset()`。
> （这条是真被踩到的：写文档示例的验证测试时就撞上了。）

### 4.1 `SwappingChatLoop` —— 换号的参考实现

`SwappingChatLoop` 是**测试侧**的一个最小「限流 → 换号 → 重试」循环。
它存在的意义是把三件夹具串起来，并且给出一个可对照的形状：

```csharp
var vault = new TestVault().Add(uid: "user-a").Add(uid: "user-b");

var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底"));
fake.Script(
    UpstreamResponses.TooManyRequests(),
    UpstreamResponses.SseText(chunkJson, done));

var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
pool.Replace(vault.Entries);

var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3");

Assert.Equal(2, attempts.Count);
Assert.Equal(UpstreamErrorKind.RateLimited, attempts[0].Kind);   // 第一轮被限
Assert.True(attempts[1].Ok);                                     // 第二轮（换了号）成功
Assert.NotEqual(attempts[0].MemberId, attempts[1].MemberId);     // 真的换了号
Assert.Equal("Bearer access-token-2", fake.AuthorizationHeaders[1]);
```

参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `model` | `"hy3"` | 冷却按模型记，所以这个要和你断言的一致 |
| `maxAttempts` | `5` | 有上限才不会在「所有号都被限」时死循环 |
| `roundsPerMember` | `1` | 每个号试几轮，超了就换下一个号 |

> ⚠ `SwappingChatLoop` **不是产品代码**。网关那条线自己不做重试
> （`GatewayApp` 只把失败映射成客户端状态码）。别把它拷进 `src/`。

---

## 5. `TestVault` —— 内存凭证工厂

```csharp
var vault = new TestVault()
    .Add()                                        // 默认：user-1 / ent-1 / workbuddy.cn，6 小时后过期
    .Add(uid: "u2", enterpriseId: "e2", domain: "corp.cn", label: "企业号")
    .Add(label: "快过期的", expiresAtMs: vault.ExpiresIn(TimeSpan.FromMinutes(5)))
    .Expired("过期号")                             // 已经过期
    .NoExpiry("上游没给过期时间")                   // expiresAtMs = 0
    .EmptyIdentity("空身份号")                     // ★ 无 uid / enterpriseId / domain
    .AddMany(5, "pool");                          // 批量
```

输出：

```csharp
vault.Entries                 // IReadOnlyList<VaultEntry>
vault.FirstCredential         // 第一个账号的 Credential
vault.CredentialOf(id)
vault.ToDocument()            // VaultDocument
vault.WriteTo(path)           // 走生产代码 Vault.Write（格式和真实文件一致）
vault.WriteToTempFile()       // 写到临时目录的新文件，返回路径
vault.ToVaultJson()           // 序列化文本
```

### 5.1 ★ 空身份账号（`EmptyIdentity`）

这是 `X-No-User-Id` / `X-No-Enterprise-Id` / `X-No-Department-Info`
三个头的**唯一测试对象**。

```csharp
var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

await client.ChatStreamAsync(new TestVault().EmptyIdentity().FirstCredential, "{}", null, null, CancellationToken.None);

Assert.Equal("1", fake.Last.Header("X-No-User-Id"));
Assert.False(fake.Last.HasHeader("X-User-Id"));       // 绝不能出现带值的那三个
```

用默认参数造不出空身份账号（默认全是有值的），所以单列了一个方法。

### 5.2 格式上的两个历史坑

`TestVault` 造的账号在**格式上**和真实保险库一致 —— 这两个坑都踩过：

```
id            "uuid:enterpriseId"     ← 末尾那个冒号是格式的一部分，别 trim
                                      个人账号就是 "uuid:"
expiresAtMs   数字（Unix 毫秒）
addedAt       ISO 字符串              ← 类型和上面不一样！
lastSeenAt    ISO 字符串
lastCheckinAt ISO 字符串
```

写错类型是**造数据发现不了**的（当年就是「读真实文件」那条测试抓的）。
所以夹具自己也会真的走一遍 `Vault.Write` → `Vault.Read` 往返来验证。

### 5.3 `VaultJson` —— 造**不合法**的保险库

```csharp
VaultJson.AllNumericTimestamps()   // 三个时间字段全用数字 → Vault.Read 会抛 JsonException
VaultJson.Blank()                  // 空文件
VaultJson.Truncated()              // 写到一半断电（原子写要防的就是这个）
VaultJson.WriteTo(path, content)   // 落盘
```

---

## 6. `FakeCredentialSource` —— 内存账号源

网关的 `VaultCredentialSource` 每次请求都读一遍**文件**。测网关时不要文件 IO，
也不要「刚导入的号还没落盘」这种时序问题，用这个：

```csharp
var source = FakeCredentialSource.EmptyIdentity();       // 一个空身份账号
source.Add(new Credential { AccessToken = "at-2", Uid = "u2" });
source.SetAccounts(new TestVault().AddMany(3).Entries);   // 模拟用户改了保险库
source.Clear();                                           // 模拟用户删光了
source.Load();                                            // IReadOnlyList<VaultEntry>
```

> ⚠ **它没有声明 `ICredentialSource`**，尽管它有那个接口的 `Load()`。
> 理由：`ICredentialSource` 定义在 `BuddyDeck.Gateway` 里，写死接口会让
> TestSupport 的编译依赖网关那条线。用的时候一行适配：
>
> ```csharp
> using BuddyDeck.Gateway;      // ← ICredentialSource 在这个命名空间里
>
> sealed class Adapter(FakeCredentialSource inner) : ICredentialSource
> {
>     public IReadOnlyList<VaultEntry> Load() => inner.Load();
> }
> ```
>
> 别漏了那条 `using BuddyDeck.Gateway` —— 漏了的话报的是
> `CS0246: 未能找到类型或命名空间名"ICredentialSource"`（真踩过）。
>
> 将来 `ICredentialSource` 挪进 Core 之后，这里再 `implements` 一次即可。

---

## 7. `GatewayHost` —— 起一个真网关

### 7.1 ★ 什么时候可用

| 条件 | 说明 |
|---|---|
| ✅ **现在就可以用** | `src/BuddyDeck.Gateway/Program.cs` 已经存在（t1 的宿主入口已落地） |
| ✅ 依赖的类 | `GatewayApp` / `GatewayOptions` / `ModelCatalog` / `BuildInfo` / `ChatObservers` / `ICredentialSource` 都已在 `BuddyDeck.Gateway` 里 |
| ⚠ 它**不走** `Program.cs` 的 Main | 走的是 `WebApplication.CreateSlimBuilder()` + 手工装配 DI。理由见 7.3 |
| ⚠ 它**不打真实上游** | 必须传 `fake`，否则默认给一个只回 `{"code":0}` 的假上游 |

**如果 `Program.cs` 消失了或者被大改**（比如装配方式换了），`GatewayHost.StartAsync`
里的 DI 那一坨需要跟着改 —— 它是「网关怎么装配」的第二份实现，改网关装配时记得同步。

### 7.2 用法

```csharp
// 一步到位：起 + 等到 /healthz 通
await using var host = await GatewayHost.StartReadyAsync(
    fake: new FakeUpstreamHandler(_ => UpstreamResponses.SseText(chunkJson, "[DONE]")),
    vault: new TestVault().Add(),
    apiKey: "test-key");          // null = 不鉴权

host.BaseAddress                  // "http://127.0.0.1:53127"
host.Port                         // 53127（系统分配的）
host.Client                       // 配好 BaseAddress 的 HttpClient（Timeout 是 Infinite）
host.Application                  // WebApplication（要读 DI 里的东西时用）
host.CapturedLog                  // 宿主产生的日志行
host.DumpLog()                    // 拼成一段文本

// 需要自己控就绪时机时，分两步
await using var host2 = await GatewayHost.StartAsync(fake: fake, vault: vault);
var health = await host2.WaitForHealthAsync(TimeSpan.FromSeconds(15));
```

`startedAt` / `buildId` 这类字段直接读 `/healthz` 的 JSON：

```csharp
var health = await host.WaitForHealthAsync();
Assert.True(health["ok"]!.GetValue<bool>());
Assert.True(health["buildId"]!.GetValue<string>().Length > 0);
```

### 7.3 三条硬约束（都在代码里落实了）

**① 端口 0，绝不占 8787 / 3080**

```csharp
builder.WebHost.UseUrls("http://127.0.0.1:0");   // 让内核挑一个空闲端口
```

起来之后从 `IServerAddressesFeature` 读回真实端口。
这**不是** `ListenLocalhost(0)` —— 那个写法的端口 0 语义在文档里是含糊的。

8787 是用户正在跑的网关（也是 DSH 自己的模型来源），3080 是 DSH Web。
夹具自己的测试里有一条断言专门钉住这一点：

```csharp
Assert.NotEqual(8787, host.Port);
Assert.NotEqual(3080, host.Port);
```

**② 就绪判定是轮询 `/healthz`，不是 `Start-Sleep`**

```csharp
public async Task<JsonNode> WaitForHealthAsync(TimeSpan? timeout = null, CancellationToken ct = default)
```

实现是「带总时限的 `CancellationTokenSource` + 立刻探测」的循环：探到就返回，
超时抛 `TimeoutException`（消息里带宿主日志），绝不猜时间。
代码里**没有任何 `Thread.Sleep` / `Task.Delay`**。

**③ 日志进内存缓冲区**

`ClearProviders()` + 一个自己实现的 `BufferLoggerProvider`。好处：
不污染测试输出、探活失败时 `DumpLog()` 能解释原因、宿主漏异常时看得见。
缓冲区有 500 行上限（一个跑飞的测试不该把内存吃光）。

### 7.4 为什么是真端口而不是 `TestServer`

端到端要验的东西里有一半是**传输层**的：真流式（边收边发、不缓冲）、
`Content-Length`、`Host` 头检查、连接中断。这些用 in-memory 的 `TestServer`
测不出来 —— 它在管道里就把响应攒完了。

### 7.5 串行（⚠ 这条踩过坑，务必读完）

宿主测试会真的绑端口。用同一个 collection 让它们串行跑：

```csharp
[Collection("GatewayHost")]          // DisableParallelization = true
public class MyHostTests { }
```

#### ⚠⚠ `[CollectionDefinition]` **不跨程序集生效**

上面那个 `[Collection]` 标记**只在「集合定义」也在同一个程序集里时才管用**。

```csharp
// 必须**写在每个用它的程序集里**，各一份：
[CollectionDefinition("GatewayHost", DisableParallelization = true)]
public class GatewayHostCollection { }
```

**为什么这是最阴的一类缺陷**：`[Collection("某未定义的集合名")]`
**不报错、不警告** —— 编译器不管，xUnit 也不抛。它只是**找不到定义**，
于是退化成「每个类一个匿名集合」= **全并行**。

**实测代价**（2026-10 这次）：
```
tests/BuddyDeck.Gateway.Tests   6 个类标了 [Collection("GatewayHost")]
                                但定义只在 TestSupport.Tests 里 → 标记全失效
结果：393 条里失败 103 条，而且**每次跑失败数都不一样**
      一度被当成「某个大改动把网关改坏了」

补上本程序集的定义之后：失败 1 条，**连跑 3 次完全一致**
```

**症状识别**：**一批无关测试同时红，且失败数每次不同** —— 那是并行互踩，
不是你的代码坏了。先看这个程序集里有没有 `CollectionDefinition`。

**当前三份定义**（内容相同、**必须各自存在**，不是重复代码）：
```
tests/BuddyDeck.TestSupport.Tests/CollectionDefinitions + GatewayHostTests.cs
tests/BuddyDeck.Gateway.Tests/CollectionDefinitions.cs
tests/BuddyDeck.E2E.Tests/CollectionDefinitions.cs
```
改一份时另外两份要一起改。

#### 互踩的通道是什么

顺带澄清一个容易猜错的地方：**它不是靠共享对象状态互踩的**。
每个用例起的是自己的 `GatewayHost`（自己的池、自己的假上游），
对象层面是隔离的。踩在**机器级资源**：

```
· 每个宿主都真的绑端口、真的起 Kestrel —— 并发时同时 5~8 个宿主
· 每个宿主都真的发 HTTP —— 抢线程池与 socket
· 「起宿主 → 轮询 /healthz → 断言」这条链在负载下被拖长
  → 对时序敏感的用例随机红
```

---

## 8. 完整可复制示例

### 8.1 测账号池：首次 429 → 换号 → 再次 200（验收场景）

```csharp
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class 换号重试
{
    private static string Chunk(string content)
        => "{\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"hy3\","
         + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + content + "\"}}]}";

    [Fact]
    public async Task 首次429换号后成功()
    {
        var vault = new TestVault()
            .Add(uid: "user-a", label: "被限流的号")
            .Add(uid: "user-b", label: "正常的号");

        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底，不该走到"));
        fake.Script(
            UpstreamResponses.TooManyRequests("账号被限流"),
            UpstreamResponses.SseText(Chunk("换号成功")));

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3");

        Assert.Equal(2, attempts.Count);
        Assert.Equal(UpstreamErrorKind.RateLimited, attempts[0].Kind);
        Assert.True(attempts[1].Ok);
        Assert.Contains("换号成功", attempts[1].Sse);

        Assert.Equal(2, fake.Count);
        Assert.Equal("Bearer access-token-1", fake.AuthorizationHeaders[0]);
        Assert.Equal("Bearer access-token-2", fake.AuthorizationHeaders[1]);
    }
}
```

### 8.2 端到端：起真网关，验流式聊天

```csharp
[Collection("GatewayHost")]
public class 流式聊天
{
    [Fact]
    public async Task 上游成功时响应以_DONE_结束()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant","content":"你好"}}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake, vault: new TestVault().Add());

        using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(
            """{"model":"hy3","stream":true,"messages":[{"role":"user","content":"你好"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", text);

        // 假上游那边也收到了真的聊天请求
        Assert.Equal("/v2/chat/completions", fake.Last.Path);
    }
}
```

### 8.4 断言一个 HTTP 端点（含鉴权与「字段真的在变」）

新增一个端点后，**别只断言「返回 200」** —— 那验不出三类最常见的静默失败：
鉴权没接上、JSON 键名对不上消费方、以及**回的是常量而不是真实数据**。

下面这份模板三样都覆盖（`/metrics` 就是照它验的）：

```csharp
[Collection("GatewayHost")]   // 宿主要真绑端口 —— 不串行会互相踩（见 §7.5）
public class 新端点验证
{
    [Fact]
    public async Task 带对key_可达且返回200()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "test-key");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "test-key");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task 无key与错key_各返回401()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "test-key");

        // 无 key
        using var anonymous = await host.Client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // 错 key
        using var wrong = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "nope");
        using var wrongResponse = await host.Client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
    }

    [Fact]
    public async Task 空状态下不报500()
    {
        // 网关还没收到任何请求时也必须能正常回
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "test-key");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "test-key");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 字段随真实请求变化_不是常量()
    {
        // ★ 这条最关键：只断言「能反序列化」的话，
        //   一个永远回全 0 的端点也能过。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"ok"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake, vault: new TestVault().Add(), apiKey: "test-key");

        // 先制造真实流量
        using (var chat = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions", "test-key"))
        {
            chat.Content = new StringContent(
                "{\"model\":\"hy3\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8, "application/json");
            using var r = await host.Client.SendAsync(chat);
            await r.Content.ReadAsStringAsync();
        }

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "test-key");
        using var response = await host.Client.SendAsync(request);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(1, json["totalRequests"]!.GetValue<long>());   // ← 真的动了
    }
}
```

#### `/metrics` 的鉴权策略（写清楚，免得被「顺手豁免」）

```
GET /metrics   →  **要求 Bearer key**，策略与 `/v1/models` **完全一致**
GET /healthz   →  免鉴权（探活窗口没有 key，也不该要）
```

**为什么 `/metrics` 不豁免**：快照里有 `accountTag`（账号短标签）。
它虽然已经脱敏（只有 uid 前 8 位），但仍是**账号维度**的信息，
和 `/admin/*` 同级 —— 不该裸奔。

**这条有测试钉住**：`MetricsEndpointVerificationTests.与_v1_models_的鉴权策略一致`
断言两者的状态码相同。改策略时它会红。

#### ⚠ 关键键名不能改（改了界面静默读不到）

界面 `GatewayMetrics` DTO 靠**大小写不敏感匹配**读这些键：

```
totalRequests  attemptCount  httpSuccessRate  completionRate
averageLatencyMs  recent
```

`recent[]` 里每条记录的键（**三个必须显式映射的**已标注）：

```
at  model  status  durationMs  accountTag  attempts
streamed         ← 有 [JsonPropertyName("streamed")]
terminal         ← 有 [JsonPropertyName("terminal")]      ⚠ 不是 isTerminal
sentToUpstream   ← 有 [JsonPropertyName("sentToUpstream")]
```

> ⚠ `IsTerminal` ← `terminal` 这组**必须显式写 `[JsonPropertyName]`**：
> `System.Text.Json` 的 `PropertyNameCaseInsensitive` **不做 `Is` 前缀推理**，
> 少了那一行字段会**静默永远为默认值**（编译不报、运行不报）。

#### ⚠ 用 `GatewayHost` 测指标时的一个坑（已修，值得知道）

`GatewayHost` 默认**挂上了真的 `RequestMetrics`**。
早先它没挂，于是 `/metrics` 虽然回 200，但回的是**空快照**
（`GatewayApp.MetricsAsync` 的 `_metrics?.Snapshot() ?? new MetricsSnapshot()` 回退）——
表现为「发了几次真请求后 `totalRequests` 仍是 0」，
很容易把排查方向带到「端点 JSON 写错了」。

那个空快照回退**本身是对的**（没接指标时给结构完整的空对象，比 404 好），
但**测试宿主不该默认处于「没接指标」状态** —— 起一个宿主就该是个完整的宿主。

### 8.5 验身份头：空身份 vs 有身份

```csharp
[Fact]
public async Task 空身份发_X_No_族()
{
    var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
    var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

    await client.ChatStreamAsync(new TestVault().EmptyIdentity().FirstCredential, "{}", null, null, CancellationToken.None);

    Assert.Equal("1", fake.Last.Header("X-No-User-Id"));
    Assert.Equal("1", fake.Last.Header("X-No-Enterprise-Id"));
    Assert.Equal("1", fake.Last.Header("X-No-Department-Info"));
    Assert.False(fake.Last.HasHeader("X-User-Id"));
}
```

---

## 9. 夹具自己的测试

夹具本身也是代码，坏了会让两边同时写出「绿的假测试」—— 那比没有夹具更糟。
所以它有两个测试项目：

```bash
cd D:\workfandaiapi\_next

# ① 夹具的行为测试（53 个）
dotnet test tests/BuddyDeck.TestSupport.Tests

# ② 这份文档里示例代码的编译 + 运行验证（15 个）
dotnet test tests/BuddyDeck.DocSamples.Tests
```

### 9.1 `BuddyDeck.TestSupport.Tests` —— 行为

| 文件 | 覆盖什么 |
|---|---|
| `FakeUpstreamHandlerTests` | 请求记录、路由、响应工厂的每一种、SSE 分块/断流/CRLF/脏数据 |
| `UpstreamScriptTests` | ★「首次 429 → 换号 → 再次 200」、脚本越界、按请求分支、无人可用不死循环 |
| `TestVaultTests` | 空身份账号的 `X-No-*` 头、过期时间、id 格式、保险库写读往返、时间字段类型 |
| `GatewayHostTests` | 端口 0 / 不占 8787·3080、`/healthz` 轮询、鉴权、`/v1/models`、流式 `[DONE]`、上游 429 不崩、非回环 Host 被拒 |

### 9.2 `BuddyDeck.DocSamples.Tests` —— 文档不会烂

**文档里的代码不会被编译器检查。** 夹具改了名、调了签名、换了参数顺序 ——
行为测试照样绿，文档悄悄烂掉，而下游（t1 / t4）正是照着文档写的。

所以这份文档里贴的每一段示例，在 `tests/BuddyDeck.DocSamples.Tests/` 里
都有一个**逐字抄下来的副本**，它们必须能编译、能跑通。

改夹具的公开 API 时，**这个项目会先红** —— 那就是提醒你回来改文档。

> 这个项目已经抓到过两个真问题：
> ① 适配器示例漏了 `using BuddyDeck.Gateway`（编译不过）；
> ② 脚本队列示例复用了同一个 fake，序号早过了第 1 步（断言失败）。
> 两条现在都写进文档了。

---

## 10. 别踩的坑

| 坑 | 正确做法 |
|---|---|
| 自己 `new HttpResponseMessage` 重复用 | 用 `UpstreamResponses.*`（每次都是新对象） |
| 在假上游里 `await` 完再断言请求体 | 请求体已经在 `CapturedRequest.Body` 里了，直接读 |
| 装完脚本又复用同一个 fake | 换一个 fake，或者先 `fake.Reset()` |
| 用 `Start-Sleep` 等宿主起来 | `await GatewayHost.WaitForHealthAsync()` |
| 硬编码 8787 起测试宿主 | `GatewayHost.StartAsync()`（内部绑 0） |
| 以为 `429` 一律换号 | `ModelRateLimited()` 是**模型级**限流，换模型也行 |
| 以为 `expiresAtMs` 和 `addedAt` 是一个类型 | 前者数字、后者 ISO 字符串 |
| 在 `TestVault` 里 `Trim()` 掉 id 末尾的冒号 | 那是格式的一部分 |
| 用 `FakeCredentialSource` 当 `ICredentialSource` 忘了 using | `using BuddyDeck.Gateway;` |
| 让 `src/` 引用 TestSupport | 夹具不能进生产依赖 |
