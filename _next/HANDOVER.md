# 交接文档 —— 给 DSH AgentTeams

> 写于 2026-10-07。这份文档的读者是接手 `_next` 项目的智能体团队。
> 目标：让你不用重新摸索，接着往下干。

---

## 一、这个项目是什么

把 `WorkBuddyPanel` + `workbuddy_api`（现有那套 Node + WPF 的东西）**全 .NET 重写**。

### 为什么重写

现有那套的问题：

```
① 两个运行时（Node 网关 + .NET 界面）
   → 打包 230 MB，用户机器上要管两套进程
② 界面是手写 XAML，单文件 MainWindow.xaml 1733 行
③ 没有依赖注入、没有结构化日志、没有指标、没有测试框架
```

### 重写后的样子

```
网关   ASP.NET Core
界面   WPF-UI 4.3.0（Fluent Design）
架构   CommunityToolkit.Mvvm + Microsoft.Extensions.DI
日志   Serilog
测试   xUnit（不碰网络，用假上游）
```

### ⚠ 最重要的一条规矩

**现有项目保持不动、继续可用。** 所以：

```
D:\workfandaiapi\workbuddy_api\       ← 不要改（Node 网关，在跑）
D:\workfandaiapi\WorkBuddyPanel\      ← 不要改（WPF 界面，在跑）
D:\workfandaiapi\_next\               ← 只有这里是你的战场
```

改动 `workbuddy_api` 会让**当前正在运行的网关**行为变化，那个网关同时是
AI 助手（pi）的模型来源 —— 杀了它助手当场断线。

---

## 二、已经做完的

### 目录结构

```
_next/
  BuddyDeck.sln
  Directory.Build.props          统一编译设置（Nullable、分析器、警告即错误）
  src/
    BuddyDeck.Core/              无 UI 依赖的核心
      Variants.cs                国内版/国际版的域名与常量
      Credential.cs              凭证模型 + 过期时间归一
      AuthDocument.cs            解析桌面 App 写的登录文件
      AuthEnvelope.cs            5.6+ 加密凭据外壳的识别
      Vault.cs                   保险库读写（原子写 + 权限收紧）
      UpstreamProtocol.cs        请求头族 + 请求体改写管线
      UpstreamClient.cs          上游 HTTP 客户端 + 错误分类
      AccountPool.cs             账号池（选号/冷却/冷冻/负载均衡）
    BuddyDeck.Gateway/           ASP.NET Core 网关（**只有壳，待填**）
    BuddyDeck.App/               WPF-UI 界面（**还没建**）
  tests/
    BuddyDeck.Core.Tests/        110 个测试，全绿
```

### 测试现状

```
110 个测试，全部通过，跑了 91 ms
不碰网络、不碰真实账号（用 FakeUpstream 假处理器）
```

---

## 三、关键设计决策（别推翻，除非有充分理由）

### 1. 上游协议的所有常量都在 `Variants.cs`

```
国内版   chatBase=https://copilot.tencent.com
         billingBase=https://www.codebuddy.cn
国际版   chatBase=https://www.workbuddy.ai
         billingBase=https://www.workbuddy.ai
```

**别把这些散到各处** —— 国际版没实测过（开发机只装了国内版），
要验证时只需要改这一个文件。

### 2. 「空身份」必须显式声明

上游的请求头里有这么一组：

```
X-User-Id / X-No-User-Id
X-Enterprise-Id / X-No-Enterprise-Id
X-Domain / X-No-Department-Info
```

**「缺 X-User-Id」和「给 X-No-User-Id: 1」是两回事。** 后者才是正确的
「我没有这个字段」。省略了会被上游当成异常请求，表现是偶发 403。

见 `UpstreamHeaders.ApplyIdentity`。

### 3. `X-IDE-*` 归属族只能出现在 chat 上

```
chat      → 带 X-IDE-Type / X-IDE-Name / X-IDE-Version / X-Product
刷新 token → **不带**（只带 X-Refresh-Token）
拉目录    → **不带**
计费      → **不带**
```

上游按这些头做用量归属，加错地方会把账算乱。

### 4. 冷却是按模型分的

上游 429 有两种：整个账号被限 / 只有某个模型被限。后者很常见。

**不分模型的话，一个模型被限就把整个号拉黑 —— 白扔配额。**

`429 + 正文含 "6004"` = 模型级限流（换模型能解决）。

### 5. 保险库的文件格式是历史包袱，不能改

```jsonc
{
  "version": 1,
  "activeId": "uuid:enterpriseId",     // ← 末尾那个冒号是格式的一部分
  "accounts": [{
    "id": "uuid:enterpriseId",
    "uid": "...", "enterpriseId": "...", "domain": "...",
    "accessToken": "...", "refreshToken": "...",
    "expiresAtMs": 1793731164678,       // ← 数字（Unix 毫秒）
    "refreshExpiresAtMs": 1793903964678,// ← 数字
    "addedAt": "2026-09-24T09:35:12.642Z",   // ← ⚠ ISO 字符串！
    "lastSeenAt": "2026-10-06T18:39:24.680Z",
    "lastCheckinAt": "2026-10-06T18:39:26.706Z"
  }]
}
```

**注意那两个类型不一致**：`expiresAtMs` 是数字，`addedAt` 是 ISO 字符串。
（我猜错了一次，见下面的「踩过的坑」）

### 6. 原子写保险库

这个文件里是**明文 token**，丢了等于用户的全部账号没了。所以：

```csharp
File.WriteAllText(temp, json);       // 先写临时文件
File.Move(temp, path, overwrite: true);  // 原子替换
```

不要改成直接覆盖。

---

## 四、踩过的坑（血泪，别重犯）

### 坑 1：加密凭据的格式

**我以为的**：

```jsonc
{ "suite": 1, "keyId": "…16位hex…", "payload": "…" }    // ← 错的
```

**真实的**：

```jsonc
{
  "accessToken": {
    "$wbEncrypted": 1,
    "envelope": "<base64>"
  }
}
// 把 envelope 解开才看到 {suite, keyId, nonce, authTag, ciphertext}
```

`nonce` 必须 12 字节、`authTag` 必须 16 字节、`keyId` 必须 16 位小写 hex。

**教训：涉及外部格式的东西，对着实现写，不要对着记忆写。**
单元测试当场抓到了（110 个测试挂了 1 个）。

### 坑 2：三个时间字段的类型

```
expiresAtMs        int     1793731164678          ← 数字
addedAt            str     "2026-09-24T09:35:12.642Z"   ← ISO 字符串
lastSeenAt         str     "2026-10-06T18:39:24.680Z"
lastCheckinAt      str     "2026-10-06T18:39:26.706Z"
```

我按数字读的，读真实保险库直接抛 JsonException。

**这个 bug 造数据永远发现不了**（我造的全是数字）。是
「读真实保险库文件」那条测试抓的（`VaultTests.能读真实的旧版保险库`）。

**所以那条测试留着 —— 它比任何手写用例都值钱。**

### 坑 3：WPF-UI 的枚举名和你想的不一样

```
Badge.Appearance    没有 Warning，是 Caution
ControlAppearance   Primary, Secondary, Info, Dark, Light, Danger, Success, Caution, Transparent
InfoBarSeverity     Informational, Success, Warning, Error
WindowBackdropType  None, Auto, Mica, Acrylic, Tabbed
```

写 XAML 前查 `~/.nuget/packages/wpf-ui/*/lib/**/*.xml` 里的枚举定义。

### 坑 4：Mica 材质是半透明的

`WindowBackdropType="Mica"` 会让内容层透出背后的东西。
内容层要自己给不透明背景（`Background="{DynamicResource ApplicationBackgroundBrush}"`）。

### 坑 5：WPF 的 StackPanel 没有 Spacing

那是 Avalonia 的。WPF 得用 `Margin`。

### 坑 6：分析器会拦日志的参数计算（CA1873）

```csharp
// 会被拦：m.Label 是计算属性，日志关掉时白算了
LogCooldown(m.Label, model, DateTimeOffset.FromUnixTimeMilliseconds(until));

// 正确：先判级别，再算
if (_log.IsEnabled(LogLevel.Information))
{
    var label = m.Label;
    var minutesLeft = (until - nowMs) / 60_000;
    LogCooldownMinutes(label, model, (int)minutesLeft);
}
```

而且要在**局部变量里算好**再传 —— 实参位置的任何计算都会被拦。

---

## 五、下一步该干什么（按优先级）

### P0：`BuddyDeck.Gateway` 的端点（最关键的链路）

现在 `BuddyDeck.Gateway` 只有一个空 csproj，**缺入口点，编译不过**。

要实现的端点（对齐 OpenAI 规范，这样现有客户端零改造接入）：

```
POST /v1/chat/completions       聊天（流式 + 非流式）
GET  /v1/models                 模型列表
POST /v1/images/generations     生图（可选，看是否移植 Miora）
GET  /healthz                   健康检查（**必须带 buildId**，见下）
```

**`/healthz` 必须回一个代码指纹**（现有版本的做法，用户依赖它）：

```json
{
  "ok": true,
  "provider": "workbuddy",
  "models": 17,
  "buildId": "aec74cebbc60",       // 这个进程启动时加载的代码的指纹
  "startedAt": "2026-10-06T15:51:38.232Z",
  "stale": false                   // 磁盘上的代码和正在跑的是否一致
}
```

为什么重要：用户遇到「改了代码但功能没变」时，靠这个判断
「端口上那个网关是不是旧代码」。现有版本有这个，别丢。

### P1：会话粘性

多轮对话必须粘在同一个账号上（不然上下文会跳号）。

上游不发 `conversation_id` 时，用 `system + 首条 user 消息` 的哈希
派生一个会话键（旧版网关的做法，加 `d-` 前缀）。

### P2：请求指标 + 脱敏日志

```
指标：完成成功率 / HTTP 成功率 / 平均耗时 / 最近请求
日志：**只记元数据**，不写提示词、响应正文、凭证
```

脱敏这条是硬要求 —— 现有版本的前身因为写全文日志被批评过。

### P3：WPF 界面

`BuddyDeck.App` 还没建。用 WPF-UI 4.3.0，参考现有 `WorkBuddyPanel` 的功能：

```
账号管理（导入/切号/签到/续期/重命名/删除）
分池（一套账号一个接入地址）
聊天测试
设置（端口/Node 路径/API Key）
日志查看
```

**信息架构参考**（抄的 GitHub 同类项目）：

```
仪表盘（总览）→ 列表（账号池/池）→ 详情（单个账号）
```

### P4：端到端测试

用 `FakeUpstream` 起一个完整的网关，跑一遍全部端点。

---

## 六、可参考的资源

### 现有项目（**只读，别改**）

```
D:\workfandaiapi\workbuddy_api\src\      Node 网关，约 11000 行
   upstream.js      上游协议（请求头、请求体改写、模型目录）
   gateway.js       HTTP 端点
   auth.js          凭证读取
   miora.js         生图/生视频
   ratelimit.js     限流追踪
   usage.js         用量账本

D:\workfandaiapi\WorkBuddyPanel\          WPF 界面
   ViewModels/      MVVM 逻辑
   Services/        业务服务
```

**移植时对着这些读，它们是目前唯一被真实账号验证过的实现。**

### GitHub 同类项目（抄设计不抄代码）

已经 clone 到 `D:\workfandaiapi\_ref\`：

```
jlcodes99_cockpit-tools            18.7k★  Tauri+React
  抄：信息架构（仪表盘→列表→详情）、配额可视化、18 语言 i18n 做法

linguo2625469_workbuddy2api-panel   2.1k★  Go
  抄：账号池调度（加权/熔断/冷却/租约）、会话粘性、请求指标、脱敏日志
      ★ 这个和我们的方向最接近，值得通读 README

changexbc_workbuddy-switch          1.0k★  Tauri+Rust
  抄：会话复制与关联、只读在线演示站点的思路
```

### 设计方法论

```
D:\workfandaiapi\_skills\skills\skills\frontend-design\SKILL.md
```

**这份值得读** —— 它是「反 AI 模板化」的设计指南，列了 AI 生成设计的
五个识别特征（奶油底+衬线标题、近黑底+酸绿点缀、一刀切圆角卡片套装……）。
做界面之前读一遍，避免做出来的东西一眼假。

---

## 七、环境与命令

```
.NET SDK    10.0.103（项目 target net8.0）
Node        D:\nodejs\node.exe  v24.14.0
Python      **必须用 `python`**，不要用 `python3`
            （python3 在这台机器上是微软商店的重定向器，会弹商店）
```

### 常用命令

```bash
cd D:/workfandaiapi/_next

dotnet build                       # 编译全部
dotnet test                        # 跑全部测试
dotnet test tests/BuddyDeck.Core.Tests    # 只跑 Core 测试
```

### ⚠ 运行环境的两条铁律

**1. 不要杀 `127.0.0.1:8787` 上的进程**

那是 pi（AI 助手）的模型来源。杀了它，当前会话当场断线。
而且 —— **DSH 自己的模型也走这个网关**（`a123` provider 指向它）。
杀了它 DSH 团队一起断。

查：

```bash
netstat -ano | grep ":8787.*LISTENING"
curl -s http://127.0.0.1:8787/healthz
```

**2. DSH Web 在 `127.0.0.1:3080`**

那是你自己跑的地方。别重启它。

---

## 八、当前 DSH 团队的模型来源（重要）

```
DSH 的 agent  →  a123 provider  →  A123_API_KEY=sk-workbuddy-local
                                →  http://127.0.0.1:8787/v1
                                →  同一个 WorkBuddy 账号池
```

### 这意味着什么

**多开 agent 不会带来新额度。** 所有 agent 都在烧同一个池子的配额。

而且 **多 agent 并发会更容易触发上游限流**。现有网关就踩过这个坑：
三个模型同时被限流 → 全部进冷却 → 返回 undefined → TypeError。

### 所以

```
· 别一次性起太多并发 agent
· 遇到 429 是正常的，重试或者换模型
· 真正需要并发时，考虑用不同的池（用户有默认池 + 志强池）
```

---

## 九、用户偏好（别踩雷）

```
· 全程中文（包括思考、注释、提交信息）
· 零第三方依赖是现有网关的设计承诺 —— 新版可以放宽，但要知道代价
· 不要写「注册机」批量注册账号
· 不要给接码平台导流
· 做完一步 → 验证 → 提交（改一步验证一步）
· 不要硬等（Start-Sleep）—— 测试要自己设置状态、可重复
· 先写失败测试再修 bug
```

### 用户的测试方法论（他明确要求过）

```
① 先写会失败的测试，再修 bug
② 不要休眠，测试要快
③ 测试自己设置状态，独立可重复
④ 组合/边界测试
```

---

## 十、现状速查

```
测试        110/110 全绿，91 ms
编译        Core 通过；Gateway 缺入口点（P0 要补）
未提交      全部未提交（等成形了再提交）
代码位置    D:\workfandaiapi\_next
参考项目    D:\workfandaiapi\_ref
设计技能    D:\workfandaiapi\_skills
```

### 一句话总结现状

**核心逻辑（凭证/协议/池）已经写完并测透了，剩下的是把它接成
一个能跑的 HTTP 服务，再包一层界面。**
