using System.Text.Json;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 布尔字段的 JSON 绑定回归测试。
///
/// ## 这一组防的是什么（真实案例）
///
/// `IsTerminal` 曾经踩过一次**静默绑定失败**：
///
/// <code>
/// 网关卡发的键      "terminal"       （没有 is 前缀）
/// App 侧属性名      IsTerminal
/// System.Text.Json 的 PropertyNameCaseInsensitive
///                   → 只做「大小写无关的**完整名字**比较」
///                   → **不做 Is 前缀推理**
/// 结果              "terminal" 绑不上 IsTerminal
///                   → 属性留在**默认值 true**
///                   → 「所有失败的中间尝试都被当成终局结果」
/// </code>
///
/// **它编译不报、运行不报、字段还有值**（默认值看起来很合理），
/// 所以只能靠「用真实 JSON 形状反序列化 + 断言取到正确值」来防。
///
/// ## ⚠ 为什么必须覆盖 `false`
///
/// 默认值是 `true` 的字段在**失配时最难发现** ——
/// 因为「绑不上」和「本来就是 true」在观感上完全一样。
///
/// 具体说：如果只测 `"terminal": true`，那么**绑定成功**和**绑定失败取默认值**
/// 都会得到 `true` —— 测试永远绿，缺陷永远看不见。
///
/// **只有喂 `false` 才能区分两者**：
/// <list type="bullet">
///   <item>绑上了 → 读到 <c>false</c></item>
///   <item>没绑上 → 留在默认 <c>true</c>（测试红）</item>
/// </list>
///
/// 这条原则适用于本文件所有默认 <c>true</c> 的布尔字段。
/// </summary>
public class BooleanJsonBindingTests
{
    /// <summary>和网关真实序列化选项一致的读法（网关与 App 都用大小写不敏感）。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ========================================================================
    //  IsTerminal —— 已知事故点，`terminal` 键没有 is 前缀
    // ========================================================================

    /// <summary>
    /// **核心用例**：喂 <c>false</c>，断言读到的也是 <c>false</c>。
    ///
    /// 这条是防「绑不上 → 留在默认 true」的唯一有效判据：
    /// 只喂 true 的话，绑上与绑不上都是 true，测试区分不出来。
    /// </summary>
    [Fact]
    public void IsTerminal_读false必须得到false而不是默认的true()
    {
        var json = """{"terminal": false}""";

        var record = JsonSerializer.Deserialize<RecentRequest>(json, ReadOptions);

        Assert.NotNull(record);
        Assert.False(
            record!.IsTerminal,
            "IsTerminal 读到 true —— 说明 `terminal` 键没绑上，取值走的是默认值。"
            + "这正是历史事故的形态（默认 true 掩盖了绑定失败）。");
    }

    [Fact]
    public void IsTerminal_读true得到true()
    {
        var record = JsonSerializer.Deserialize<RecentRequest>(
            """{"terminal": true}""", ReadOptions);

        Assert.NotNull(record);
        Assert.True(record!.IsTerminal);
    }

    /// <summary>
    /// 大小写不敏感：网关若发 <c>IsTerminal</c>（首字母大写）也要绑得上。
    /// 这是 <c>PropertyNameCaseInsensitive</c> 的正当作用范围 ——
    /// **完整名字**的大小写差异，而不是前缀推理。
    /// </summary>
    [Theory]
    [InlineData("terminal")]
    [InlineData("Terminal")]
    [InlineData("TERMINAL")]
    public void IsTerminal_大小写变体都能绑上(string key)
    {
        var json = $$"""{"{{key}}": false}""";

        var record = JsonSerializer.Deserialize<RecentRequest>(json, ReadOptions);

        Assert.NotNull(record);
        Assert.False(record!.IsTerminal, $"键 `{key}` 没绑上");
    }

    // ========================================================================
    //  SentToUpstream —— 同样是默认 true 的字段
    // ========================================================================

    [Fact]
    public void SentToUpstream_读false必须得到false()
    {
        var record = JsonSerializer.Deserialize<RecentRequest>(
            """{"sentToUpstream": false}""", ReadOptions);

        Assert.NotNull(record);
        Assert.False(record!.SentToUpstream);
    }

    // ========================================================================
    //  PoolSummary.IsDefault —— 本次修复的目标字段
    // ========================================================================

    /// <summary>
    /// `IsDefault` 的绑定回归。
    ///
    /// 网关侧的实际键名见 `src/BuddyDeck.Gateway/AdminEndpoints.cs`：
    /// <c>["isDefault"] = true</c>（小写开头）。
    ///
    /// ⚠ `IsDefault` 的默认值是 `false`（不是 true），所以这条的
    /// 「最难发现」方向**相反**：绑不上时它会静默变成 `false` ——
    /// 而 `false` 恰好也是「不是默认池」的合法取值，同样看不出来。
    /// 所以要**两个方向都断言**。
    /// </summary>
    [Fact]
    public void IsDefault_读true必须得到true()
    {
        var json = """
            {"id":"p1","displayName":"默认池","port":8787,
             "apiKey":"","running":true,"accountCount":2,"isDefault":true,"lastError":null}
            """;

        var pool = JsonSerializer.Deserialize<PoolSummary>(json, ReadOptions);

        Assert.NotNull(pool);
        Assert.True(
            pool!.IsDefault,
            "IsDefault 读到 false —— `isDefault` 键没绑上。"
            + "后果：界面会把默认池显示成非默认池。");
    }

    [Fact]
    public void IsDefault_读false必须得到false()
    {
        var json = """
            {"id":"p2","displayName":"志强池","port":8788,
             "apiKey":"","running":false,"accountCount":1,"isDefault":false,"lastError":null}
            """;

        var pool = JsonSerializer.Deserialize<PoolSummary>(json, ReadOptions);

        Assert.NotNull(pool);
        Assert.False(pool!.IsDefault);
        Assert.False(pool.Running);
    }

    /// <summary>
    /// 用**真实网关的键名形状**整体反序列化一次，确认所有布尔都各就各位。
    ///
    /// 这条的价值：把「键名 ↔ 属性名」这层隐式约定**变成一条可执行的断言**。
    /// 哪天有人在网关上改了键名，这条会红 —— 而不是等到界面上显示出错误的池。
    /// </summary>
    [Fact]
    public void PoolSummary_用真实网关键名整体反序列化()
    {
        // 逐字对齐 AdminEndpoints.cs 的序列化键名
        var json = """
            {"id":"default","displayName":"默认池","port":8787,
             "apiKey":"sk-x","running":false,"accountCount":3,
             "isDefault":true,"lastError":null}
            """;

        var pool = JsonSerializer.Deserialize<PoolSummary>(json, ReadOptions);

        Assert.NotNull(pool);
        Assert.Equal("default", pool!.Id);
        Assert.Equal(8787, pool.Port);
        Assert.Equal(3, pool.AccountCount);

        // 两个布尔，各有各的默认值方向 —— 都要断言到
        Assert.True(pool.IsDefault, "isDefault 没绑上");
        Assert.False(pool.Running, "running 没绑上（默认 false，绑不上也是 false，需靠上面的 true 方向区分）");
    }

    /// <summary>
    /// 本组修复新增的三条标注也各验一遍。
    ///
    /// `Streamed` / `Active` / `Running` 三个都是**默认 `false`** 的字段，
    /// 所以「最难发现」的方向是 **`true`** —— 绑不上会静默变成 `false`，
    /// 而 `false` 是合法取值，界面上看不出来。
    ///
    /// （与 `IsTerminal`/`SentToUpstream` 正好相反：那两个默认 `true`，
    /// 难点在 `false` 方向。）
    /// </summary>
    [Fact]
    public void Streamed_读true必须得到true()
    {
        var record = JsonSerializer.Deserialize<RecentRequest>(
            """{"streamed":true,"terminal":true}""", ReadOptions);

        Assert.NotNull(record);
        Assert.True(record!.Streamed, "`streamed` 没绑上（默认 false 会掩盖它）");
    }

    [Fact]
    public void Active_读true必须得到true()
    {
        var account = JsonSerializer.Deserialize<AccountSummary>(
            """{"id":"a1","active":true}""", ReadOptions);

        Assert.NotNull(account);
        Assert.True(account!.Active, "`active` 没绑上 —— 会把在用的号显示成没在用");
    }

    [Fact]
    public void Running_读true必须得到true()
    {
        var pool = JsonSerializer.Deserialize<PoolSummary>(
            """{"id":"p1","running":true}""", ReadOptions);

        Assert.NotNull(pool);
        Assert.True(pool!.Running, "`running` 没绑上 —— 会把在跑的池显示成没在跑");
    }

    /// <summary>
    /// 反向也验一遍（默认 false 的字段，`false` 方向不能误报）。
    /// </summary>
    [Fact]
    public void 默认false的字段读false不应误报()
    {
        var record = JsonSerializer.Deserialize<RecentRequest>(
            """{"streamed":false,"terminal":false}""", ReadOptions);

        Assert.NotNull(record);
        Assert.False(record!.Streamed);
        Assert.False(record.IsTerminal);
    }

    /// <summary>
    /// 对照：**缺失**的布尔键会取默认值 —— 这是框架行为，不是 bug。
    ///
    /// 把这层行为显式记录下来，是为了让「为什么必须显式标注」有据可依：
    /// 标注**不能**解决「键缺失」时的默认值语义，它只解决「键名对不上」。
    /// 两者的区别在 <see cref="BooleanJsonBindingTests"/> 的类注释里说明了。
    /// </summary>
    [Fact]
    public void 对照_键缺失时布尔取默认值这是框架行为()
    {
        // 完全不提 terminal
        var record = JsonSerializer.Deserialize<RecentRequest>("""{"model":"hy3"}""", ReadOptions);

        Assert.NotNull(record);
        // 默认值是 true —— 缺失即 true。这正是「默认值掩盖绑定失败」的机理。
        Assert.True(record!.IsTerminal);
    }
}
