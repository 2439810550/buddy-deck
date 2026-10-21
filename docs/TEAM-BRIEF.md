# 给 AgentTeams 的启动指令（复制进 DSH 对话框）

---

## 推荐的分工方案

创建 4 个 agent。**注意：他们的模型都走同一个 `a123` provider
（也就是 `127.0.0.1:8787` 那个网关），不要一次全开跑满并发。**

### 队长（主会话，不用建）

职责：读交接文档 → 拆任务 → 用任务 DAG 排依赖 → 汇总

### Agent 1：网关端点

```
名字：gateway
首条任务：

在 D:\workfandaiapi\_next 里实现 BuddyDeck.Gateway（ASP.NET Core）。
先读 D:\workfandaiapi\_next\HANDOVER.md 和
D:\workfandaiapi\_next\src\BuddyDeck.Core\ 下的全部代码。

要做的端点（对齐 OpenAI 规范）：
  POST /v1/chat/completions   流式 + 非流式
  GET  /v1/models
  GET  /healthz（必须带 buildId，见 HANDOVER.md 第五节）

网关的职责是「把 Core 里的东西接成 HTTP 服务」，不要重写 Core 的逻辑。
上游调用已经在 UpstreamClient 里了，账号池在 AccountPool 里了。

约束：
· 不要改 D:\workfandaiapi\workbuddy_api（Node 版，在跑）
· 不要改 D:\workfandaiapi\WorkBuddyPanel（在跑）
· 只改 _next 目录
· 流式端点要真流式（ResponseHeadersRead，不要缓冲整个响应体）
· 每加一个端点就写测试（用 tests 里现成的 FakeUpstream）
```

### Agent 2：会话粘性 + 指标 + 脱敏日志

```
名字：observability

依赖：等 Agent 1 的网关骨架出来再开始（任务 DAG 里设 dependsOn）

三个模块：

① 会话粘性（Core/ConversationAffinity.cs）
   多轮对话粘在同一个账号上。上游不发 conversation_id 时，
   用 system + 首条 user 消息的哈希派生会话键（加 d- 前缀）。
   参考实现：D:\workfandaiapi\workbuddy_api\src\gateway.js

② 请求指标（Gateway/Metrics.cs）
   完成成功率 / HTTP 成功率 / 平均耗时 / 最近 N 条请求
   响应带 X-Request-Id

③ 脱敏日志（Gateway/RequestLog.cs）
   **只记元数据**：时间、模型、状态、耗时、账号标签、请求 id
   **绝不记**：提示词、响应正文、token
   这条是硬要求，现有版本的前身因为写全文日志被批评过
```

### Agent 3：WPF-UI 界面

```
名字：ui

依赖：不需要等（可以并行做界面骨架）

用 WPF-UI 4.3.0 + CommunityToolkit.Mvvm 建 BuddyDeck.App。

先读：
  D:\workfandaiapi\_next\HANDOVER.md（里面有 WPF-UI 踩过的坑）
  D:\workfandaiapi\_skills\skills\skills\frontend-design\SKILL.md
    ↑ 这份是反 AI 模板化的设计指南，**做界面之前一定要读**

功能对齐现有 WorkBuddyPanel（D:\workfandaiapi\WorkBuddyPanel，只读参考）：
  账号管理 / 分池 / 聊天测试 / 设置 / 日志

信息架构参考（抄的 GitHub 同类项目）：
  仪表盘（总览）→ 列表（账号池）→ 详情（单个账号）

约束：
· WPF-UI 的枚举名和直觉不同（Badge 用 Caution 不是 Warning），
  写 XAML 前查 ~/.nuget/packages/wpf-ui/*/lib/**/*.xml
· Mica 是半透明的，内容层要自己给不透明背景
· WPF 的 StackPanel 没有 Spacing，用 Margin
· 不要做「奶油底 + 衬线标题 + 陶土色点缀」那套 —— 一眼假
```

### Agent 4：端到端测试

```
名字：e2e

依赖：等 Agent 1 完成

用 tests 里现成的 FakeUpstream 起一个完整网关，跑一遍全部端点。
覆盖：
  · 正常聊天（流式 + 非流式）
  · 上游 429 → 自动换号 → 成功
  · 全部账号冷却 → 返回 429 错误（不能崩）
  · 客户端取消 → 不乱报错
  · /healthz 的 buildId 是稳定的

参考：D:\workfandaiapi\workbuddy_api\scripts\verify.mjs
      （现有版本的真实端到端测试，57 个断言）
```

---

## 关键上下文（贴给所有 agent）

```
项目位置    D:\workfandaiapi\_next
交接文档    D:\workfandaiapi\_next\HANDOVER.md  ← 先读这个
现有参考    D:\workfandaiapi\workbuddy_api\src\  ← Node 版，只读
GitHub 参考 D:\workfandaiapi\_ref\              ← 同类项目，抄设计
设计技能    D:\workfandaiapi\_skills\skills\skills\frontend-design\

⛔ 不要杀 127.0.0.1:8787 上的进程
   那是 pi 的模型来源，也是 DSH 自己的模型来源。杀了全断。

⛔ 不要改 workbuddy_api 和 WorkBuddyPanel（都在跑）
   只在 _next 里干活。

⚠ 你们的模型都走同一个账号池，多开不带来新额度
   遇到 429 是正常的，重试或换模型。别一次开满并发。

Python 用 `python`，不要用 `python3`（那是微软商店的重定向器）
全程中文（思考、注释、提交信息）
```

---

## 建议的任务 DAG

```
  ① 网关端点（Agent 1）
       ↓
  ② 会话粘性+指标+日志（Agent 2） ─┐
       ↓                            │
  ④ 端到端测试（Agent 4）           │ 可以并行
                                    │
  ③ 界面（Agent 3）  ───────────────┘
```

**先让 Agent 1 和 Agent 3 动起来**（它们互不依赖）。
Agent 2 和 4 等 ① 出来再启动 —— 否则它们会去改还不存在的文件。
