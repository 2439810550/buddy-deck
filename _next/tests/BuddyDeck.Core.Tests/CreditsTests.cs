using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 积分查询解析层的测试（t57，对齐旧版 `upstream.js:750` `fetchCredits`）。
///
/// ## 回归输入来自**真机实测**（2026-10-07）
///
/// 下面所有标了「真机」的常量都是照着真实响应逐字抄的，不是编的。
/// 实测那个账号当时的状态：
/// ```
/// TotalCount = 28, TotalDosage = 3200
/// 28 个包：8 个带 grantSource="daily_checkin"（剩余合计 800）
///          20 个不带（剩余合计 2400）
/// ```
///
/// ⚠ 任务书写的是「16 个包、13 个 daily_checkin」—— 那是 captain 更早一次实测的数字。
/// 我这次跑出来是 **28 / 8**，账号状态在两次之间变了（签到一直发新的包）。
/// 两个数字都真实，**但回归夹具必须用我实际拿到的那份**，否则断言的是我没见过的数据。
/// </summary>
public class CreditsTests
{
    // ══ 真机原文片段（逐字） ═════════════════════════════════════

    /// <summary>
    /// 真机原文的一个**带 grantSource** 的包（`Accounts[20]`）。
    /// 关键：`grantSource` 在 `AccountAttributes` 里，且是**第 0 项**
    /// （这个包里它恰好排第一）。
    /// </summary>
    private static JsonObject RealPackageWithGrantSource() => new()
    {
        ["AccountId"] = 238416403,
        ["Uin"] = "330111699997",
        ["AppId"] = 1467607979,
        ["CapacityUnit"] = "credits",
        ["CycleStartTime"] = "2026-09-30 08:26:16",
        ["CycleEndTime"] = "2026-10-31 08:26:15",
        ["PackageCode"] = "TCACA_code_007_nzdH5h4Nl0",
        ["PackageName"] = "CodeBuddy个人版国内运营裂变包",
        ["ProductName"] = "腾讯云代码助手",
        ["SubProductName"] = "腾讯云代码助手 (IDE) - 赠送包",
        ["CapacityRemain"] = 100,
        ["CapacityUsed"] = 0,
        ["CapacitySize"] = 100,
        ["CycleCapacityRemain"] = 100,
        ["CycleCapacitySize"] = 100,
        ["Status"] = 0,
        ["CapacitySizePrecise"] = "100",
        ["CapacityRemainPrecise"] = "100",
        ["CapacityUsedPrecise"] = "0",
        ["CycleCapacitySizePrecise"] = "100",
        ["CycleCapacityRemainPrecise"] = "100",
        ["CycleCapacityUsedPrecise"] = "0",
        ["AccountAttributes"] = new JsonArray(
            new JsonObject { ["Key"] = "grantSource", ["Value"] = "daily_checkin", ["Type"] = 5 }),
    };

    /// <summary>
    /// 真机原文的一个**不带 grantSource** 的包（`Accounts[0]`）。
    /// ⚠ 它的 `AccountAttributes` 里第 0 项是 `payerType`、第 1 项是 `payerUin`
    /// —— **没有 grantSource**。按位置取属性的实现会在这里读成 "0"。
    /// </summary>
    private static JsonObject RealPackageWithoutGrantSource() => new()
    {
        ["AccountId"] = 100561034,
        ["CapacityUnit"] = "credits",
        ["CycleStartTime"] = "2026-10-01 00:00:00",
        ["CycleEndTime"] = "2026-10-31 23:59:59",
        ["PackageCode"] = "TCACA_code_008_cfWoLwvjU4",
        ["PackageName"] = "CodeBuddy个人版包",
        ["ProductName"] = "腾讯云代码助手",
        ["CapacityRemain"] = 500,
        ["CapacityUsed"] = 0,
        ["CapacitySize"] = 500,
        ["Status"] = 0,
        ["CapacitySizePrecise"] = "500",
        ["CapacityRemainPrecise"] = "500",
        ["CapacityUsedPrecise"] = "0",
        // ⚠ 真机顺序就是 payerType / payerUin —— 没有 grantSource
        ["AccountAttributes"] = new JsonArray(
            new JsonObject { ["Key"] = "payerType", ["Value"] = "0", ["Type"] = 5 },
            new JsonObject { ["Key"] = "payerUin", ["Value"] = "330111699997", ["Type"] = 5 }),
    };

    /// <summary>
    /// 把一组包包成真机的三层嵌套信封。
    ///
    /// ⚠ 传进来的 <see cref="JsonObject"/> 会被**深拷贝**一次再放进数组。
    /// System.Text.Json 的节点**只能有一个父节点** —— 直接塞入会让第二次
    /// 使用同一个对象时抛 "node already has a parent"。
    /// 测试里写出 `Envelope([p])` 两次是很自然的事，所以这里直接兜住，
    /// 而不是要求每个用例自己记住这条规则。
    /// </summary>
    private static JsonObject Envelope(IEnumerable<JsonObject> packages, int? totalCount = null, double totalDosage = 0)
    {
        var array = new JsonArray([.. packages.Select(p => (JsonNode)p.DeepClone())]);
        var list = array.Count;

        return EnvelopeOf(array, totalCount ?? list, totalDosage);
    }

    /// <summary>用一个**已经造好的**数组包信封（要往数组里混脏元素时用这个）。</summary>
    private static JsonObject EnvelopeOf(JsonArray accounts, int? totalCount = null, double totalDosage = 0)
        => new()
        {
            ["code"] = 0,
            ["msg"] = "OK",
            ["requestId"] = "e4c0a5d0-0000-0000-0000-000000000000",
            ["data"] = new JsonObject
            {
                // ⚠ 真机这里多一层 Response（别的接口没有）
                ["Response"] = new JsonObject
                {
                    ["Data"] = new JsonObject
                    {
                        ["TotalCount"] = totalCount ?? accounts.Count,
                        ["TotalDosage"] = totalDosage,
                        ["Accounts"] = accounts,
                    },
                    ["RequestId"] = "inner-req",
                },
            },
        };

    // ══ ① 真机原文：单包逐字段 ═══════════════════════════════════

    [Fact]
    public void 真机_带签到标记的包被完整读出()
    {
        var snapshot = Credits.Parse(Envelope([RealPackageWithGrantSource()]));

        var package = Assert.Single(snapshot.Packages);

        Assert.Equal("CodeBuddy个人版国内运营裂变包", package.PackageName);
        Assert.Equal("TCACA_code_007_nzdH5h4Nl0", package.PackageCode);
        Assert.Equal("腾讯云代码助手", package.ProductName);
        Assert.Equal("credits", package.CapacityUnit);

        // 数值走精确字段
        Assert.Equal(100, package.Size);
        Assert.Equal(100, package.Remain);
        Assert.Equal(0, package.Used);
        Assert.Equal(100, package.CycleSize);
        Assert.Equal(100, package.CycleRemain);

        // 周期是上游原样字符串（空格分隔、无时区）
        Assert.Equal("2026-09-30 08:26:16", package.CycleStartTime);
        Assert.Equal("2026-10-31 08:26:15", package.CycleEndTime);

        Assert.Equal(0, package.Status);

        // ★ grantSource 从 AccountAttributes 里读出来
        Assert.Equal("daily_checkin", package.GrantSource);
        Assert.True(package.IsDailyCheckin);
    }

    [Fact]
    public void 真机_不带签到标记的包_GrantSource_为空()
    {
        var snapshot = Credits.Parse(Envelope([RealPackageWithoutGrantSource()]));

        var package = Assert.Single(snapshot.Packages);

        Assert.Equal("CodeBuddy个人版包", package.PackageName);
        Assert.Equal(500, package.Remain);
        Assert.Equal("", package.GrantSource);
        Assert.False(package.IsDailyCheckin);
    }

    [Fact]
    public void 真机_grantSource_按_Key_找而不是按位置()
    {
        // ⚠⚠ 这条钉的是一个**真机上会读错**的实现 ⚠⚠
        //
        // 真机里 grantSource 混在 payerType / payerUin 之间，**位置不固定**：
        //   · Accounts[0]  → [payerType, payerUin]            （没有 grantSource）
        //   · Accounts[20] → [grantSource, payerType, payerUin]
        //
        // 按位置取（比如「看第 0 项」或「看最后一项」）的实现：
        //   在有 grantSource 的包上碰巧可能对，在**没有的包上会读到 payerType 的 "0"**
        //   —— 那会显示成"来源是 0"，而不是"没有来源"。
        var shifted = RealPackageWithGrantSource();
        shifted["AccountAttributes"] = new JsonArray(
            new JsonObject { ["Key"] = "payerType", ["Value"] = "0", ["Type"] = 5 },
            new JsonObject { ["Key"] = "payerUin", ["Value"] = "330111699997", ["Type"] = 5 },
            new JsonObject { ["Key"] = "grantSource", ["Value"] = "daily_checkin", ["Type"] = 5 });

        var package = Assert.Single(Credits.Parse(Envelope([shifted])).Packages);

        // 换到第 2 项仍然能读出来 ⇒ 确实是按 Key 找的
        Assert.Equal("daily_checkin", package.GrantSource);
    }

    [Fact]
    public void 真机_AccountAttributes_为空或缺失时不误报来源()
    {
        var noAttributes = RealPackageWithoutGrantSource();
        noAttributes.Remove("AccountAttributes");

        var emptyAttributes = RealPackageWithoutGrantSource();
        emptyAttributes["AccountAttributes"] = new JsonArray();

        Assert.Equal("", Assert.Single(Credits.Parse(Envelope([noAttributes])).Packages).GrantSource);
        Assert.Equal("", Assert.Single(Credits.Parse(Envelope([emptyAttributes])).Packages).GrantSource);
    }

    // ══ ② 大量包（真机 28 个）═══════════════════════════════════

    /// <summary>
    /// 造真机那种分布：8 个签到包（各 100，合计 800）+ 20 个普通包（各 120，合计 2400）。
    ///
    /// ⚠ 这是**按真机的分布重建**的（数量、分类、剩余合计都对齐实测），
    /// 但每个包的字段值是构造的 —— 逐字夹具只有上面两个。
    /// 本条测的是**聚合能力**，不是单包读取（那个由真机逐字用例覆盖）。
    /// </summary>
    private static List<JsonObject> RealDistribution()
    {
        var list = new List<JsonObject>();

        for (var i = 0; i < 8; i++)
        {
            list.Add(new JsonObject
            {
                ["PackageName"] = "CodeBuddy个人版国内运营裂变包",
                ["PackageCode"] = $"TCACA_code_00{i}",
                ["CycleEndTime"] = $"2026-10-{20 + i:D2} 08:26:15",
                ["CapacitySizePrecise"] = "100",
                ["CapacityRemainPrecise"] = "100",
                ["CapacityUsedPrecise"] = "0",
                ["CycleCapacitySizePrecise"] = "100",
                ["CycleCapacityRemainPrecise"] = "100",
                ["Status"] = 0,
                ["AccountAttributes"] = new JsonArray(
                    new JsonObject { ["Key"] = "grantSource", ["Value"] = "daily_checkin" }),
            });
        }

        for (var i = 0; i < 20; i++)
        {
            list.Add(new JsonObject
            {
                ["PackageName"] = "CodeBuddy个人版包",
                ["PackageCode"] = $"TCACA_code_1{i:D2}",
                ["CycleEndTime"] = $"2026-11-{i + 1:D2} 23:59:59",
                ["CapacitySizePrecise"] = "120",
                ["CapacityRemainPrecise"] = "120",
                ["CapacityUsedPrecise"] = "0",
                ["CycleCapacitySizePrecise"] = "120",
                ["CycleCapacityRemainPrecise"] = "120",
                ["Status"] = 0,
                ["AccountAttributes"] = new JsonArray(
                    new JsonObject { ["Key"] = "payerType", ["Value"] = "0" }),
            });
        }

        return list;
    }

    [Fact]
    public void 真机分布_28个包_聚合计正确()
    {
        var snapshot = Credits.Parse(Envelope(RealDistribution(), totalCount: 28, totalDosage: 3200));

        Assert.Equal(28, snapshot.Packages.Count);
        Assert.Equal(28, snapshot.TotalCount);
        Assert.Equal(3200, snapshot.TotalDosage);

        // 总剩余 = 8*100 + 20*120 = 800 + 2400 = 3200（与真机两次实测的合计一致）
        Assert.Equal(3200, snapshot.TotalRemaining);

        // 签到包单独可辨
        Assert.Equal(8, snapshot.DailyCheckinPackageCount);
        Assert.Equal(800, snapshot.DailyCheckinRemaining);
    }

    [Fact]
    public void 大量包时排序稳定且不漏不重()
    {
        var snapshot = Credits.Parse(Envelope(RealDistribution()));

        var sorted = snapshot.ByExpiryAscending();

        Assert.Equal(28, sorted.Count);
        // 升序：每一项都不比前一项早
        for (var i = 1; i < sorted.Count; i++)
        {
            var previous = CreditsSnapshot.TryParseTime(sorted[i - 1].CycleEndTime)!.Value;
            var current = CreditsSnapshot.TryParseTime(sorted[i].CycleEndTime)!.Value;
            Assert.True(previous <= current, $"第 {i} 项比前一项更早到期");
        }
        // 没丢也没重复
        Assert.Equal(28, sorted.Select(p => p.PackageCode).Distinct().Count());
    }

    // ══ ③ 派生能力 ═══════════════════════════════════════════════

    [Fact]
    public void 总剩余是自己累加而不是用_TotalDosage()
    {
        // ⚠ 语义区分：TotalDosage 是"发过的总量"，不是"还能用的量"。
        // 这里刻意让两者不等，钉住"用的是累加值"。
        var packages = new List<JsonObject>
        {
            new() { ["PackageName"] = "A", ["CapacityRemainPrecise"] = "10" },
            new() { ["PackageName"] = "B", ["CapacityRemainPrecise"] = "25.5" },
        };

        var snapshot = Credits.Parse(Envelope(packages, totalCount: 2, totalDosage: 9999));

        Assert.Equal(9999, snapshot.TotalDosage);      // 上游报的总量
        Assert.Equal(35.5, snapshot.TotalRemaining);   // ← 我们算的余额（不是 9999）
    }

    [Fact]
    public void 按到期升序_最先到期的在前()
    {
        var late = new JsonObject { ["PackageName"] = "晚", ["CycleEndTime"] = "2026-12-31 00:00:00" };
        var early = new JsonObject { ["PackageName"] = "早", ["CycleEndTime"] = "2026-10-08 00:00:00" };
        var middle = new JsonObject { ["PackageName"] = "中", ["CycleEndTime"] = "2026-11-01 00:00:00" };

        var snapshot = Credits.Parse(Envelope([late, early, middle]));

        Assert.Equal(["早", "中", "晚"], snapshot.ByExpiryAscending().Select(p => p.PackageName));
    }

    [Fact]
    public void 按到期升序_解析不出时间的排最后()
    {
        // 解不出时间 ⇒ 没有倒计时可言 ⇒ 排最后。
        // **不能**当成"最早到期"——那会制造假的紧迫感。
        var noTime = new JsonObject { ["PackageName"] = "无时间", ["CycleEndTime"] = "" };
        var junkTime = new JsonObject { ["PackageName"] = "坏时间", ["CycleEndTime"] = "不是时间" };
        var real = new JsonObject { ["PackageName"] = "有时间", ["CycleEndTime"] = "2026-12-31 00:00:00" };

        var snapshot = Credits.Parse(Envelope([noTime, junkTime, real]));

        var sorted = snapshot.ByExpiryAscending();
        Assert.Equal("有时间", sorted[0].PackageName);
        // 后两个都在它之后（彼此顺序按名字，稳定）
        Assert.Equal(["坏时间", "无时间"], sorted.Skip(1).Select(p => p.PackageName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void N天内到期的包数()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var inOneDay = new JsonObject { ["PackageName"] = "1天", ["CycleEndTime"] = "2026-10-08 12:00:00" };
        var inFiveDays = new JsonObject { ["PackageName"] = "5天", ["CycleEndTime"] = "2026-10-12 12:00:00" };
        var inTwentyDays = new JsonObject { ["PackageName"] = "20天", ["CycleEndTime"] = "2026-10-27 12:00:00" };

        var snapshot = Credits.Parse(Envelope([inOneDay, inFiveDays, inTwentyDays]));

        Assert.Equal(2, snapshot.PackagesExpiringWithin(7, now));    // 1天 + 5天
        Assert.Equal(0, snapshot.PackagesExpiringWithin(0, now));    // 没有"立刻到期"的
        Assert.Equal(3, snapshot.PackagesExpiringWithin(30, now));   // 全部
    }

    [Fact]
    public void 已过期的包不计入_N天内到期()
    {
        // ⚠ 已过期的包不是"即将到期"而是"已经没了"。
        // 算进来的话告警会永远响着（恒 ≥1）。
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var expired = new JsonObject { ["PackageName"] = "昨天过期", ["CycleEndTime"] = "2026-10-06 12:00:00" };
        var soon = new JsonObject { ["PackageName"] = "明天过期", ["CycleEndTime"] = "2026-10-08 12:00:00" };

        var snapshot = Credits.Parse(Envelope([expired, soon]));

        Assert.Equal(1, snapshot.PackagesExpiringWithin(7, now));
        Assert.Equal(["明天过期"], snapshot.ExpiringWithin(7, now).Select(p => p.PackageName));
    }

    [Fact]
    public void 到期边界_含当天不含过去()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var exactlyNow = new JsonObject { ["PackageName"] = "此刻", ["CycleEndTime"] = "2026-10-07 12:00:00" };
        var exactlyLimit = new JsonObject { ["PackageName"] = "正好7天", ["CycleEndTime"] = "2026-10-14 12:00:00" };

        var snapshot = Credits.Parse(Envelope([exactlyNow, exactlyLimit]));

        // 两个边界都算在内（闭区间 [now, now+days]）
        Assert.Equal(2, snapshot.PackagesExpiringWithin(7, now));
    }

    // ══ ④ 边界：缺失 / 空 / 单包 ═════════════════════════════════

    [Fact]
    public void 空数组()
    {
        var snapshot = Credits.Parse(Envelope([], totalCount: 0, totalDosage: 0));

        Assert.Empty(snapshot.Packages);
        Assert.Equal(0, snapshot.TotalRemaining);
        Assert.Equal(0, snapshot.DailyCheckinRemaining);
        Assert.Equal(0, snapshot.DailyCheckinPackageCount);
        Assert.Empty(snapshot.ByExpiryAscending());
        Assert.Equal(0, snapshot.PackagesExpiringWithin(7, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void 单个包()
    {
        var snapshot = Credits.Parse(Envelope([RealPackageWithGrantSource()]));

        Assert.Single(snapshot.Packages);
        Assert.Equal(100, snapshot.TotalRemaining);
        Assert.Equal(1, snapshot.DailyCheckinPackageCount);
    }

    [Fact]
    public void 三层嵌套缺任一层都返回空而不抛()
    {
        // ⚠ 少一层不会报错，只会让列表为空 —— 这正是这个接口最容易静默出错的地方。
        // 所以每一层都单独测。
        Assert.Empty(Credits.Parse(null).Packages);
        Assert.Empty(Credits.Parse(JsonValue.Create(1)).Packages);
        Assert.Empty(Credits.Parse(new JsonObject()).Packages);                                    // 缺 code/data
        Assert.Empty(Credits.Parse(new JsonObject { ["data"] = (JsonNode?)null }).Packages);         // data 显式 null
        Assert.Empty(Credits.Parse(new JsonObject { ["data"] = JsonValue.Create("x") }).Packages);   // data 不是对象
        Assert.Empty(Credits.Parse(new JsonObject { ["data"] = new JsonObject() }).Packages);        // 缺 Response
        Assert.Empty(Credits.Parse(new JsonObject
        {
            ["data"] = new JsonObject { ["Response"] = new JsonObject() },
        }).Packages);                                                                               // 缺 Response.Data
        Assert.Empty(Credits.Parse(new JsonObject
        {
            ["data"] = new JsonObject { ["Response"] = new JsonObject { ["Data"] = new JsonObject() } },
        }).Packages);                                                                               // 缺 Accounts
    }

    [Fact]
    public void Accounts_不是数组时返回空()
    {
        var root = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["Response"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["Accounts"] = "not-an-array" },
                },
            },
        };

        Assert.Empty(Credits.Parse(root).Packages);
    }

    [Fact]
    public void 数组里混入非对象元素时跳过而不是崩()
    {
        var list = new JsonArray(
            RealPackageWithGrantSource(),
            JsonValue.Create(42),
            "字符串",
            new JsonArray(),
            RealPackageWithoutGrantSource());

        // 直接把这个**含脏元素的数组**放进信封 —— 走的才是真正那条路径
        var snapshot = Credits.Parse(EnvelopeOf(list));

        Assert.Equal(2, snapshot.Packages.Count);
        Assert.Equal(600, snapshot.TotalRemaining);   // 100 + 500，脏元素没被算进来
    }

    [Fact]
    public void 字段缺失时给安全默认_不抛()
    {
        // 只有一个空对象 —— 所有字段都缺
        var snapshot = Credits.Parse(Envelope([new JsonObject()]));

        var package = Assert.Single(snapshot.Packages);
        Assert.Equal("(unnamed)", package.PackageName);   // 与旧版一致的占位
        Assert.Equal(0, package.Size);
        Assert.Equal(0, package.Remain);
        Assert.Equal(0, package.Used);
        Assert.Equal("", package.CycleEndTime);
        Assert.Equal("", package.GrantSource);
        Assert.False(package.IsDailyCheckin);
    }

    // ══ ⑤ 精确字段（字符串小数）═════════════════════════════════

    [Fact]
    public void 数值优先读_Precise_字符串小数()
    {
        // ⚠ 这是与旧版的**有意差异**：旧版只读整数，遇到小数会静默取整。
        var package = new JsonObject
        {
            ["PackageName"] = "小数包",
            ["CapacitySizePrecise"] = "499.5",
            ["CapacityRemainPrecise"] = "123.75",
            ["CapacityUsedPrecise"] = "375.25",
            ["CycleCapacitySizePrecise"] = "499.5",
            ["CycleCapacityRemainPrecise"] = "123.75",
        };

        var read = Assert.Single(Credits.Parse(Envelope([package])).Packages);

        Assert.Equal(499.5, read.Size);
        Assert.Equal(123.75, read.Remain);
        Assert.Equal(375.25, read.Used);
        Assert.Equal(123.75, read.CycleRemain);

        // 余额也走精确字段（Envelope 会深拷贝，所以这里能安全地再包一次）
        Assert.Equal(123.75, Credits.Parse(Envelope([package])).TotalRemaining);
    }

    [Fact]
    public void Precise_缺失时退回整数兜底字段()
    {
        // Precise 字段整体缺失（老数据？）—— 退回 CapacityRemain 等整数
        var package = new JsonObject
        {
            ["PackageName"] = "只有整数",
            ["CapacityRemain"] = 42,
            ["CapacitySize"] = 100,
            ["CapacityUsed"] = 58,
        };

        var read = Assert.Single(Credits.Parse(Envelope([package])).Packages);

        Assert.Equal(42, read.Remain);
        Assert.Equal(100, read.Size);
        Assert.Equal(58, read.Used);
    }

    [Fact]
    public void Precise_是坏值时退回整数()
    {
        var package = new JsonObject
        {
            ["PackageName"] = "坏Precise",
            ["CapacityRemainPrecise"] = "not-a-number",
            ["CapacityRemain"] = 77,
        };

        Assert.Equal(77, Assert.Single(Credits.Parse(Envelope([package])).Packages).Remain);
    }

    [Fact]
    public void Precise_直接是数字时也认()
    {
        // 上游两种都发过（字符串 / 数字）
        var package = new JsonObject
        {
            ["PackageName"] = "数字Precise",
            ["CapacityRemainPrecise"] = 88.5,
        };

        Assert.Equal(88.5, Assert.Single(Credits.Parse(Envelope([package])).Packages).Remain);
    }

    // ══ ⑥ 查询体 ═════════════════════════════════════════════════

    [Fact]
    public void 查询体对齐旧版()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 30, 45, TimeSpan.Zero);

        var body = Credits.BuildQueryBody(now);

        Assert.Equal(1, body["PageNumber"]!.GetValue<int>());
        Assert.Equal(100, body["PageSize"]!.GetValue<int>());
        Assert.Equal("p_tcaca", body["ProductCode"]!.GetValue<string>());
        Assert.Equal([0, 3], body["Status"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("2026-10-07 12:30:45", body["PackageEndTimeRangeBegin"]!.GetValue<string>());

        // 上界 = now + 3185136e6 ms（旧版的魔数，约 101 年，不是 36.86 天）
        //
        // ⚠ 直接比**格式化后的字符串**，不要 DateTimeOffset.Parse 再比 ——
        // 上游格式不带时区，Parse 会按**本机时区**解释，于是这个断言在
        // UTC+8 的机器上必然失败（CI 与开发机时区不同就更糟）。
        // 比字符串既准确又与时区无关。
        Assert.Equal("2127-09-13 12:30:45", body["PackageEndTimeRangeEnd"]!.GetValue<string>());

        // 顺带证明"魔数确实是 3185136e6 毫秒"（用不受时区影响的 UTC 时刻算）
        var expectedEnd = now.AddMilliseconds(3_185_136_000_000L);
        Assert.Equal("2127-09-13 12:30:45", expectedEnd.ToString("yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 查询体_发的是裸本地时间_不做时区换算()
    {
        // ⚠⚠ 这条钉的是 t57 做的一个**实测判定** ⚠⚠
        //
        // 争议：`format(now)` 用的是 `DateTimeOffset` **自带的偏移量**，
        // 也就是说发出去的是"调用方的本地时刻"。这会不会是"碰巧对"？
        //
        // **实测结论：契约对。** 真机边界实验（已知某包在
        // `2026-10-08 08:21:25` 到期，把窗口上界卡在它两侧）：
        // ```
        // end = "2026-10-08 08:21:24"  → TotalCount = 4   （不含）
        // end = "2026-10-08 08:21:26"  → TotalCount = 5   （含）
        // ```
        // **边界严丝合缝落在字面量上、零时区平移** ⇒ 上游就是拿字符串
        // 当本地时间直接比，两边都不解析时区。
        //
        // 所以本用例断言的是：**同一个本地时刻，不同偏移量 → 同一个字符串**。
        // 这正是"双方约定用裸本地时间"的形状。
        var asUtc = new DateTimeOffset(2026, 10, 7, 12, 30, 45, TimeSpan.Zero);
        var asCst = new DateTimeOffset(2026, 10, 7, 12, 30, 45, TimeSpan.FromHours(8));

        var beginUtc = Credits.BuildQueryBody(asUtc)["PackageEndTimeRangeBegin"]!.GetValue<string>();
        var beginCst = Credits.BuildQueryBody(asCst)["PackageEndTimeRangeBegin"]!.GetValue<string>();

        // 只取"本地时分秒"，偏移量被丢掉 ⇒ 两者相同
        Assert.Equal("2026-10-07 12:30:45", beginUtc);
        Assert.Equal(beginUtc, beginCst);
    }

    [Fact]
    public void 查询体_偏移量不参与格式化()
    {
        // 同一天同一时刻、偏移量不同 → 字面量相同（偏移量被丢弃）。
        // 记录这个事实，免得下一个人以为它做了 UTC 归一。
        var utc = Credits.BuildQueryBody(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var plus8 = Credits.BuildQueryBody(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(
            utc["PackageEndTimeRangeBegin"]!.GetValue<string>(),
            plus8["PackageEndTimeRangeBegin"]!.GetValue<string>());
    }

    [Fact]
    public void 查询体时间格式是空格分隔不带时区()
    {
        // 上游要的是 "YYYY-MM-DD HH:mm:ss"，不是 ISO（带 T）
        var body = Credits.BuildQueryBody(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var begin = body["PackageEndTimeRangeBegin"]!.GetValue<string>();
        Assert.Equal("2026-01-02 03:04:05", begin);
        Assert.DoesNotContain("T", begin, StringComparison.Ordinal);
    }

    // ══ ⑦ 常量契约 ═══════════════════════════════════════════════

    [Fact]
    public void 端点与来源常量与旧版一致()
    {
        Assert.Equal("/v2/billing/meter/get-user-resource", Credits.ResourcePath);
        Assert.Equal("daily_checkin", Credits.DailyCheckinSource);
        Assert.Equal("grantSource", Credits.GrantSourceKey);
        Assert.Equal("p_tcaca", Credits.ProductCode);
    }
}
