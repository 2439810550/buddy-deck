#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
验证「限流历史」能不能被捕获并展示。

为什么不用真机等限流：真限流是**不可控**的（要打满配额，而且会烧用户额度）。
而这条链路的验证点很明确：
  ① 发生限流时，历史被记下来（成功不清）
  ② 汇总能按模型算出来（多少号限过、最晚何时解除）

用真实网关 + 假上游做不到（假上游不会"限流"）。
所以这里用**直接调 Core 的 AccountPool** —— 它是纯逻辑，能构造任意状态。

用法：
  dotnet run --project tools/PoolHealthProbe
（本脚本是它的说明与预期输出，实际由 C# 探针执行）
"""

EXPECTED = """
预期行为（构造 20 个号，其中 18 个对 deepseek-v4.1-flash 限流）：

  /admin/pool-health 里该模型那一行应该是：
    usableCount         = 2      （20 - 18）
    total               = 20
    cooling             = 18 个号名
    rateLimitedAccounts = 18     ← 【关键】成功之后**仍然保留**
    recoveredAccounts   = 0      （还没恢复过）
    lastResetAtMs       = 一个未来的时刻 ← 【关键】"啥时候解除限流"
    coolingNow          = true

  然后对这 18 个号调 MarkSuccess：
    cooling             = 0
    rateLimitedAccounts = **仍然是 18**  ← 这正是要防的"全绿"假象
    recoveredAccounts   = 18
    coolingNow          = false

⇒ 用户要的两条信息都能回答：
   「哪些模型限流了」→ rateLimitedAccounts > 0
   「啥时候解除」     → lastResetAtMs（还在冷却时是未来时刻）
"""

if __name__ == "__main__":
    print(EXPECTED)
