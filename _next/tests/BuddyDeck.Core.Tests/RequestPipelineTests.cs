using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 请求体改写管线的测试。
///
/// 这层的作用是「把各家客户端五花八门的请求，改成上游唯一认的那种」。
/// 所以测试的重点是**各种客户端写法都要能正确归一**，以及
/// 「不认识的字段不许弄丢」。
/// </summary>
public class RequestPipelineTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // ── 强制 stream ───────────────────────────────────────────

    [Fact]
    public void 非流式请求会被改成流式()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"stream":false,"messages":[]}""");
        Assert.True(Parse(result)["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void 没有_stream_字段也会补上()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"messages":[]}""");
        Assert.True(Parse(result)["stream"]!.GetValue<bool>());
    }

    // ── developer 角色归一 ────────────────────────────────────

    [Fact]
    public void developer_角色改成_system()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[{"role":"developer","content":"be nice"},{"role":"user","content":"hi"}]}
        """);
        var messages = Parse(result)["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 正常的_system_角色不动()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"messages":[{"role":"system","content":"x"}]}""");
        Assert.Equal("system", Parse(result)["messages"]![0]!["role"]!.GetValue<string>());
    }

    // ── tool_choice 归一 ──────────────────────────────────────

    [Fact]
    public void tool_choice_none_字符串_会连_tools_一起删()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"tool_choice":"none","tools":[{"type":"function","function":{"name":"x"}}]}
        """);
        var body = Parse(result).AsObject();
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.False(body.ContainsKey("tools"));
    }

    [Fact]
    public void tool_choice_none_对象形态也一样()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"tool_choice":{"type":"none"},"tools":[{"type":"function","function":{"name":"x"}}]}
        """);
        var body = Parse(result).AsObject();
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.False(body.ContainsKey("tools"));
    }

    [Fact]
    public void tool_choice_auto_对象形态会拍平成字符串()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"messages":[],"tool_choice":{"type":"auto"}}""");
        Assert.Equal("auto", Parse(result)["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_function_对象形态会取出函数名()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"tool_choice":{"type":"function","function":{"name":"get_weather"}}}
        """);
        Assert.Equal("get_weather", Parse(result)["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_function_也认_name_写法()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"tool_choice":{"type":"function","name":"direct_name"}}
        """);
        Assert.Equal("direct_name", Parse(result)["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_认不出的形态会被删掉()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"messages":[],"tool_choice":12345}""");
        Assert.False(Parse(result).AsObject().ContainsKey("tool_choice"));
    }

    [Fact]
    public void 没有_tool_choice_时不乱动_tools()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"tools":[{"type":"function","function":{"name":"x"}}]}
        """);
        Assert.True(Parse(result).AsObject().ContainsKey("tools"));
    }

    // ── 国际版补 system ───────────────────────────────────────

    [Fact]
    public void 国际版缺_system_时会前置一条()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.Global, """{"messages":[{"role":"user","content":"hi"}]}""");
        var messages = Parse(result)["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 国际版已有_system_时不动()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.Global, """
        {"messages":[{"role":"system","content":"custom"},{"role":"user","content":"hi"}]}
        """);
        var messages = Parse(result)["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("custom", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void 国内版不补_system()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """{"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Single(Parse(result)["messages"]!.AsArray());
    }

    // ── 不认识的字段必须保留 ──────────────────────────────────

    [Fact]
    public void 未知字段原样保留()
    {
        // 各家客户端都有自己的扩展字段，弄丢它们等于弄坏客户端。
        var result = RequestPipeline.Prepare(UpstreamVariant.China, """
        {"messages":[],"temperature":0.7,"top_p":0.9,"自定义字段":{"a":1},"metadata":{"user":"x"}}
        """);
        var body = Parse(result).AsObject();
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, body["top_p"]!.GetValue<double>());
        Assert.Equal(1, body["自定义字段"]!["a"]!.GetValue<int>());
        Assert.Equal("x", body["metadata"]!["user"]!.GetValue<string>());
    }

    // ── 坏输入：必须原样返回，不能把客户端的东西改坏 ──────────

    [Theory]
    [InlineData("不是 json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]        // 顶层不是对象
    [InlineData("\"字符串\"")]
    [InlineData("null")]
    public void 坏输入原样返回(string input)
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, input);
        Assert.Equal(input, result);
    }
}

/// <summary>
/// 请求头的测试。
///
/// 重点是那两条最容易漏的规矩：
///   · 空身份必须**显式**声明（X-No-XXX），不能省略
///   · IDE 归属族只能在 chat 上出现
/// </summary>
public class UpstreamHeadersTests
{
    private static Credential MakeCredential(string uid = "u1", string? ent = "e1", string domain = "workbuddy.cn")
        => new()
        {
            AccessToken = "tok",
            // 刷新接口要用它 —— 第一版测试里忘了设，断言 X-Refresh-Token 时读到空串。
            // 测试当场抓到，说明这条断言是有效的。
            RefreshToken = "refresh-tok",
            Uid = uid,
            EnterpriseId = ent,
            Domain = domain,
        };

    private static HttpRequestMessage NewRequest() => new(HttpMethod.Post, "https://example.test/x");

    [Fact]
    public void 有身份时带正常的身份头()
    {
        var req = NewRequest();
        UpstreamHeaders.ApplyChat(req, MakeCredential(), UpstreamVariant.China, null, null);

        Assert.Equal("u1", req.Headers.GetValues("X-User-Id").Single());
        Assert.Equal("e1", req.Headers.GetValues("X-Enterprise-Id").Single());
        Assert.Equal("workbuddy.cn", req.Headers.GetValues("X-Domain").Single());
        Assert.False(req.Headers.Contains("X-No-User-Id"));
        Assert.False(req.Headers.Contains("X-No-Enterprise-Id"));
    }

    [Fact]
    public void 空身份时带_No_标记且不给空值头()
    {
        // 这是这套协议里最容易漏的一条：
        // 「缺 X-User-Id」和「给 X-No-User-Id: 1」是两回事。
        var req = NewRequest();
        UpstreamHeaders.ApplyChat(req, MakeCredential(uid: "", ent: null, domain: ""), UpstreamVariant.China, null, null);

        Assert.Equal("1", req.Headers.GetValues("X-No-User-Id").Single());
        Assert.Equal("1", req.Headers.GetValues("X-No-Enterprise-Id").Single());
        Assert.Equal("1", req.Headers.GetValues("X-No-Department-Info").Single());
        Assert.False(req.Headers.Contains("X-User-Id"));
        Assert.False(req.Headers.Contains("X-Enterprise-Id"));
        Assert.False(req.Headers.Contains("X-Domain"));
    }

    [Fact]
    public void chat_带_IDE_归属族()
    {
        var req = NewRequest();
        UpstreamHeaders.ApplyChat(req, MakeCredential(), UpstreamVariant.China, "WorkBuddy/5.6.2", "5.6.2");

        Assert.Equal("WorkBuddy", req.Headers.GetValues("X-IDE-Type").Single());
        Assert.Equal("WorkBuddy", req.Headers.GetValues("X-IDE-Name").Single());
        Assert.Equal("5.6.2", req.Headers.GetValues("X-IDE-Version").Single());
        Assert.Equal("SaaS", req.Headers.GetValues("X-Product").Single());
    }

    [Fact]
    public void 没有客户端版本时不给_X_IDE_Version()
    {
        var req = NewRequest();
        UpstreamHeaders.ApplyChat(req, MakeCredential(), UpstreamVariant.China, null, null);
        Assert.False(req.Headers.Contains("X-IDE-Version"));
    }

    [Fact]
    public void 刷新请求带_refresh_token_但不带_IDE_族()
    {
        // 归属族混进刷新请求会把用量账算乱。
        var req = NewRequest();
        UpstreamHeaders.ApplyRefresh(req, MakeCredential(), UpstreamVariant.China);

        Assert.Equal("refresh-tok", req.Headers.GetValues("X-Refresh-Token").Single());
        Assert.Equal("workbuddy", req.Headers.GetValues("X-Auth-Refresh-Source").Single());
        Assert.False(req.Headers.Contains("X-IDE-Type"));
        Assert.False(req.Headers.Contains("X-User-Id"));
    }

    [Fact]
    public void 计费请求只有授权头()
    {
        var req = NewRequest();
        UpstreamHeaders.ApplyBilling(req, MakeCredential());

        Assert.Equal("Bearer tok", req.Headers.GetValues("Authorization").Single());
        Assert.False(req.Headers.Contains("X-IDE-Type"));
        Assert.False(req.Headers.Contains("X-User-Id"));
    }
}
