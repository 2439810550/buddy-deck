# 往 `BuddyDeck.slnx` 追加测试项目条目

> ✅ **已完成（2026-10-07，t25 收口）**。下面 §1/§2 是历史待办记录，保留作依据。
> **收口结果见文末 §6** —— 含收口前后的实测数字对比。

> 提出者：e2e（t4 交付物之一）。**t4 期间 `BuddyDeck.slnx` 我一行都没动**
> （那条路径在 t4 的 inScope 之外）。
>
> 需要的是：把 §2 那一行加进 `<Solution>` 的 `/tests/` 分组里。
> 加完 `dotnet test`（不带参数、在仓库根目录跑）就会带上这个项目 ——
> **不加的话这些端到端用例在裸 `dotnet test` 里一个都不会跑，而且没有任何警告。**
>
> **执行人：ui（t25，2026-10-07）** —— 队长自己的活，因其正在改 slnx 的路径归属而转派。
> 本文件 §1–§5 是待办与依据，已完成；§6 是结果。

---

## 1. 当前 `BuddyDeck.slnx` 内容

**读于 t4 收尾（2026-10-07，t7 已加入 TestSupport 三项之后）：**

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/BuddyDeck.Core/BuddyDeck.Core.csproj" />
    <Project Path="src/BuddyDeck.Gateway/BuddyDeck.Gateway.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BuddyDeck.Core.Tests/BuddyDeck.Core.Tests.csproj" />
    <Project Path="tests/BuddyDeck.Gateway.Tests/BuddyDeck.Gateway.Tests.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport.Tests/BuddyDeck.TestSupport.Tests.csproj" />
    <Project Path="tests/BuddyDeck.DocSamples.Tests/BuddyDeck.DocSamples.Tests.csproj" />
  </Folder>
</Solution>
```

> ⚠ 这份快照仍可能过时：`tests/` 下还有 `BuddyDeck.App.Presentation.Tests`
> 与 `BuddyDeck.E2E.Tests` 两个项目**尚未进入** slnx。
> 加的时候**以文件当前内容为准**，不要照抄这一段。
>
> **收口安排（队长裁定，2026-10-07）**：队长会在 t2 收口、t8/t11/t9 落定后
> **一次性**补上 App / App.Presentation.Tests / E2E.Tests 三条 ——
> 等等它们全部编译通过再一起加，避免裸 `dotnet build` 中途变红。
> 因此**本文件当前只是一份待办与依据，不是行动指令**；除队长外的成员不要动 slnx。

---

## 2. 要追加的条目

### 2.1 本体（必须）

放进 `<Folder Name="/tests/">`，位置：**`/tests/` 里最后一个条目之后**
（不要插在中间 —— 追加在末尾最不容易和别人的改动手工冲突）：

```xml
  <Folder Name="/tests/">
    ...（现有条目，以文件当前内容为准）...
+   <Project Path="tests/BuddyDeck.E2E.Tests/BuddyDeck.E2E.Tests.csproj" />
  </Folder>
```

**追加后（基于 §1 快照）应该是：**

```xml
  <Folder Name="/tests/">
    <Project Path="tests/BuddyDeck.Core.Tests/BuddyDeck.Core.Tests.csproj" />
    <Project Path="tests/BuddyDeck.Gateway.Tests/BuddyDeck.Gateway.Tests.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport.Tests/BuddyDeck.TestSupport.Tests.csproj" />
    <Project Path="tests/BuddyDeck.DocSamples.Tests/BuddyDeck.DocSamples.Tests.csproj" />
    <Project Path="tests/BuddyDeck.E2E.Tests/BuddyDeck.E2E.Tests.csproj" />
  </Folder>
```

> 队长收口时还会同时补 `BuddyDeck.App`（`/src/` 分组）与
> `BuddyDeck.App.Presentation.Tests`（`/tests/` 分组）两条。

### 2.2 为什么不需要单独加 TestSupport（说明，不是待办）

`BuddyDeck.E2E.Tests` 用 `ProjectReference` 引了
`tests/BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj`。
slnx 里**不必**再为它写一条 —— MSBuild 会顺着 `ProjectReference` 把它拉进来。
（如果 `BuddyDeck.TestSupport` 自己也没进 slnx，那由它的作者决定，与我这条无关。）

---

## 3. 加完之后应该看到什么

```bash
cd D:\workfandaiapi\_next

# ① 这个项目单独跑（不依赖 slnx，任何时候都能跑）
dotnet test tests\BuddyDeck.E2E.Tests

# ② 进了 slnx 之后，全量跑会带上它
dotnet test
```

判定标准：`dotnet test`（不带参数）的输出里出现
`BuddyDeck.E2E.Tests.dll` 那一行以及它的 `已通过/已失败` 统计。

---

## 4. 排期说明

> ⚠ **本节是历史记录（2026-10-07 当时的排期安排）。收口已于同日完成，见 §6。**
> 下面提到的「除队长外不要动 slnx」**已失效** —— 实际由 ui 在 t25 执行。

- 本条目**不阻塞** E2E 测试本身：`dotnet test tests\BuddyDeck.E2E.Tests`
  不经过 slnx 也能跑。所以「等人改 slnx」不影响 t4 的验收。
- **收口方式（当时队长裁定，2026-10-07）**：由队长在
  t2 收口、t8/t11/t9 落定后**一次性**补上
  App / App.Presentation.Tests / E2E.Tests 三条。
  理由：三个项目都要补，且要等它们全部编译通过再一起加，
  否则裸 `dotnet build` 会中途变红。
  ~~在此之前除队长外不要动 `BuddyDeck.slnx`。~~
  → **实际执行人改成了 ui（t25）**：队长本人正在改 slnx 的路径归属，
  转派给 ui；三条于 2026-10-07 一次性补齐，见 §6。
- 之前给 t4 的口径是「测试写在 `tests/BuddyDeck.Gateway.Tests`」，
  后来改成 `tests/BuddyDeck.E2E.Tests`（与 observability 的 t2 分目录），
  队长已裁定以 `BuddyDeck.TestSupport` 的现况为准。
- 如果最后决定把 `BuddyDeck.E2E.Tests` 合并进 `BuddyDeck.Gateway.Tests`
  （或改名），那么**删掉上面那条、保留 Gateway.Tests 那条**即可；
  两个项目里的夹具是共享的（都来自 `BuddyDeck.TestSupport`），合并只涉及
  文件搬家，不涉及行为变更。

---

## 5. 一条容易踩的坑

slnx 是 XML，`Path` 用**正斜杠**、**相对于 slnx 所在目录**。
写成 `tests\BuddyDeck.E2E.Tests\...`（反斜杠）在 Windows 上多数时候也能解析，
但和文件里现有条目的风格不一致 —— 按现有风格写。

---

## 6. 收口结果（t25，2026-10-07，ui）

### 6.1 最终 `BuddyDeck.slnx`

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/BuddyDeck.Core/BuddyDeck.Core.csproj" />
    <Project Path="src/BuddyDeck.Gateway/BuddyDeck.Gateway.csproj" />
    <Project Path="src/BuddyDeck.App/BuddyDeck.App.csproj" />          <!-- 新增 -->
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BuddyDeck.Core.Tests/BuddyDeck.Core.Tests.csproj" />
    <Project Path="tests/BuddyDeck.Gateway.Tests/BuddyDeck.Gateway.Tests.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport/BuddyDeck.TestSupport.csproj" />
    <Project Path="tests/BuddyDeck.TestSupport.Tests/BuddyDeck.TestSupport.Tests.csproj" />
    <Project Path="tests/BuddyDeck.DocSamples.Tests/BuddyDeck.DocSamples.Tests.csproj" />
    <Project Path="tests/BuddyDeck.E2E.Tests/BuddyDeck.E2E.Tests.csproj" />              <!-- 新增 -->
    <Project Path="tests/BuddyDeck.App.Presentation.Tests/BuddyDeck.App.Presentation.Tests.csproj" />  <!-- 新增 -->
  </Folder>
</Solution>
```

项目数 **7 → 10**（`dotnet sln BuddyDeck.slnx list` 实测）。

### 6.2 收口前后对比（核心证据）

| | 收口前 | 收口后 |
|---|---|---|
| 裸 `dotnet test` 跑到的项目数 | **4** | **6** |
| 裸 `dotnet test` 跑到的用例数 | **696** | **1161** |
| `dotnet sln list` 项目数 | 7 | 10 |

裸 `dotnet test` 逐项目统计（收口后实测）：

```
BuddyDeck.App.Presentation.Tests   206
BuddyDeck.Core.Tests               390
BuddyDeck.Gateway.Tests            377
BuddyDeck.E2E.Tests                129
BuddyDeck.TestSupport.Tests         53
BuddyDeck.DocSamples.Tests          15
                                   ───
                                  1161
```

> ⚠ **这是一次测量快照，不是长期常量。** 用例总数随队友持续新增而变化
> （收口当天稍晚再测已是 1180，再往后又会不同）。
> **要看当前值就自己跑一遍** `dotnet test`，不要引用本表里的数字当真值。
> 本表的价值只在**那一次前后对比**：它证明「新进 slnx 的两个项目确实被执行了」。
> 判定收口是否仍然有效，只需看**项目数**（应为 6 个测试项目全部出现），
> 这个数字不随用例增减而变。

**新进 slnx 且此前在裸命令下完全不可见的**：
- `BuddyDeck.E2E.Tests` —— 129 个用例
- `BuddyDeck.App.Presentation.Tests` —— 206 个用例

### 6.3 为什么把 `BuddyDeck.App`（WinExe + WPF）也加进 /src/

它此前不在 slnx 里，于是 `dotnet build`（不带路径）**不编译界面** ——
界面代码坏了不会有任何人发现，直到有人单独去编它。**这正是本任务要消灭的那类静默漏掉。**

**风险评估**：加它确实引入一个限制 —— 在没有 WPF workload 的环境（非 Windows、或未装
`Microsoft.WindowsDesktop.App`）里，全解决方案构架会失败。

**判断依据**：
- 本项目的**唯一部署目标是 Windows 桌面**（HANDOVER 明确写 WPF-UI / FluentWindow / Mica），
  没有跨平台需求；
- 当前构建机是 Windows 11（10.0.22631）+ .NET SDK 10.0.103，`dotnet build` 实测 0 错 0 警；
- 权衡：**"界面坏了没人发现"是已经发生过多次的真实损失**（t3 就因此被误判 failed），
  而"非 Windows 环境构建失败"在当前团队不会发生。

**如果将来真有非 Windows CI**：把这一条从 slnx 移出即可（它已是独立条目，取舍成本一行）。

