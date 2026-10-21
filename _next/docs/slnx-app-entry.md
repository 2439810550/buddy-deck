# 需要追加到 BuddyDeck.slnx 的条目

> ✅ **已完成（t25 收口，2026-10-07）—— 见文末 §8 的实际结果与前后对比。**
> 下面 §1–§7 是当时的待办与依据，保留作记录。

> 作者：ui（t3）。**t3 时我没有改 `BuddyDeck.slnx`** —— 那个文件当时归 gateway（t1）负责。
> 这份文档描述「需要追加什么、追加在哪」。
> **最终由 ui 在 t25 收口时执行**（队长转派，因其正在改 slnx 的路径归属）。

---

## 1. 现状

`D:\workfandaiapi\_next\BuddyDeck.slnx` 当前的内容：

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/BuddyDeck.Core/BuddyDeck.Core.csproj" />
    <Project Path="src/BuddyDeck.Gateway/BuddyDeck.Gateway.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BuddyDeck.Core.Tests/BuddyDeck.Core.Tests.csproj" />
  </Folder>
</Solution>
```

## 2. 需要追加的那一段

在 `<Folder Name="/src/">` 里面、**`BuddyDeck.Gateway` 那一行之后**追加一行：

```xml
    <Project Path="src/BuddyDeck.App/BuddyDeck.App.csproj" />
```

## 3. 改完后 `BuddyDeck.slnx` 应该是

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/BuddyDeck.Core/BuddyDeck.Core.csproj" />
    <Project Path="src/BuddyDeck.Gateway/BuddyDeck.Gateway.csproj" />
    <Project Path="src/BuddyDeck.App/BuddyDeck.App.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BuddyDeck.Core.Tests/BuddyDeck.Core.Tests.csproj" />
  </Folder>
</Solution>
```

## 4. 为什么插入位置是「Gateway 之后」

`.slnx` 里的顺序不影响构建，只影响 IDE 里解决方案树的排列。

`/src/` 下按**依赖方向**排：`Core`（无依赖）→ `Gateway`（依赖 Core）→
`App`（依赖 Core）。这个顺序和人读代码的顺序一致：先读被依赖的，再读依赖它的。
把 App 插在 Core 前面也能编译，但文件顺序会和依赖方向相反。

## 5. 追加之后的验证

```bash
cd D:\workfandaiapi\_next
dotnet build            # 应该把 BuddyDeck.App 一起编了
```

想确认 App 真的进了解决方案（而不是只是碰巧编译过）：

```bash
cd D:\workfandaiapi\_next
dotnet sln BuddyDeck.slnx list
```

## 6. ⚠ 一并说明：`BuddyDeck.App.Presentation.Tests` 也不在 slnx 里

`tests/BuddyDeck.App.Presentation.Tests/` 是队友 design（t6）建的纯逻辑测试项目，
**它同样没进 `BuddyDeck.slnx`**（design 不动那个文件）。

如果你（改 slnx 的人）想让它一起被 `dotnet test` 跑到，那么在
`<Folder Name="/tests/">` 里、`BuddyDeck.Core.Tests` 之后追加：

```xml
    <Project Path="tests/BuddyDeck.App.Presentation.Tests/BuddyDeck.App.Presentation.Tests.csproj" />
```

**这条不是我这个任务的要求**，只是把情况说清楚，免得改 slnx 的人以为漏了。
在它进 slnx 之前，跑它的方式是指定项目路径：

```bash
dotnet test tests\BuddyDeck.App.Presentation.Tests\BuddyDeck.App.Presentation.Tests.csproj
```

---

## 7. 临时编译（改 slnx 之前也能验）

不想等 slnx 改动的话，App 可以单独编译和运行：

```bash
cd D:\workfandaiapi\_next
dotnet build src\BuddyDeck.App\BuddyDeck.App.csproj
dotnet run   --project src\BuddyDeck.App
```

App 项目自己带 ProjectReference 指向 `src/BuddyDeck.Core`，不依赖解决方案文件。

---

## 8. 收口结果（t25，2026-10-07，ui）

### 8.1 实际追加的条目

`<Folder Name="/src/">` 里、`BuddyDeck.Gateway` 之后：

```xml
    <Project Path="src/BuddyDeck.App/BuddyDeck.App.csproj" />
```

与本文 §2 写的一致。同一次收口还补了 `/tests/` 的两条
（`BuddyDeck.E2E.Tests`、`BuddyDeck.App.Presentation.Tests`），
详见 `docs/slnx-tests-entry.md` §6。

**项目数 7 → 10**（`dotnet sln BuddyDeck.slnx list` 实测）。

### 8.2 `BuddyDeck.App` 为什么最终决定加（回答本文 §2 的取舍）

**加。判断依据：**

1. **不加的代价是已经发生过的真实损失**：它不在 slnx 里时，裸 `dotnet build` **不编译界面** ——
   界面代码坏了没有任何人会知道，直到有人单独去编它。这正是本任务要消灭的那类静默漏掉。
2. **风险只在非 Windows 环境成立**：它是 `net8.0-windows` + `UseWPF`。
   而本项目的唯一部署目标就是 Windows 桌面（HANDOVER 写明 WPF-UI / FluentWindow / Mica），
   没有跨平台需求；当前构建机是 Windows 11 + .NET SDK 10.0.103，实测 `dotnet build` 0 错 0 警。
3. **取舍成本很低**：它是一条独立条目，将来若真有非 Windows CI，删掉这一行即可。

### 8.3 验证

```bash
cd D:\workfandaiapi\_next
dotnet build                    # 已成功生成。0 个警告 0 个错误
dotnet test                     # 6 个项目 / 1161 个用例（收口前 4 个 / 696 个）
```

> ⚠ **用例数是测量快照，不是常量**（会随队友持续新增而变化）。
> 判定收口是否仍有效，看**项目数**（6 个测试项目全部出现），那个数字不随用例增减而变。

---

## 9. 同轮勘误登记：4 个 Pages 文件（t43 补登记）

t25 收口那一轮，船长额外指派了一件不在 t25 inScope 里的事 ——
**删掉页面里为绕过 `StatusRail` 缺陷而临时加的 `Height="220"`**。
执行时又发现 `AccountsPage` 带着**同一个绑定 bug**，一并修了。
因为 `Pages/` 不在 t25 的 inScope 里，当时无法在 `changedPaths` 登记；
**此处补正式登记（代码在 t25 那一轮已改完并编译验证）**。

### 9.1 改了什么

| 文件 | 改动 |
|---|---|
| `Pages/DashboardPage.xaml` | ① 删临时 `Height="220"`（根因已由 design 修好，留着会掩盖「控件会自己测量」）② 绑定 `RelativeSource=Window` → `ElementName=PageRoot` ③ 根元素加 `x:Name="PageRoot"` ④ 手搓的 `ItemsControl` 换成 `bd:StatusRail` |
| `Pages/DashboardPage.xaml.cs` | 暴露 `public ShellViewModel Shell { get; }` |
| `Pages/AccountsPage.xaml` | ① 删临时 `Height="220"` ② 同样的 `RelativeSource=Window` → `ElementName=PageRoot` ③ 根元素加 `x:Name="PageRoot"` |
| `Pages/AccountsPage.xaml.cs` | 暴露 `public ShellViewModel Shell { get; }` |

### 9.2 为什么必须改绑定（不是顺手重构）

原写法 `{Binding StatusRail, RelativeSource={RelativeSource AncestorType=Window}}`
**静默失败**：`ContentHost` 是 `ContentControl`，换内容时页面的可视树父链会**断开重建**，
`RelativeSource` 向上找不到 `Window` → 绑定不生效，**不报错、不抛异常、就是空着**。

表现极具迷惑性：状态轨一条竖条都没有、底部数字为空，只剩「可用」「个号」两个标签竖排 ——
**看起来像布局错位，实际是绑定没接上**。当时照那个假象去查 DockPanel / 宽度 / 控件测量，
**三轮方向全错**。最后靠**对照实验**（顶栏 `{Binding StatusLine}` 工作正常 →
说明 `DataContext` 是好的，问题只出在 `RelativeSource` 那条路）才把方向掰回来。

**修法**：页面暴露一个具名属性 `Shell`，XAML 用 `{Binding Shell.X, ElementName=PageRoot}`。
不依赖视觉树形状，页面放到哪里都成立。修完数字立即出现（5 可用 / 6 个号）。

> **沉淀成团队规则**（船长已采纳）：
> **WPF 绑定失败的表征和布局问题长得一模一样。
> 判断「是绑定还是布局」应该先做对照实验，而不是从外观猜。**

### 9.3 相关的控件缺陷（另一个问题，已由 design 修）

同一次排障还发现 `bd:StatusRail` **完全没有 `MeasureOverride`** ——
`FrameworkElement` 不给测量逻辑时 WPF 把它量成 **0×0**，`OnRender` 里
`if (height <= 0) return;` 直接退出，于是**永远不画**。

**⚠ 这和 §9.2 的绑定 bug 是两回事**，当时把它们混在一起查，浪费了时间。
我那两处临时 `Height="220"` 就是为这个加的；根因（`MeasureOverride`）修好后已删除。

design 的修复还带出一个值得记的发现：**用 `Border` 当宿主时那条渲染断言是"假绿"**
（`Arrange` 会给尺寸，对 `Measure` 缺陷不敏感），换 `StackPanel` 才有判别力，并做了注入验证。
**「测试通过」和「测试在测对的东西」是两件事。**

### 9.4 验证

```bash
cd D:\workfandaiapi\_next
dotnet build src\BuddyDeck.App\BuddyDeck.App.csproj   # 0 个警告 0 个错误
```

**另外核对过**：两页已无 `RelativeSource={RelativeSource AncestorType=Window}` 残留；
两个 code-behind 都有 `public ShellViewModel Shell`。

**未做**：视觉复验（当时为查此问题临时搭的假网关已清理，重建成本不值当）。

