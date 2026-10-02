// 验证「异常处理器不再无限递归」的逻辑。
//
// 真实的场景没法在测试里造（要让 WPF 布局真的抛异常），
// 所以这里把那段判定逻辑**照搬过来**，用模拟的异常序列验证它挡不挡得住。

using System;
using System.Collections.Generic;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
}

Console.WriteLine("  ── 场景1：同一个异常重复抛（真实崩溃就是这个）──");
{
    var g = new Guard();
    var ex = new NullReferenceException("Object reference not set to an instance of an object.");
    int popups = 0;
    for (int i = 0; i < 29; i++)   // 实测崩的时候重复了 29 次
        if (g.ShouldPopup(ex)) popups++;

    Check("29 次同一个异常只弹 1 次窗", popups == 1, $"实际 {popups} 次");
    Check("不会无限弹（这是栈溢出的根源）", popups < 29);
}

Console.WriteLine();
Console.WriteLine("  ── 场景2：不同异常轮流抛（第二层熔断）──");
{
    var g = new Guard();
    int popups = 0;
    for (int i = 0; i < 100; i++)
    {
        var ex = new InvalidOperationException($"错误 #{i}");   // 消息每次都不同
        if (g.ShouldPopup(ex)) popups++;
    }
    Check("100 个不同的异常最多弹 3 次", popups == 3, $"实际 {popups} 次");
}

Console.WriteLine();
Console.WriteLine("  ── 场景3：正常的偶发异常还是能弹 ──");
{
    var g = new Guard();
    Check("第一次异常会弹窗", g.ShouldPopup(new Exception("文件读不了")));
    Check("第二次（不同异常）也会弹", g.ShouldPopup(new Exception("网络超时")));
    Check("第三次（又不同）还会弹", g.ShouldPopup(new Exception("权限不够")));
    Check("第四次不弹了（到上限）", !g.ShouldPopup(new Exception("第四个问题")));
}

Console.WriteLine();
Console.WriteLine("  ── 场景4：第一次就重复的极端情况 ──");
{
    var g = new Guard();
    var ex = new Exception("同一个");
    int popups = 0;
    for (int i = 0; i < 1000; i++)
        if (g.ShouldPopup(ex)) popups++;
    Check("1000 次相同异常只弹 1 次", popups == 1, $"实际 {popups}");
}

Console.WriteLine();
Console.WriteLine($"  {(fail == 0 ? "ALL GREEN" : "FAILED")} — {pass} 通过, {fail} 失败");
return fail == 0 ? 0 : 1;

// ── 照搬产品的逻辑 ──────────────────────────────────────────
class Guard
{
    private string? _lastSignature;
    private int _count;
    private const int MAX = 3;

    public bool ShouldPopup(Exception ex)
    {
        var sig = $"{ex.GetType().Name}:{ex.Message}";
        if (sig == _lastSignature) return false;   // 第一层：同一个异常不重复弹
        _lastSignature = sig;

        _count++;
        if (_count > MAX) return false;            // 第二层：熔断
        return true;
    }
}
