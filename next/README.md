# BuddyDeck.Next

全 .NET 重写版。**现有 `WorkBuddyPanel` 与 `workbuddy_api` 保持不动、可继续运行** ——
这个目录是并行开发的替代实现，等它成熟了再切过去。

## 为什么重写

现有那套是把一个 Node 网关（约 11000 行）套了一层 WPF 壳。问题：

- 两个运行时（Node + .NET）→ 打包 230 MB、进程管理复杂、用户机器上要管两套东西
- 界面是手写 XAML，单个 MainWindow.xaml 1733 行，改一处怕碰坏别处
- 没有依赖注入、没有结构化日志、没有指标、没有测试框架

新版本的取舍：**全部 .NET**（网关 = ASP.NET Core，界面 = WPF-UI），
换来单一运行时、单一语言、可测试、可观测。

## 抄了谁

调研了 GitHub 上同类项目（都验证过、星数高），**抄设计不抄代码**
（它们是 Go / Rust / TypeScript，而且许可证各不相同）：

| 项目 | 星 | 抄了什么 |
|---|---|---|
| `jlcodes99/cockpit-tools` | 18.7k | 信息架构（仪表盘 → 列表 → 详情）、配额可视化、多语言做法 |
| `linguo2625469/workbuddy2api-panel` | 2.1k | 账号池调度（加权/熔断/冷却/租约）、会话粘性、请求指标、脱敏日志 |
| `changexbc/workbuddy-switch` | 1.0k | 会话复制与关联、只读在线演示站点的思路 |
| `anthropics/skills` → `frontend-design` | — | 反 AI 模板化的设计方法论 |

## 目录

    src/
      BuddyDeck.Gateway/        网关（ASP.NET Core）
      BuddyDeck.Core/           账号、保险库、上游协议（无 UI 依赖）
      BuddyDeck.App/            桌面界面（WPF-UI）
    tests/
      BuddyDeck.Core.Tests/     单元测试
      BuddyDeck.Gateway.Tests/  网关测试（含假上游）

## 设计原则

1. **不要重复造轮子** —— 能用现成的库就用（MVVM Toolkit / Serilog / xUnit）
2. **抄思路不抄代码** —— 参考项目的许可证各不相同，而且语言不同，抄不了
3. **可测** —— 上游协议用接口隔离，测试里塞假实现
4. **可观测** —— 结构化日志 + 请求指标 + 请求 id 贯穿
5. **不碰现有项目** —— 并行开发，成熟了再切
