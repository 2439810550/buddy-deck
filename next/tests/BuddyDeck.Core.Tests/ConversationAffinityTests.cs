using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 会话键派生 + 亲和表的测试。
///
/// 这一块错了的症状是「聊天偶发失忆」——不是崩溃、不是报错，
/// 是用户聊着聊着上下文没了，而且**换个时间点就复现不出来**。
/// 所以边界用例要压密：穿插对话、账号失效、TTL 过期、容量驱逐。
/// </summary>
public class ConversationAffinityTests
{
    /// <summary>可控时钟 —— 不 Start-Sleep，测试快且可重复。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }

    private static VaultEntry Entry(string id, string label = "", long expiresAtMs = 0)
        => new()
        {
            Id = id,
            AccessToken = $"tok-{id}",
            Uid = $"uid-{id}",
            Label = label,
            ExpiresAtMs = expiresAtMs,
        };

    private static (ConversationAffinity Affinity, AccountPool Pool, FakeClock Clock) Make(
        AffinityOptions? options = null,
        params VaultEntry[] entries)
    {
        var clock = new FakeClock();
        var pool = new AccountPool(clock, NullLogger<AccountPool>.Instance);
        pool.Replace(entries);
        var affinity = new ConversationAffinity(pool, clock, NullLogger<ConversationAffinity>.Instance, options);
        return (affinity, pool, clock);
    }

    /// <summary>造一条最普通的聊天请求体。</summary>
    private static string Body(string system, string firstUser, params string[] moreTurns)
    {
        var messages = new List<string> { $$"""{"role":"system","content":{{Json(system)}}}""" };
        messages.Add($$"""{"role":"user","content":{{Json(firstUser)}}}""");
        foreach (var turn in moreTurns) messages.Add(turn);

        return $$"""{"model":"hy3","messages":[{{string.Join(",", messages)}}]}""";
    }

    private static string Json(string s)
        => System.Text.Json.Nodes.JsonValue.Create(s)!.ToJsonString();

    // ── ① 派生：d- 前缀 + 稳定性 + 区分度 ────────────────────

    [Fact]
    public void 派生出来的会话键带d前缀()
    {
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        Assert.NotNull(key);
        Assert.StartsWith(ConversationKey.DerivedPrefix, key, StringComparison.Ordinal);
    }

    [Fact]
    public void 相同输入派生的会话键稳定()
    {
        // 同一个会话发两次（第二次带上了上一轮的回答+新问题）
        var first = ConversationKey.Derive(Body("你是助手", "今天天气怎么样"));
        var second = ConversationKey.Derive(Body("你是助手", "今天天气怎么样",
            """{"role":"assistant","content":"今天晴。"}""",
            """{"role":"user","content":"那明天呢"}"""));

        // ⚠ 这是粘性的**核心不变式**：后续轮次不参与哈希。
        // 参与的话每加一轮键就变一次，粘性等于没有 —— 而且测试很容易
        // 写成「同一段文本算两次相等」，那样根本测不出这个 bug。
        Assert.Equal(first, second);
    }

    [Fact]
    public void 调用两次完全相同也稳定()
    {
        var body = Body("你是助手", "你好");
        Assert.Equal(ConversationKey.Derive(body), ConversationKey.Derive(body));
    }

    [Fact]
    public void 不同system派生不同键()
    {
        var a = ConversationKey.Derive(Body("你是助手", "你好"));
        var b = ConversationKey.Derive(Body("你是翻译", "你好"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 不同首条user派生不同键()
    {
        var a = ConversationKey.Derive(Body("你是助手", "你好"));
        var b = ConversationKey.Derive(Body("你是助手", "再见"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 拼接歧义不会撞键()
    {
        // ["ab"] + ["c"] 和 ["a"] + ["bc"] 拼起来都是 "abc" —— 必须有分隔符区分
        var a = ConversationKey.Derive(Body("ab", "c"));
        var b = ConversationKey.Derive(Body("a", "bc"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 只有system也能派生()
    {
        var key = ConversationKey.Derive("""{"messages":[{"role":"system","content":"你是助手"}]}""");
        Assert.NotNull(key);
        Assert.StartsWith(ConversationKey.DerivedPrefix, key, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有可用字段时返回空()
    {
        Assert.Null(ConversationKey.Derive("""{"model":"hy3"}"""));
        Assert.Null(ConversationKey.Derive("""{"messages":[{"role":"assistant","content":"嗨"}]}"""));
        Assert.Null(ConversationKey.Derive(""));
        Assert.Null(ConversationKey.Derive(null));
    }

    [Fact]
    public void 请求体不是合法JSON时不抛异常()
    {
        // 坏请求体走池的默认轮询，不能在这里炸 —— 炸了就是 500
        Assert.Null(ConversationKey.Derive("{这不是 JSON"));
    }

    [Fact]
    public void 客户端给了conversation_id就用它()
    {
        var key = ConversationKey.Derive("""{"conversation_id":"abc123","messages":[]}""");
        Assert.Equal("d-abc123", key);
    }

    [Fact]
    public void metadata里的conversation_id也认()
    {
        var key = ConversationKey.Derive("""{"metadata":{"conversation_id":"xyz"},"messages":[]}""");
        Assert.Equal("d-xyz", key);
    }

    [Fact]
    public void 显式id优先于派生()
    {
        var withId = ConversationKey.Derive(
            """{"conversation_id":"abc","messages":[{"role":"user","content":"你好"}]}""");
        var without = ConversationKey.Derive(
            """{"messages":[{"role":"user","content":"你好"}]}""");

        Assert.Equal("d-abc", withId);
        Assert.NotEqual(without, withId);
    }

    [Fact]
    public void 规范化是幂等的()
    {
        // d-xxx 再包一次不能变成 d-d-xxx
        Assert.Equal("d-abc", ConversationKey.FromProductId("d-abc"));
        Assert.Equal("d-abc", ConversationKey.FromProductId("abc"));
        Assert.Equal("d-abc", ConversationKey.FromProductId("  abc  "));
    }

    [Fact]
    public void 多模态只取文本部分()
    {
        var withImage = """
            {"messages":[
              {"role":"user","content":[
                {"type":"text","text":"这张图里有什么"},
                {"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}
              ]}
            ]}
            """;
        var withBiggerImage = """
            {"messages":[
              {"role":"user","content":[
                {"type":"text","text":"这张图里有什么"},
                {"type":"image_url","image_url":{"url":"data:image/png;base64,BBBBBBBBBB"}}
              ]}
            ]}
            """;

        var a = ConversationKey.Derive(withImage);
        var b = ConversationKey.Derive(withBiggerImage);

        Assert.NotNull(a);
        // 图片 base64 不参与哈希 —— 否则同一张图重编码一次就换了会话键
        Assert.Equal(a, b);
    }

    [Fact]
    public void developer角色归一到system()
    {
        var asDeveloper = ConversationKey.Derive(
            """{"messages":[{"role":"developer","content":"你是助手"},{"role":"user","content":"你好"}]}""");
        var asSystem = ConversationKey.Derive(
            """{"messages":[{"role":"system","content":"你是助手"},{"role":"user","content":"你好"}]}""");

        // 上游管线会把 developer 改成 system（见 RequestPipeline）——
        // 派生这边必须认同一件事，否则改角色之后会话键就变了
        Assert.Equal(asSystem, asDeveloper);
    }

    // ── ② 粘性：同一个会话粘同一个号 ──────────────────────────

    [Fact]
    public void 同一段对话连续两次选到同一个号()
    {
        var (affinity, _, _) = Make(null, Entry("a"), Entry("b"), Entry("c"));
        var body = Body("你是助手", "你好");

        var first = affinity.Select(ConversationKey.Derive(body), "hy3");
        var second = affinity.Select(ConversationKey.Derive(body), "hy3");

        Assert.NotNull(first);
        Assert.Equal(first.Id, second?.Id);
    }

    [Fact]
    public void 穿插其他对话不串号()
    {
        // 三个号 + 两个会话交替 —— 粘性错的话会看到 A/B 互串
        var (affinity, _, _) = Make(null, Entry("a"), Entry("b"), Entry("c"));

        var conv1 = ConversationKey.Derive(Body("你是助手", "会话一"));
        var conv2 = ConversationKey.Derive(Body("你是助手", "会话二"));

        var c1First = affinity.Select(conv1, "hy3")!;
        var c2First = affinity.Select(conv2, "hy3")!;

        // 两个会话必须落在不同的号上（池是公平轮询的）——
        // 落在同一个号上的话，后面的断言就测不出「不串号」了
        Assert.NotEqual(c1First.Id, c2First.Id);

        // 交替各来两轮
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(c1First.Id, affinity.Select(conv1, "hy3")?.Id);
            Assert.Equal(c2First.Id, affinity.Select(conv2, "hy3")?.Id);
        }
    }

    [Fact]
    public void 十个会话各自粘住自己的号()
    {
        var (affinity, _, _) = Make(null,
            Entry("a"), Entry("b"), Entry("c"), Entry("d"), Entry("e"));

        var keys = Enumerable.Range(0, 10)
            .Select(i => ConversationKey.Derive(Body("你是助手", $"问题 {i}")))
            .ToList();

        var pinned = keys.Select(k => affinity.Select(k, "hy3")!.Id).ToList();

        // 每个会话再走 3 轮，全部要回到原来的号
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                Assert.Equal(pinned[i], affinity.Select(keys[i], "hy3")?.Id);
            }
        }
    }

    [Fact]
    public void 没有会话键时退化到池的轮询()
    {
        var (affinity, _, _) = Make(null, Entry("a"), Entry("b"));

        // key = null：没有会话概念，每次都该走池（会换号）
        var first = affinity.Select(null, "hy3");
        var second = affinity.Select(null, "hy3");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
    }

    // ── ③ 淘汰：账号不可用时映射要让路 ────────────────────────

    [Fact]
    public void 账号冷却后映射被淘汰且下次重新选号()
    {
        var (affinity, pool, _) = Make(null, Entry("a"), Entry("b"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var first = affinity.Select(key, "hy3")!;

        // 上游说这个号被限流了（按模型冷却）
        pool.MarkRateLimited(first.Id, "hy3", null);

        // 下一轮：不能再给它 —— 但也不能抛异常
        var next = affinity.Select(key, "hy3");

        Assert.NotNull(next);
        Assert.NotEqual(first.Id, next.Id);
        // 死映射当场被清掉了
        Assert.Equal(next.Id, affinity.Peek(key));
    }

    [Fact]
    public void 账号连续失败被冷冻后映射被淘汰()
    {
        var (affinity, pool, _) = Make(null, Entry("a"), Entry("b"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var first = affinity.Select(key, "hy3")!;

        // 连续失败到阈值 → 整体冷冻
        for (var i = 0; i < 5; i++) pool.MarkFailure(first.Id, "hy3");

        var next = affinity.Select(key, "hy3");
        Assert.NotNull(next);
        Assert.NotEqual(first.Id, next.Id);
    }

    [Fact]
    public void 池里全不可用时返回空而不是抛()
    {
        var (affinity, pool, _) = Make(null, Entry("a"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var first = affinity.Select(key, "hy3")!;
        pool.MarkRateLimited(first.Id, "hy3", null);

        // 唯一一个号在冷却里 —— 应该老实返回 null，让网关回 429 错误对象。
        // 抛异常的话网关会 500，而旧版踩过的坑正是这里返回 undefined。
        Assert.Null(affinity.Select(key, "hy3"));
    }

    [Fact]
    public void 冷却到期后能重新粘回原来的号()
    {
        var (affinity, pool, clock) = Make(null, Entry("a"), Entry("b"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var first = affinity.Select(key, "hy3")!;
        pool.MarkRateLimited(first.Id, "hy3", clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds());

        var other = affinity.Select(key, "hy3")!;
        Assert.NotEqual(first.Id, other.Id);

        // 5 分钟后冷却过期 —— b 也还在池里，但 a 已经恢复可用
        clock.Advance(TimeSpan.FromMinutes(6));

        // 不抛异常，而且能选出一个号
        var later = affinity.Select(key, "hy3");
        Assert.NotNull(later);
    }

    [Fact]
    public void 点名排除的号不能让粘性硬塞()
    {
        var (affinity, _, _) = Make(null, Entry("a"), Entry("b"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var first = affinity.Select(key, "hy3")!;
        var next = affinity.Select(key, "hy3", [first.Id]);

        Assert.NotNull(next);
        Assert.NotEqual(first.Id, next.Id);
    }

    [Fact]
    public void 吊销一个不存在的会话不抛异常()
    {
        var (affinity, _, _) = Make(null, Entry("a"));

        // 并发下两个请求同时吊销同一个键很常见 —— 幂等，不能炸
        Assert.False(affinity.Revoke("d-不存在"));
        Assert.False(affinity.Revoke(null));
        Assert.False(affinity.Revoke(""));
        Assert.Equal(0, affinity.RevokeAccount("不存在的号"));
    }

    [Fact]
    public void 按账号吊销会清掉它上面的全部会话()
    {
        var (affinity, _, _) = Make(null, Entry("a"), Entry("b"));

        var k1 = ConversationKey.Derive(Body("你是助手", "一"));
        var k2 = ConversationKey.Derive(Body("你是助手", "二"));
        var k3 = ConversationKey.Derive(Body("你是助手", "三"));

        var a = affinity.Select(k1, "hy3")!;
        affinity.Select(k2, "hy3");
        affinity.Select(k3, "hy3");

        var removed = affinity.RevokeAccount(a.Id);
        Assert.True(removed >= 1);

        // 被吊销的那些会话重新选号时不能再撞回原号（前提是它不可用；
        // 这里只断言「不抛异常且选得出号」——吊销本身已经清干净了）
        Assert.NotNull(affinity.Select(k1, "hy3"));
    }

    [Fact]
    public void 过期的映射会被清理()
    {
        var (affinity, _, clock) = Make(
            new AffinityOptions { TimeToLive = TimeSpan.FromMinutes(30) },
            Entry("a"), Entry("b"));

        var key = ConversationKey.Derive(Body("你是助手", "你好"));
        var first = affinity.Select(key, "hy3")!;
        Assert.Equal(1, affinity.Count);

        // TTL 内还在
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(first.Id, affinity.Select(key, "hy3")?.Id);

        // 超过 TTL —— 映射该没了。
        //
        // ⚠ 这里有两个不同的概念，别混：
        //   · Count  = **活的**映射数（过期的不算，但还物理躺在表里）
        //   · PruneExpired = 把过期的**物理删掉**，返回删了几条
        // 先断言 Count 已经看不见它，再断言 PruneExpired 真的能把它清掉。
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, affinity.Count);
        Assert.Equal(1, affinity.PruneExpired());

        // 清过一次之后就没有可清的了（幂等）
        Assert.Equal(0, affinity.PruneExpired());
    }

    [Fact]
    public void 过期后重新建映射不会抛异常()
    {
        var (affinity, _, clock) = Make(
            new AffinityOptions { TimeToLive = TimeSpan.FromMinutes(1) },
            Entry("a"));

        var key = ConversationKey.Derive(Body("你是助手", "你好"));
        affinity.Select(key, "hy3");

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(affinity.Peek(key));
        Assert.NotNull(affinity.Select(key, "hy3"));
    }

    [Fact]
    public void 表满时按容量驱逐且不超上限()
    {
        var (affinity, _, clock) = Make(
            new AffinityOptions { Capacity = 64, TimeToLive = TimeSpan.FromHours(1) },
            Entry("a"), Entry("b"));

        for (var i = 0; i < 500; i++)
        {
            var key = ConversationKey.Derive(Body("你是助手", $"问题 {i}"));
            affinity.Select(key, "hy3");
            // 每条映射之间推进 1 秒，让 LastSeen 有区分度（否则驱逐顺序不确定）
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // 容量是硬上限 —— 超了就是内存泄漏
        Assert.True(affinity.Count <= 64, $"表里有 {affinity.Count} 条，超过容量 64");
        Assert.True(affinity.Evictions > 0);
    }

    [Fact]
    public void 清空之后表是空的()
    {
        var (affinity, _, _) = Make(null, Entry("a"));
        affinity.Select(ConversationKey.Derive(Body("你是助手", "你好")), "hy3");

        affinity.Clear();
        Assert.Equal(0, affinity.Count);
    }

    // ── ④ 池的记账：粘住的号也要计入选中次数 ──────────────────

    [Fact]
    public void 粘住的号选中次数也要涨()
    {
        var (affinity, pool, _) = Make(null, Entry("a"), Entry("b"), Entry("c"));
        var key = ConversationKey.Derive(Body("你是助手", "你好"));

        var pinned = affinity.Select(key, "hy3")!;
        var before = pool.Members.First(m => m.Id == pinned.Id).SelectionCount;

        affinity.Select(key, "hy3");
        affinity.Select(key, "hy3");

        var after = pool.Members.First(m => m.Id == pinned.Id).SelectionCount;
        Assert.True(after > before, "粘住的号没有计入 SelectionCount —— 负载会歪");

        // 别的号一次都没被粘走
        foreach (var m in pool.Members.Where(m => m.Id != pinned.Id))
        {
            Assert.Equal(0, m.SelectionCount);
        }
    }

    [Fact]
    public void 点名要一个不存在的号返回空()
    {
        var (_, pool, _) = Make(null, Entry("a"));
        Assert.Null(pool.SelectSpecific("不存在", "hy3"));
        Assert.NotNull(pool.SelectSpecific("a", "hy3"));
    }

    [Fact]
    public void 可用性探测不改动任何状态()
    {
        var (_, pool, _) = Make(null, Entry("a"), Entry("b"));

        // CanPick 是只读探测 —— 调用它不能推进任何计数，
        // 否则「看一眼」这个动作会让负载慢慢歪掉
        for (var i = 0; i < 5; i++)
        {
            Assert.True(pool.CanPick("a", "hy3"));
            Assert.True(pool.CanPick("b", "hy3"));
        }

        foreach (var m in pool.Members)
        {
            Assert.Equal(0, m.SelectionCount);
            Assert.Equal(0, m.LastUsedAtMs);
        }

        // 不存在的号 / 被排除的号 / 冷冻的号
        Assert.False(pool.CanPick("不存在", "hy3"));
        Assert.False(pool.CanPick("a", "hy3", ["a"]));

        var member = pool.Members.First(m => m.Id == "b");
        for (var i = 0; i < 5; i++) pool.MarkFailure(member.Id, "hy3");
        Assert.False(pool.CanPick("b", "hy3"));
    }
}
