using System.Text;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 限流历史落盘的测试 —— 用户核心需求「重启后仍能看到哪些模型限流过、何时解除」。
///
/// ## 为什么必须落盘（实测背景）
///
/// `AccountPool.RateLimits` 在内存里累积（成功不清），
/// 但**重启进程就全没了** —— 用户看到的又是"全绿池健康"，
/// 而他体感是"deepseek 基本都限流了"。
///
/// ## 测试约定
///
/// · 键用 **uid（36 位完整）**，不用截断 tag ——
///   实测积分端点的 accountTag 是 uid 前 8 位，相等匹配永远失败。
///   存储必须用稳定键，展示层要短再自己截。
/// </summary>
public sealed class RateLimitStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly RateLimitStore _store;

    public RateLimitStoreTests()
    {
        _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rlstore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = System.IO.Path.Combine(_dir, "rate-limits.json");
        _store = new RateLimitStore(() => _path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ModelRateLimit R(int count, long lastAt, long resetAt, bool recovered)
        => new(count, lastAt, resetAt, recovered);

    // ══════════════════════════════════════════════════════════
    // ① 写了能读回
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 写入后读回_字段逐个一致()
    {
        var uid = "3e563042-d9da-4ce6-bbb8-bf52debdbff0";
        var model = "deepseek-v4.1-flash";
        var history = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>
        {
            [uid] = new Dictionary<string, ModelRateLimit>
            {
                [model] = R(3, 1791370000000, 1791370300000, recovered: false),
            },
        };

        _store.Save(history);
        var loaded = _store.Load();

        // ★ 键是**完整 uid**（36 位）—— 截断键会让账号列表匹配不上
        //   （积分端点的 accountTag 就踩过这条）。
        Assert.True(loaded.ContainsKey(uid), "必须用完整 uid 当键");
        Assert.True(loaded[uid].ContainsKey(model));

        var rl = loaded[uid][model];
        Assert.Equal(3, rl.Count);
        Assert.Equal(1791370000000L, rl.LastAtMs);
        Assert.Equal(1791370300000L, rl.LastResetAtMs);
        Assert.False(rl.Recovered);
    }

    [Fact]
    public void 多号多模型_全部读回()
    {
        var history = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>
        {
            ["uid-aaa"] = new Dictionary<string, ModelRateLimit>
            {
                ["deepseek-v4.1-flash"] = R(2, 1000, 2000, false),
                ["glm-5.3"] = R(1, 1500, 2500, true),
            },
            ["uid-bbb"] = new Dictionary<string, ModelRateLimit>
            {
                ["hy3"] = R(5, 3000, 4000, false),
            },
        };

        _store.Save(history);
        var loaded = _store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, loaded["uid-aaa"].Count);
        Assert.Equal(1, loaded["uid-bbb"].Count);
        Assert.True(loaded["uid-aaa"]["glm-5.3"].Recovered);
        Assert.Equal(5, loaded["uid-bbb"]["hy3"].Count);
    }

    // ══════════════════════════════════════════════════════════
    // ② 空与损坏的容错
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 文件不存在_返回空历史_不抛()
    {
        var empty = new RateLimitStore(() => System.IO.Path.Combine(_dir, "不存在.json"));

        var loaded = empty.Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void 文件损坏_返回空历史()
    {
        // 半截文件 —— 原子写就是为了防它，但外部损坏仍可能发生
        System.IO.File.WriteAllText(
            _path, "{ \"version\": 1, \"entries\": { \"uid-aaa\": {", Encoding.UTF8);

        var loaded = _store.Load();

        // 空历史起步 —— **池还能跑**，只是少一块
        Assert.Empty(loaded);
    }

    [Fact]
    public void 顶层不是对象_返回空历史()
    {
        System.IO.File.WriteAllText(_path, "[1,2,3]", Encoding.UTF8);

        Assert.Empty(_store.Load());
    }

    // ══════════════════════════════════════════════════════════
    // ③ 覆盖写（导出/导入同款语义）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 二次写入_覆盖旧内容而不是追加()
    {
        var first = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>
        {
            ["uid-old"] = new Dictionary<string, ModelRateLimit>
            {
                ["m"] = R(1, 1, 2, false),
            },
        };
        _store.Save(first);

        var second = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>
        {
            ["uid-new"] = new Dictionary<string, ModelRateLimit>
            {
                ["m"] = R(9, 9, 9, true),
            },
        };
        _store.Save(second);

        var loaded = _store.Load();

        // ⚠ 全量覆盖：旧的 uid 必须不在了 ——
        //   若是"追加"语义，过期的号会永远留在文件里越积越多。
        Assert.False(loaded.ContainsKey("uid-old"));
        Assert.True(loaded.ContainsKey("uid-new"));
    }

    // ══════════════════════════════════════════════════════════
    // ④ 编码：中文标签/模型名不走样
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 路径含中文_读写正常()
    {
        var subdir = System.IO.Path.Combine(_dir, "中文目录");
        Directory.CreateDirectory(subdir);
        var store = new RateLimitStore(() => System.IO.Path.Combine(subdir, "历史.json"));

        var history = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>
        {
            ["uid-cn"] = new Dictionary<string, ModelRateLimit>
            {
                ["deepseek-v4.1-flash"] = R(1, 100, 200, false),
            },
        };

        store.Save(history);
        var loaded = store.Load();

        Assert.True(loaded.ContainsKey("uid-cn"));
    }
}
