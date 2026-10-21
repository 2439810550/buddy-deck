# 静默失败防护：检查器为什么存在

> 作者：design。任务：t31（用户裁定 —— 做静默失败防护比再写一个功能更有价值）。
>
> **这份文档的存在理由**：说不清价值的检查会被当噪音删掉。
> 所以每类检查都写清「它拦住了什么**真实发生过**的 bug」，
> 而不是「理论上可能发生什么」。
>
> 后续追加（t41）：§3.1「断言也要自证」这条团队规矩的落地、
> §4 StatusRail 缺陷收口、§5 检查器自己的两次翻车。

---

## 0. 先看这一类 bug 长什么样

本次会话里团队抓到 5 个 bug，**全都是同一类**：

> **编译通过、运行不报错、测试可能是绿的** —— 但行为是错的。

| 谁 | 静默失败 | 表面现象 |
|---|---|---|
| ui | `IsTerminal` 绑不上 JSON 键 `terminal` | 字段有值（默认 `true`）、看起来正常 |
| harness | 不可重试路径也记「中间尝试」 | 数字在，但翻倍 |
| observability | `TryGetValue<long>` 对 `{"prompt_tokens":2541}` 返回 false | JSON 合法、值永远是 null |
| observability | `AppendAllText` 并发截断 | 不崩，只是安静地少写 74 行 |
| gateway | `SpinWait` 忙等做并发测试 | 数据漂亮，实际全程串行 |
| **design** | **`StatusRail` 缺 `MeasureOverride` → 量成 0 → 永远不画** | **不报错、不抛异常、静默空白** |

**共同点：失败不发出任何信号。**

所以防护的重点不是「多写断言」，而是**在失败不发生信号的地方补一个信号**。

---

## 1. 为什么是独立脚本，不是 `dotnet test` 的红测试

**刻意的选择。** 三条理由：

1. **信号纯度**。团队已经论证过：把「风险面提示」做成红测试，
   会让 `dotnet test` 的绿/红语义变浑。工程师看到红会去修，
   但「未显式标注 JSON 名」**在单侧序列化、键名稳定时是完全正确的写法** ——
   逼人「修」一个不是 bug 的东西，比不报还糟。
   （`check_parity.py` 立过同一条规矩：**有意不做的东西如果每次都报，
   人就会开始忽略整个输出**。）

2. **视野**。「未标注 JSON 名的属性有多少」是一个**全 `src/` 的源码问题**。
   C# 测试看不见自己程序集之外的源码 —— 它天然做不到这件事。

3. **可重跑**。`check_parity.py` 的教训：一份会过期的记录价值在衰减，
   一个能重跑的检查价值不衰减。

**结论**：检查器住 `tools/`，独立入口，退出码语义独立。

---

## 2. 五类检查

### ① JSON 字段命名依赖隐式约定

**真实案例**：`IsTerminal`

```
网关卡发的键      "terminal"      （没有 is 前缀）
App 侧属性名      IsTerminal
System.Text.Json 的 PropertyNameCaseInsensitive
                  → 只做「大小写无关的**完整名字**比较」
                  → **不做 Is 前缀推理**
结果              "terminal" 绑不上 IsTerminal
                  → 属性留在**默认值 true**
                  → 「所有失败的中间尝试都被当成终局结果」
```

**为什么特别危险**：默认值恰好是 `true`，看起来完全合理。
编译不报、运行不报、字段还有值。

**检查什么**：列出所有「含 JSON 处理 + public 属性 + 未显式 `[JsonPropertyName]`」的位置，
把 `Is*`/`Has*`/`Can*` 等**前缀形状的布尔**标为高危。

**为什么不一律判成 bug**：见 §1 第 1 条。产出的是**风险面清单**。

**注意脚本自己踩过的坑**：第一版正则只匹配属性所在那一行，
漏掉了「特性写在上一行」的写法 —— 于是把**已经修好的** `IsTerminal`
（它现在有 `[JsonPropertyName("terminal")]`）误报成风险点。
**修法**是把特性行一起纳入匹配（`PROPERTY_RE` 的 `attrs` 组）。
这条提醒：检查器自己的误报率也是要验的。

**这条检查顺带发现的现存项**：见 §4。

---

### ② 并发写入丢数据

**真实案例**：`File.AppendAllText` 并发截断

```
8 线程 × 50 条 = 期望 400 行
实际落盘       326 行          （少 74 行）
```

`AppendAllText` 内部是 `open(append) → write → close`，**不是原子操作**。
两个线程同时走到这里会互相截断。**它不崩、不抛异常**。

**为什么弱断言验不出来**：

```csharp
Assert.True(lines.Count > 0)              // 326 > 0  ✅ 通过
Assert.True(lines.Count > previousCount)  // 也在增长 ✅ 通过
```

**正确的断言模板**：

```csharp
Assert.Equal(expected: N, actual: lines.Count);      // 精确等于
Assert.Equal(expected: 0, actual: badLines.Count);   // 坏行精确为 0
```

**检查器怎么证**：脚本**真的跑两版**（无锁 / 有锁），并且
**在同一次运行里同时验证「弱断言会通过、强断言会失败」** ——
这直接证明了「为什么必须用强断言」。

> ⚠ 竞态受调度影响，无锁版偶尔可能不丢。所以脚本对
> 「无锁必须丢」**不做硬断言**，只如实报告观察到的数字 ——
> 把这个偶然性写进断言会让检查变得时红时绿。

---

### ③ 类型转换静默返回默认值

**真实案例**：`JsonValue.TryGetValue<long>` 对 `{"prompt_tokens": 2541}` 返回 **false**

因为 2541 是 **int**，而 `TryGetValue<T>` 要求底层类型**精确匹配**。
于是 token 数**永远是 null**。JSON 合法、解析不报错、值就是没有。

**实测数字**（`python tools/prove_json_numeric_backing.py`）：

```
只写 TryGetValue<long>：
  {"prompt_tokens": 2541}          int     → false  ❌ 拿 null
  {"prompt_tokens": 0}             int     → false  ❌
  {"prompt_tokens": 2147483647}    int     → false  ❌
  {"prompt_tokens": 2147483648}    long    → true   ✅
  {"prompt_tokens": 9999999999999} long    → true   ✅
  {"prompt_tokens": 0.22}          double  → false  ❌
  {"prompt_tokens": "2541"}        string  → false  ❌

  7 种输入里 5 种静默失败（约 71%）
```

**正确范式**（`src/BuddyDeck.Core/UsageLedger.cs` 的 `Long()` / `Double()`）：
**逐类型试** `int → long → double → decimal → string`。

**检查什么**：找出「只试单一数值泛型、且附近没有转换兜底」的位置。

**脚本自己踩的第二个坑**：第一版把**注释行**也算了进去 ——
`AnthropicEndpoints.cs` 里有 3 行注释在**解释**这个 bug，
全被误报成风险点。修法是跳过 `//` / `*` / `/*` 开头的行，
并且**兜底判定的窗口里也要剔掉注释**（否则一句「该逐类型试」的注释
会被当成「已经做了逐类型试」）。

> **这两次误报都是同类教训**：检查器的**信噪比**必须自己先验一遍，
> 否则它会被当噪音删掉，那就等于没有。

---

### ③b ★★ 只试 `TryGetValue<double>` —— 整数读不出，兜底静默得 0

**真实案例**：features 在 t57 发现 —— 兜底分支只写 `TryGetValue<double>`，
而真机的 `CapacityRemain` / `CapacitySize` **恰好全是整数**，
于是兜底字段在真机上**永远读不出来，且返回 0 不报错**。

#### 实测：`TryGetValue<T>` 的匹配行为分成**两条路径**

这是本节最关键的发现。我用真实 C# 探针跑了全部组合，结果**不是一句话能概括的**：

```
路径 A：从 JSON 文本反序列化（JsonNode.Parse）—— 真实网络响应走这条
  Parse("42")    → kind=Number  int=true   long=true   double=true   ← 都行
  Parse("42.0")  → kind=Number  int=false  long=false  double=true
  Parse("42.5")  → kind=Number  int=false  long=false  double=true
  Parse("1e3")   → kind=Number  int=false  long=false  double=true

路径 B：在 C# 里用 JsonValue.Create 构造 —— **不经过 JSON 文本**
  Create(42)[int]    → kind=Number  int=true   long=false  double=false  ← !!
  Create(42L)[long]  → kind=Number  int=false  long=true   double=false  ← !!
  Create(42.0)[dbl]  → kind=Number  int=false  long=false  double=true
  Create(42m)[dec]   → kind=Number  int=false  long=false  double=false  decimal=true
```

**`JsonValue.Create(int)` 的形状是「只认 int」—— 连 `long` 和 `double` 都不认。**

⇒ 所以「只写 `TryGetValue<double>`」的后果**取决于数据来源**：

| 数据来源 | 整数能否被 `double` 读到 | 后果 |
|---|---|---|
| JSON 文本（真实网络响应） | **能**（`Parse("42")` double=true） | 不会出错 |
| `JsonValue.Create`（手工构造） | **不能** | **静默得 0** |

**这就是为什么本检查只报「位置」而不下「一定有 bug」的结论** ——
同一个写法在不同来源下后果不同，**静态判不出来**。
（features 踩的正是路径 B。）

#### ⚠ 我第一次做这条检查时，它**恒为 0 命中** —— 是注入自证抓出来的

第一版的「已有转换兜底」判据里写了 `GetValue<double>` 做子串匹配。
但 **`GetValue<double>` 是 `TryGetValue<double>` 的子串** ——
于是**每一条**待检语句都被判成「已有兜底」，检查永远报 0。

**这个缺陷的方向性后果很严重**：0 命中看起来像「仓库很干净」，
我会据此得出「无隐患」的结论 —— **而实际上检查根本没在工作。**

修法：加词边界 `(?<![\w])GetValue<(...)`，并加一条**常驻自证**：

```
③b_GetValue不能命中TryGetValue子串（子串重叠回归）
```

> 这已是同一个毛病**第二次**发作（上次是断言审计的 `>\s*[\w]+` 吃掉 `> 0`）。
> 见 §7 车 4 —— **子串/重叠匹配会让一个判据悄悄吃掉另一个判据的样本。**

#### 注入自证（实测输出）

造一个真实文件，只写 `TryGetValue<double>`：

```csharp
// src/BuddyDeck.Core/__ZzProbe.cs（注入用，已删）
if (o[k] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
return 0;
```

检查立刻报出：

```
③b 只试 TryGetValue<double>（整数读不出）
   只试 double 且无整数兜底的位置：1 处
       src/BuddyDeck.Core/__ZzProbe.cs:9  试过=['double']
         if (o[k] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
     修法：按 int → long → double → string 逐个试（范式见 Credits.TryReadNumber）
```

删除注入文件后回到 **0 处**。

#### 假阳性核查

| 样本 | 是否被报 | 期望 |
|---|---|---|
| `TryGetValue<int>` → `long` → `double` 逐个试 | 否 | ✅ 正确范式不误报 |
| 带 `Convert.To*` / `(long)` / `(int)` 强制转换 | 否 | ✅ 有兜底不误报 |
| 单写 `TryGetValue<double>` | **是** | ✅ 真实风险点 |

#### 现有仓库逐个核查：**14 个 double 读取点，全部无隐患**

判据：该处的判定窗口内**是否出现整数背书泛型**（int/long/short/byte/…）。
出现即「无隐患」—— 因为整数形态会被那个分支接住。

| 文件 | 行 | 试过的数值泛型 | 有整数兜底 | 判定 |
|---|---|---|---|---|
| `src/BuddyDeck.Core/AccountTransfer.cs` | 610 | double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/CatalogPromotions.cs` | 351 | double → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/Checkin.cs` | 299 | double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/Checkin.cs` | 347 | double → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/Credits.cs` | 307 | double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/SearchClient.cs` | 474 | double → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/UsageLedger.cs` | 263 | decimal → double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Core/UsageLedger.cs` | 286 | decimal → double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/AnthropicEndpoints.cs` | 688 | decimal → double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/AnthropicEndpoints.cs` | 710 | decimal → double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/MediaEndpoints.cs` | 567 | double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/MediaToolExecutor.cs` | 316 | double → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/ResponsesEndpoints.cs` | 1023 | decimal → double → int → long | 是 | 无隐患 |
| `src/BuddyDeck.Gateway/ResponsesEndpoints.cs` | 1037 | decimal → double → int → long | 是 | 无隐患 |

**「无隐患」的判定依据**（三选一，本表全部命中第 1 条）：

1. 判定窗口内出现了整数背书泛型（`int`/`long`/…）→ 整数形态被接住；
2. 或有 `Convert.To*` / 显式 cast → 转换兜底；
3. 或有 `(?<![\w])GetValue<T>` → 另一条读取路径兜底。

**⇒ 结论：features 的修复（`Credits.TryReadNumber` 的 `int → long → double → string`）
可以视为全仓库的基准范式。** 14 个点里没有一个只试 double。

**正确范式**（`src/BuddyDeck.Core/Credits.cs` 的 `TryReadNumber`）：

```csharp
if (json.TryGetValue<int>(out var asInt))    { value = asInt;    return true; }
if (json.TryGetValue<long>(out var asLong))  { value = asLong;   return true; }
if (json.TryGetValue<double>(out var asDbl)) { value = asDbl;    return true; }
if (json.TryGetValue<string>(out var text) && double.TryParse(...)) { ... return true; }
```

---

### ④ 测试无法区分串行与并发

**真实案例**：`Thread.SpinWait` 忙等做的并发测试
「8 号 / 8 并发都用到了 8 个号」看着很漂亮，**实际全程串行**。

原因：忙等占住线程，任务之间没有**时间重叠**窗口。
（团队里两个人先后栽在这里；ui 在
`docs/concurrency-verification-howto.md` §0③ 记过，
`AccountConcurrencyTests.cs` 的注释里也留了第一版的教训。）

**判据 A（硬判据）—— 峰值并发**：

```
真并发：同时在途峰值 > 1
串行  ：同时在途峰值 == 1
```

⚠ **光看「用到了几个资源」不够** —— 串行也能用到 8 个资源。
要看的是**瞬时重叠**，不是总量。
（这正是 ui 在 howto 里的原则：判据要从系统外部取，
而且 `SelectionCount` 那种历史累计值回答不了「此刻几个在途」。）

**判据 B（线索）—— 耗时比**：

```
N 个 t 秒任务：真并发耗时 ≈ t，串行耗时 ≈ N × t
实测 8 任务 / 60ms：串行 0.724s，并发 0.067s → 10.8×（理想 8×）
```

⚠ 耗时受机器负载影响，**只能当线索，不能当硬判据**。

---

### ⑤ ★★ `catch { return 默认值 }` —— 「读不到」伪装成「本来就没有」

**这是本轮第 10 类，也是形态最朴素的一类。** 它一共造成**两次真事故**：

#### 真实案例 ①：`/admin/pools` 的 `accountCount` 恒为 0

e2e 发现一个自相矛盾的现象：

```
GET /admin/accounts → 2 个号
GET /admin/pools    → accountCount = 0
```

同一个保险库，两个端点说的不一样。根因：

```
VaultPathOf(pool) 推出一个不存在的路径
  → Vault.Read(不存在的路径) 返回 null      ← 「读不到」
  → 代码写 `?? 0`                            ← 变成「0 个号」
```

于是「**读不到**」和「**这个池里真的没有号**」在返回值上**完全一样**。

gateway 的归纳是这一类最好的描述：

> **「读不到」和「本来就没有」长得一样，是它藏了这么久的原因。**

#### 真实案例 ②：目录刷新静默降级（t53）

```
FetchCatalogAsync(credential, userAgent: null)
  → 上游 400/12403（缺 UA）
  → 失败被 **Debug 级**吞掉
  → 永久回落到 16 个内置模型
  → 界面毫无异常
```

用户看到的是「模型列表就这样」，而真相是「**压根没刷到上游目录**」。
修好之后 `/v1/models` 从 16 变成 **32**（真机实测）——差别这么大，
却一直没有任何信号。

#### 共同形态

```csharp
try   { return Read(); }             // Read() 可能失败
catch { return 0; }                  // ← 失败与「空」不可区分
```

#### 判据（三条同时满足才报）

```
① catch 块里 return 了默认值（0 / -1 / null / false / "" / [] / default）
② try 块里做了 I/O（文件 / 目录 / HTTP）—— 这次失败原本意味着「数据取不到」
③ catch 块里没有日志（_log.* / LogXxx(...) / ILogger / Console.）
```

#### ⚠⚠ ③ 为什么必须有「I/O」这条限定 —— 假阳性会差 8 倍

这是本检查最关键的设计约束，**有实测数字**：

```
不做 I/O 限定：全仓命中 24 处   ← 其中 21 处是完全正确的写法
加了 I/O 限定：全仓命中  3 处   ← 只剩真正同形的
```

被排除的 21 处是**纯解析器**，例如：

```csharp
try   { innerJson = Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
catch (FormatException) { return null; }     // ✅ 完全正确
```

「输入不合法 → 返 null」是**正常的令牌返回**：调用方手里有完整信息
（输入是它自己给的），而**没有任何可丢失的外部状态**。
它和「读不到伪装成没有」是两回事。

**把这类一起报，就是往输出里灌噪音** —— 而本文件原则①说过：
**说不清价值的检查会被当噪音删掉。**

#### ⚠ 为什么判据是「没有日志」而不是「没返哨兵值」

因为**哨兵值没有统一约定**：

```
-1   在 AdminEndpoints.AccountCountOf 里是「不知道有几个」
     在别的地方可能是「未找到」
```

静态判不出哪个值是哨兵。而「**有没有日志**」是可判的**事实**：

```
有日志 → 运维能从日志里看出「这次是读不到」→ 两种情形可区分 ✅
无日志 → 两种情形真的长得一样               → 报出
```

这正是本条要守的东西，也是 gateway 那个修法的实质：

```csharp
// src/BuddyDeck.Gateway/AdminEndpoints.cs —— 修好的写法
if (!File.Exists(path))
{
    LogPoolVaultAbsent(display, path);   // Debug：新池没号是**正常状态**，不吵
    return 0;                            // 真的没有
}

try { return Core.Vault.Read(path)?.Accounts.Count ?? 0; }
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
{
    LogPoolVaultUnreadable(display, path, reason, ex.Message);   // Warning：读不到要看见
    return -1;                           // 「不知道有几个」≠「0 个」
}
```

**要点不是「返 -1 这个魔法值」，而是「两种情形产生了两种可观测的信号」。**

#### 自查现有仓库：逐个结论

`sigcheck⑦` 现在全仓命中 **1 处**（`--json` 的 `readFailureMasquerade`）：

| 位置 | 形态 | 结论 |
|---|---|---|
| `src/BuddyDeck.Core/UsageLedger.cs:751`<br>`ShouldRotate()` | `new FileInfo(_path)` 抛 `IOException`/`UnauthorizedAccessException` → `return false`（不轮转），**无日志** | ⚠ **同形，值得处理**。后果：查不到文件大小时静默不轮转 → 账本文件**无界增长**，而「文件很小」与「查不到」不可区分。写的人显然想过（注释：「轮转失败不该阻断记账」），但**代价从没被说出来**：不轮转 = 文件一直长 |

其余候选（24 处里的 21 处纯解析 + 2 处已排除的取消/有日志）**逐个判为不需要处理**，理由：
- **纯解析（21 处）**：无 I/O、无可丢失状态，返 null 是正确的令牌返回
- **`IGatewayClient.cs:900` 的 `catch (OperationCanceledException) { return (null, null); }`**：
  **刻意**的静默 —— 取消不是错误，而且它用元组的第二个元素（错误文案）显式表达了
  「这次没有错误」。检查器已把取消单独排除。
- **`AdminEndpoints.cs:852`**：就是修好的那个正样本（Warning + `-1`）

> ⚠ **脚本不下「这是 bug」的结论**（原则③）。上表是**我的人工判读**，
> 写在这里是因为任务要求「逐个给结论」；脚本只负责把位置摆出来。


**脚本自己踩的第三个坑（最有教学价值的一个）**：

第一版判据 A 用**纯 Python 忙等**造并发窗口，8 线程的**峰值只有 2**。
原因：**GIL** —— 纯 Python 字节码同一时刻只有一个线程在跑，
忙等的线程互相抢不到 CPU。

> **这和我们要防的 bug 是同一个失败模式的 Python 版**：
> 「看起来开了 8 个线程，实际没有并行」。
>
> 修法：改用 `threading.Event` **阻塞等待**（阻塞会释放 GIL），
> 峰值立刻稳定为 8。
>
> **这条本身就是判据 ① 的最佳示范** ——
> 如果我只写「我起了 8 个线程」就当并发成立，就正好复刻了要防的那个错误。

---

## 3. 检查器自身的对照组

`check_parity.py` 立的规矩：**检查器自己也要有对照组**。

本脚本的对照组分两层：

### 常驻对照组（`--selftest`，28 条）

```bash
python tools/check_silent_failures.py --selftest
```

它把**故意写错的数据**喂给**正式检查用的同一段逻辑**，断言它真的判失败。
关键设计：**不复刻检查逻辑** —— 复刻的话证明的只是「我复刻那份有效」。

| 用例 | 证明什么 |
|---|---|
| 前缀布尔识别_能认出 IsTerminal | 已知失效点会被标出来 |
| 前缀布尔识别_不误报 `Terminal`（已修好的写法） | **不误报**正确代码 |
| 前缀布尔识别_对普通属性名不误报 | `Online`/`Port`/`Model`… 不该被标 |
| 并发写入_无锁版观察到丢数据 | 这条检查**有判别力** |
| 并发写入_有锁版精确无丢无坏 | 断言模板**可用** |
| **弱断言_vs_强断言_弱断言会放过丢数据** | **本检查存在的核心理由** |
| 假并发判据A_真并发峰值 > 1 | 判据能识别真并发 |
| 假并发判据A_串行峰值恒为 1 | 判据能识别串行 |
| 假并发判据B_耗时比能区分 | 线索有效 |
| 数值泛型扫描_recognizes 逐类型试写法 | 对**正确范式**不误报 |

### 注入对照（一次性，已做过）

把 `PREFIX_BOOL_RE` 故意改成永不匹配的正则（模拟「检查器腐化」），
`--selftest` 立刻报：

```
失败  前缀布尔识别_能认出IsTerminal
      PREFIX_BOOL_RE 匹配 IsTerminal → None
自证：通过 9 · 失败 1
退出码: 1
```

还原后回到 `自证：通过 10 · 失败 0`，退出码 0。

> **这说明对照不是装饰**：检查器真的坏掉时，它会说出来。

---

### 3.1 ★ 团队规矩：**断言也要自证**（本轮升级，t41 落地）

#### 规矩本身

> **没有做注入验证的断言，不能假定它有判别力。**

这条由 captain 在 StatusRail 事件后升级为全队规矩。此前团队只对
**检查器**立过「要有对照组」的规矩 —— **断言这一层是空的**。

#### 怎么落到工具里（不只写进文档）

新增 `--audit-assertions` 独立入口：

```bash
python tools/check_silent_failures.py --audit-assertions
```

它扫全部测试文件，把**最容易没有判别力的断言形态**列出来：

| 形态 | 为什么弱 | 怎么改 |
|---|---|---|
| `Assert.True(x > 0)` | 「存在」—— 少了 90% 也能通过 | `Assert.Equal(期望数, 实际数)` |
| `Assert.True(a > b)` | 「增长」—— 增长多少不设界 | 改动场景断言精确增量 |
| `Assert.True(x != null)` | 只验「有值」—— 值对不对没验 | 补内容断言 |

**实测输出**（60 个测试文件 / 2798 处断言）：

```
命中弱形态 7 处（0.25%）

  tests/BuddyDeck.App.Presentation.Tests/StatusRailLayoutTests.cs:124
      Assert.True(rail.DesiredSize.Height > 0);
  tests/BuddyDeck.App.Presentation.Tests/StatusRailLayoutTests.cs:384
      Assert.True(good.DesiredSize.Height > 0);
  tests/BuddyDeck.Core.Tests/CatalogPromotionsTests.cs:228
      Assert.True(promo.EndMs > promo.StartMs);
  tests/BuddyDeck.Core.Tests/ConversationAffinityTests.cs:476
      Assert.True(affinity.Evictions > 0);
  tests/BuddyDeck.DocSamples.Tests/DocSamplesTests.cs:365
      Assert.True(host.Port > 0);
  tests/BuddyDeck.E2E.Tests/AnthropicE2ETests.cs:607
      Assert.True(e2e.Port > 0);
  tests/BuddyDeck.E2E.Tests/RequestBoundaryE2ETests.cs:464
      Assert.True(e2e.Port > 0);
```

**0.25% 的信噪比** —— 低到不会让人无视它，又不至于漏掉真问题。

> ⚠ **命中 ≠ 错**。存在性断言有时是恰当的（`e2e.Port > 0` 验的是
> 「起真网关后拿到了系统分配的端口」，这本来就该用存在性）。
> 这个入口只做一件事：**把「值得做一次注入验证」的地方摆出来**。
> 每条命中的作者要回答一句：**我把缺陷注入进去，这条会红吗？**

#### 这个工具自己也要自证（套娃防线）

captain 明确要求：**否则就是「审计断言判别力的工具本身没有判别力」的套娃。**

`--selftest` 里有 7 条专测这个入口，含**反向**用例：

| 用例 | 证明什么 |
|---|---|
| 断言审计_能认出 `>0` 这类弱断言 | 有判别力 |
| 断言审计_对精确断言不误报 | `Assert.Equal(400, n)` **不该**被报 |
| 断言审计_对坏行为精确为 0 的断言不误报 | `Assert.Equal(0, bad)` 不该被报 |
| 断言审计_跳过注释里的弱断言样本 | 注释不是代码 |
| 断言审计_能认出 `>变量` 这类增长断言 | 另一种弱形态 |
| 断言审计_对真实仓库有产出（非空壳） | 防「判据失效 → 0 命中」假绿 |
| **断言审计_判据互不重叠** | 防注入验证失效（见 §5.2） |

#### ⚠ 注入 → 变红的实测输出

把「存在性」那条判据故意改成永不匹配，`--selftest` 立刻报：

```
passed=17 failed=2 exit=1

FAIL: 断言审计_能认出 >0 这类弱断言
      `Assert.True(lines.Count > 0)` → 未命中（少了 90% 也能通过）
FAIL: 断言审计_判据互不重叠（>0 只被存在性判据命中）
      `Assert.True(lines.Count > 0)` 被 0 条判据命中（应为 1）
```

还原后 `passed=19 failed=0 exit=0`。

**两条一起红是有意义的**：第一条说「存在性检查失效了」，
第二条说「被 0 条判据命中」—— **后者正是前者的量化表述**。

---

## 4. 检查器当前发现的现存项

跑 `python tools/check_silent_failures.py -v` 可见完整清单。

### 4.1 高危（HIGH）：前缀布尔 + 有序列化器

| 位置 | 属性 | 网关发/对方发的键 | 现状 |
|---|---|---|---|
| `src/BuddyDeck.App/Services/IGatewayClient.cs:180` | `bool IsDefault` | `"isDefault"` | **恰好大小写无关匹配 → 能用**，但形状与 `IsTerminal` 完全相同 |

**为什么标出来**：`IsDefault` 现在能工作，靠的是
「网关恰好发了 `isDefault`」这个**巧合**。同一个文件里的 `IsTerminal`
就是因为对方发 `terminal`（无前缀）而绑不上、静默取默认值。

> **已转 ui（t32）处理** —— 他是该文件作者，「谁的文件谁改」更省沟通成本。

### 4.2 MEDIUM：前缀布尔但文件里无序列化器 —— **已标注，清零**

| 位置 | 属性 | 手工读的键 | 处置 |
|---|---|---|---|
| `src/BuddyDeck.Core/Checkin.cs` | `IsStreakDay` | `"is_streak_day"` | ✅ 已加 `[JsonPropertyName]` |
| `src/BuddyDeck.Core/Effort.cs` | `CanDisableThinking` | `"canDisableThinking"` | ✅ 已加 `[JsonPropertyName]` |

**加标注的理由不是「它现在危险」，而是「形状危险」**（captain 原话）：
将来谁加一句 `Serialize` 就静默中招，**代价不对称（标注 2 行 vs 事后查一天）**。

**标注里写清了「当前不经序列化器」这个事实依据** —— 否则下一个人会以为它已经是 HIGH。

> 这两条最后由 features 加（他在同一轮里自己也扫到了），
> 我的文档注释合了进去，**没有重复 attribute**。

### 4.3 ★ 盲区补洞：属性名 ≠ 手工读的键名

**features 指出「前缀布尔」这个启发式有盲区**，很准：

```csharp
public bool Supports { get; init; }        // 读的是 "supportsReasoning"
public bool OnlyReasoning { get; init; }   // 读的是 "onlyReasoning"
```

`Supports` **没有 `Is`/`Has` 前缀** —— 前缀启发式**完全扫不到它**，
但失配风险和 `IsStreakDay` 是**同一类**（写出去 `Supports`、读回来 `supportsReasoning`）。

**采纳他提的更本质判据**：

```
属性名 与 手工读键名 不一致  →  形状危险（不管有没有 Is/Has 前缀）
```

**实测**（新增检查 ⑤）：

```
命中 1 处：
  src/BuddyDeck.Core/Effort.cs:119  Supports  ← 手工读 supportsReasoning(L64)
```

**假阳性检查**（关键）：

| 属性 | 手工读的键 | 判定 |
|---|---|---|
| `IsStreakDay` | `is_streak_day` | 未报（snake_case 同形）✅ |
| `CanDisableThinking` | `canDisableThinking` | 未报（小驼峰同形）✅ |
| `OnlyReasoning` | `onlyReasoning` | 未报（同形）✅ |
| **`Supports`** | **`supportsReasoning`** | **报为危险** ✅ |

三类同形属性一个都没误报，只抓到真正不同形的那条。

### 数值抽取：8 处只试单一泛型

例如 `src/BuddyDeck.Core/MioraClient.cs:704` 的 `ReadLong`：

```csharp
if (value.TryGetValue<long>(out var l)) return l;
if (value.TryGetValue<string>(out var s) && long.TryParse(...)) return parsed;
```

它只试 `long` 和 `string`，**没试 `int`** —— 而 `{"code": 200}`
的 200 正是 int 背书 → 返回 `null`。
（与 `UsageLedger.Long()` 的写法对比：那边逐类型试了 5 种。）

**同样是提示，不是结论**：有些字段的背书确实稳定是 long
（比如毫秒时间戳），那种情况不试 int 也没问题。

---

## 5. 怎么用

```bash
cd D:\workfandaiapi\_next

# 人读（默认）
python tools/check_silent_failures.py

# 看完整明细
python tools/check_silent_failures.py -v

# 给 CI / 界面读
python tools/check_silent_failures.py --json

# 自证：断言检查器真的会失败
python tools/check_silent_failures.py --selftest

# 断言审计：哪些断言「可能没有判别力」
python tools/check_silent_failures.py --audit-assertions

# 数值背书复盘（检查 ③ 的证据脚本）
python tools/prove_json_numeric_backing.py

# 布局两趟语义复盘（解释 §4.4 那个假绿）
python tools/explain_layout_phases.py
```

**检查项一览**（跑默认模式的全在，③b 是 t59 新增）：

| 编号 | 检查什么 | 真实案例 |
|---|---|---|
| ① | JSON 命名依赖隐式约定 | `IsTerminal` ← `"terminal"` |
| ② | 并发写入丢数据 | `AppendAllText` 少 74 行 |
| ③ | 只试单一数值泛型 | `TryGetValue<long>` 对 int 返回 false |
| **③b** | **只试 `TryGetValue<double>`** | **真机整数字段全读成 0（t57）** |
| ④ | 测试无法区分串行与并发 | `SpinWait` 忙等实际串行 |
| ⑤ | 属性名 ≠ 手工读的键名 | `Supports` ← `supportsReasoning` |
| ⑥ | 断言判别力审计 | StatusRail 渲染断言是假绿 |

**退出码**：

| 场景 | 退出码 |
|---|---|
| 正常检查 / `--audit-assertions`（即使有风险面清单） | **0** |
| `--selftest` 全部通过 | 0 |
| `--selftest` 有失败（检查器腐化） | **1** |

> **正常检查永远是 0**。理由见 §1：风险面清单是「需要人看」，
> 不是「检查失败」。让它非零退出就等于把它变成一个会叫的噪音源。

---

## 5. StatusRail 缺陷收口（`MeasureOverride` 缺失，t40）

### 5.1 缺陷与根因

```
src/BuddyDeck.App/Controls/StatusRail.cs
  public sealed class StatusRail : FrameworkElement
  ← 整个类**没有 MeasureOverride**

Themes/Controls.xaml 的 <Style TargetType="bd:StatusRail">
  ← 只设了 Width，没设 Height

后果链：
  FrameworkElement 没有 MeasureOverride → WPF 把期望尺寸算成 0×0
  → OnRender 里 if (width <= 0 || height <= 0) return;
  → **永远什么都不画**
```

**它不报错、不抛异常、日志里什么都没有** —— 只是静静地不显示。
实测表现：ui 起假网关喂 6 个账号（数据层确认真有 6 条），界面上一条竖条都没有。

**为什么难查**：ui 的自述 ——

> 我先怀疑 DockPanel、又怀疑宽度、再怀疑截图时序，**查了三轮全错**，
> 因为"什么都不显示"看起来太像布局问题。
> 最后是靠对照实验 ——「换一个确定能工作的绑定看它是否正常」—— 才把方向掰回来。

### 5.2 修法：控件自己决定尺寸，不依赖使用方

**选 `MeasureOverride` 而不是在 Style 里设 `Height`**（ui 的判断，我认可）：

> Style 设 Height 只是掩盖症状 —— **下一次有人给 StatusRail 换个容器还会踩。**

`MeasureOverride` 按条数算高度（`条数 × 28 + 间隔`），受 `availableSize` 约束，
所以在任何容器里都能自己撑开。

**顺带做对的一件事**：抽出 `ComputeLayout` 供**测量与绘制共用**。
原来 `OnRender` 内部自己算 slot/gap；再写一套必然出现
「测出来的高度和画出来的内容对不上（底部被裁）」。现在两边调同一个函数。

### 5.3 落点清单（5 个文件）

| 文件 | 改动摘要 |
|---|---|
| `src/BuddyDeck.App/Controls/StatusRail.cs` | ① 加 `MeasureOverride`（按条数撑开，受 availableSize 约束）② 抽出 `ComputeLayout` 供测量与绘制共用 ③ 类注释记录缺陷与排查教训 |
| `src/BuddyDeck.App/Themes/Controls.xaml` | StatusRail 样式加注释说明**为什么不设 Height**（**没有加 Height 值**） |
| `tests/BuddyDeck.App.Presentation.Tests/StatusRailLayoutTests.cs` | 10 条用例：测量 > 0 / 随条数增长 / 算式一致性 / StackPanel 真渲染 / 2 条自证 |
| `tools/explain_layout_phases.py` | 布局两趟语义复盘（解释 §5.4 的假绿） |
| `docs/silent-failure-guards.md` | 本节 + §4.3 |

### 5.4 ★★ 我在这里发现了自己的断言是「假绿」

**这条比缺陷本身更值得记。**

第一版渲染断言我写成「塞进 `Border` + `RenderTargetBitmap` + 数非黑像素」，
**并假定它能守住这个缺陷**。

**注入验证（`MeasureOverride` 返回 0）发现：抓不到，照样通过。**

根因（WPF 布局两趟）：

```
Measure —— 自下而上，子元素报告 DesiredSize
Arrange —— 自上而下，父元素**给定**子元素的最终矩形

Border.ArrangeOverride 不看 child.DesiredSize，直接把整个可用矩形给它：
    DesiredSize        = 0×0        ← 缺陷在这里
    ActualWidth/Height = 56×200     ← 但 Arrange 还是给了这么大
    → OnRender 的 if (width <= 0) return; 不成立 → 照样画
```

**换成 `StackPanel`（尊重 `DesiredSize`）才有判别力。**

### 5.5 注入 → 变红的实测输出

给 `MeasureOverride` 注入 `return new Size(0, 0)`（复刻缺陷），**6 条红**：

```
失败 StatusRailLayoutTests.高度随条数增长
失败 StatusRailLayoutTests.有条数时测量结果必须大于零
     测量宽度为 0 —— 控件量成 0 宽，永远不会画
失败 StatusRailLayoutTests.测量高度与布局算式一致
失败 StatusRailLayoutTests.自证_不实现MeasureOverride的元素会量成零
失败 StatusRailLayoutTests.条数上界受单槽上限约束
失败 StatusRailLayoutTests.在尊重DesiredSize的容器里渲染必须画出像素
     有 4 个账号时画了 0 个像素，没有账号时画了 0 个
     —— 两者相同说明 StatusRail 根本没画东西（正是它曾经的缺陷）。

失败: 6，通过: 4，总计: 10
```

> 最后那条（StackPanel 版渲染）**就是被假绿事件修出来的** ——
> 改之前它在这一注入下**照样通过**。还原后逐字节校验一致（14978 字节）。

---

## 6. 这套检查的边界（它做不到什么）

**诚实列出来，免得下一个人高估它**：

1. **判不了「键名是否真的对得上」**。它只知道「没显式标注」，
   不知道对方实际发什么键。要判那个得跨仓库对比，或者跑端到端。
2. **判不了「并发写是否真的安全」**。检查 ② 复现的是 `AppendAllText`
   这个具体 API 的已知问题；生产代码改用别的方式写文件，
   得另判（脚本给的是**断言模板**，不是全自动检查）。
3. **判不了「这个泛型参数是否够」**。它只能看出「只试了一种」；
   「一种够不够」取决于数据来源，需要人判断。
4. **判不了控制流类的问题**。`check_parity.py` 早就指出：
   像「agent 循环有没有真的跑起来」这种，grep 根本判不出来，
   必须显式列进「待人工」那一档。

**共同点**：这套检查防的是「**检索/观测没被验证**」这一类，
防不了「**控制流没被验证**」那一类。后者要靠端到端测试。

---

## 7. 这个检查器自己翻过六次车（诚实记录）

**列出来不是为了自我批评，是为了让下一个人知道「检查器的信噪比也要验」。**
一个误报太多的检查器**比没有更糟** —— 它会让人开始无视整个入口。

### 车 1：正则漏匹配「特性在上一行」

第一版属性正则只匹配属性所在那一行，漏掉：

```csharp
[JsonPropertyName("terminal")]        // ← 特性在上一行
public bool IsTerminal { get; init; }
```

后果：把**已经修好的** `IsTerminal` 误报成高危。
**修法**：`PROPERTY_RE` 加 `attrs` 组，把特性行一起纳入匹配。

### 车 2：注释行被当成代码

`AnthropicEndpoints.cs` 里有 3 行注释在**解释** `TryGetValue<long>` 这个坑，
全被误报成风险点。
**修法**：跳过 `//` / `*` / `/*` 开头的行，**且兜底判定的窗口里也要剔掉注释**
（否则一句「该逐类型试」的注释会被当成「已经做了逐类型试」）。

### 车 3：把安全代码和危险代码报成同样的红

第一版把所有前缀布尔一律判 HIGH → `IsStreakDay`（从不走序列化器）和
`IsDefault`（真的走）**看起来一样红**。
**修法**：三级严重度（HIGH / MEDIUM / INFO），判据里加
「文件里有没有 `JsonSerializer.*` 调用」。

### 车 4：★ 判据互相重叠 → 注入验证失效

写「断言审计」时，我把「`> 0`」和「`> 变量`」写成两条并列判据，
但第二条的正则是 `>\s*[\w...]+` —— **`0` 恰好也是 `[\w]+`**，
于是第二条把第一条的样本也吞了。

**后果**：我把第一条判据改成「永不匹配」做注入验证时，
**自证依然全绿**（存在性断言被第二条兜住）。

> **这恰好说明了注入验证的价值**：如果我只删掉一条判据就以为验完了，
> 会得出「判据 0 是必需的」这个**错误结论** —— 其实它是冗余的。
>
> 而如果反过来（两条里有一条坏了但我没注入），我会以为保护网还在。

**修法**：按阈值形态显式区分（`[01]` vs `(?!\d)[A-Za-z_]`），
并加一条**常驻自证**「判据互不重叠」—— 断言
`Assert.True(x > 0)` **只被 1 条判据命中**。

### 车 5：★ 同一个「子串重叠」毛病第二次发作（t59，检查 ③b）

写「只试 `TryGetValue<double>`」这条检查时，我在**兜底判据**里写了
`GetValue<double>` 做子串匹配 —— 但 **`GetValue<double>` 正是
`TryGetValue<double>` 的子串**，于是**每一条**待检语句都被判成「已有兜底」。

**后果**：检查**恒为 0 命中**。

> **这个方向比车 4 更危险**：车 4 是「判据冗余」（无害），
> **本车是「判据失效」—— 0 命中看起来像「仓库很干净」，我会据此报「无隐患」，
> 而实际上检查根本没在工作。**

**修法**：加词边界 `(?<![\w])GetValue<(...)`，
并加**常驻自证** `③b_GetValue不能命中TryGetValue子串（子串重叠回归）`。

> ⚠ **同一个毛病两次发作，说明它不是偶发** —— 已升级为通用规则：
> **判据里的「存在性匹配」一律用词边界，不用裸子串。**
> 尤其注意 `A` 与 `TryGetValue<A>`、`GetValue<A>` 这种**包含关系**。

### 车 6：★ 正则缺 `re.M` → 自报数字**自相矛盾**（t74，检查 ⑤）

写「读不到 vs 本来就没有」这条检查时，我加了一条防呆自证：

> 「⑦在真实仓库上确实扫到了 catch 块（防扫描器失效）」

它的用意是好的 —— 防止「扫描器坏了 → 0 命中」被误读成「仓库很干净」
（车 5 的同款担忧）。**但它自己先坏了**：

```python
CATCH_START_RE = re.compile(r"^\s*\}?\s*catch\b")      # ← 没有 re.M
total_catches += len(CATCH_START_RE.findall(t))        # 对整段文本
```

`^` 在缺 `re.M` 时**只匹配整个字符串的开头**，所以对一段源码文本最多
只匹配第一行 → 计数恒为 0。

**症状是自相矛盾的数字**：

```
失败  ⑦在真实仓库上确实扫到了 catch 块
      全仓 catch 块 0 个，其中「I/O 失败且无日志」命中 1 处
                    ↑ 0 个                        ↑ 但命中 1 处
```

**「0 个 catch 块」与「在其中命中 1 处」不可能同时成立** ——
正是这个矛盾让我去查，而不是把它当噪音忽略掉。

**为什么主检查没受影响**：`check_read_failure_masquerade` 是**按行**喂给
`CATCH_START_RE.match()` 的（每行一个短字符串），那里 `^` 行为正确。
**只有「对整段文本计数」这条自证踩到了**。

> 教训（与车 4/5 同族但换了角度）：
> **判据在「按行喂」与「整段喂」两种用法下行为不同时，只有其中一种被验证过，
> 就一定会漏。** 现在正则自带 `re.M`，两种用法一致。

**修法**：`re.compile(..., re.M)` + 在正则旁写明为什么必须有它。
自证本身**完成了它的使命** —— 它是被自己的输出矛盾触发的。

### 共同教训（六车归纳）

> **检查器/断言的误报率与漏报率，都要自己先验一遍。**
> 五条车里三条是「误报」、两条是「漏报」——
> **它们全是靠注入自证或看真实输出发现的，没有一条是靠读代码想出来的。**

> **而「子串重叠」这一类已经发作两次** —— 凡是要判「某写法是否出现」，
> 先问一句：**它会不会被另一个更宽的模式吃掉？**

---

## 8. 一句话总结

> **每一条检查背后都有一个真实发生过的故障。**
> 如果你要删掉某一条，先回答：它拦的那个故障现在靠什么防？
> 答不上来就留着 —— 它比一个功能便宜得多。

> **每一条断言背后都应该有一次注入验证。**
> 如果你的断言从没红过，那它很可能**只会绿**。
