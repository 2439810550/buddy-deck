using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 上游请求头。
///
/// ## 为什么这里这么啰嗦
///
/// 上游是**腾讯的企业服务**，它按一组 `X-*` 头判断请求来自谁、走哪条链路：
///
/// ```
/// X-User-Id / X-No-User-Id              有 uid 就给 uid，没有要给「没有」的标记
/// X-Enterprise-Id / X-No-Enterprise-Id  同上（企业账号才有）
/// X-Domain / X-No-Department-Info       登录域，决定国内还是国际链路
/// X-IDE-Type / X-IDE-Name / X-IDE-Version  客户端归属（官方桌面端用 WorkBuddy）
/// X-Product: SaaS
/// ```
///
/// **「没有」也要显式说** —— 这是这套协议里最容易漏的：
/// 缺 `X-User-Id` 和给 `X-No-User-Id: 1` 是两回事，前者会被当成异常请求。
///
/// ## 什么头不能乱加
///
/// `X-IDE-*` 那一族**只属于 chat 和探活** —— 刷新 token、拉模型目录、
/// 查计费都不该带。上游会按这些头做用量归属，加错地方会把账算乱。
/// 所以这里分成三个方法，各自管自己那套。
/// </summary>
public static class UpstreamHeaders
{
    /// <summary>所有请求共用的基础头。</summary>
    private static void AddCommon(HttpRequestMessage request, UpstreamVariant variant)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", variant.IsInternational ? "en-US,en;q=0.9" : "zh-CN,zh;q=0.9");
    }

    /// <summary>
    /// 聊天请求的头。
    ///
    /// @param userAgent 桌面端身份 UA。传 null 用 CLI 形态（部分账号上桌面形态更宽松）。
    /// @param clientVersion `X-IDE-Version` 的值，跟 userAgent 必须是同一个身份。
    /// </summary>
    public static void ApplyChat(
        HttpRequestMessage request,
        Credential credential,
        UpstreamVariant variant,
        string? userAgent,
        string? clientVersion)
    {
        AddCommon(request, variant);

        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");

        ApplyIdentity(request, credential);

        // IDE 归属族 —— 只在 chat 上出现
        request.Headers.TryAddWithoutValidation("X-IDE-Type", "WorkBuddy");
        request.Headers.TryAddWithoutValidation("X-IDE-Name", "WorkBuddy");
        if (!string.IsNullOrEmpty(clientVersion))
        {
            request.Headers.TryAddWithoutValidation("X-IDE-Version", clientVersion);
        }
        request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
    }

    /// <summary>
    /// 刷新 token 的头。
    ///
    /// `X-Refresh-Token` 只在这里出现 —— 它是刷新专用凭据，混进别的请求
    /// 等于把长期凭据多暴露几个地方。
    /// </summary>
    public static void ApplyRefresh(HttpRequestMessage request, Credential credential, UpstreamVariant variant)
    {
        AddCommon(request, variant);
        request.Headers.TryAddWithoutValidation("X-Refresh-Token", credential.RefreshToken);
        request.Headers.TryAddWithoutValidation("X-Auth-Refresh-Source", "workbuddy");
        if (!string.IsNullOrEmpty(credential.EnterpriseId))
        {
            request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
        }
    }

    /// <summary>计费/账号类请求的头。只有 Authorization，不带 IDE 归属族。</summary>
    public static void ApplyBilling(HttpRequestMessage request, Credential credential)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
        request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
        if (!string.IsNullOrEmpty(credential.EnterpriseId))
        {
            request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
        }
    }

    /// <summary>
    /// 身份三元组。**空值必须显式声明**，不能省略。
    ///
    /// 这三个 if/else 是这套协议里最不能省的地方 —— 省略 = 上游不知道你是谁，
    /// 表现是「偶发 403」而不是个清晰的错误。
    /// </summary>
    private static void ApplyIdentity(HttpRequestMessage request, Credential credential)
    {
        if (string.IsNullOrEmpty(credential.Uid))
        {
            request.Headers.TryAddWithoutValidation("X-No-User-Id", "1");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-User-Id", credential.Uid);
        }

        if (string.IsNullOrEmpty(credential.EnterpriseId))
        {
            request.Headers.TryAddWithoutValidation("X-No-Enterprise-Id", "1");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
        }

        if (string.IsNullOrEmpty(credential.Domain))
        {
            request.Headers.TryAddWithoutValidation("X-No-Department-Info", "1");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-Domain", credential.Domain);
        }
    }
}

/// <summary>
/// 「这次失败怪谁」—— **环境**还是**账号/上游**。
///
/// ## 为什么必须分开（t79 的真缺陷）
///
/// 本机系统代理没生效时，**所有**请求都是连接层失败（`status == 0`）。
/// 如果这类失败也计入账号的连续失败数，会发生：
///
/// ```
/// 8 个健康账号 → 每个被记 5 次连接失败 → 全部冷冻
/// → 之后所有请求返回 429「没有可用账号」
/// → 界面上看不出是环境问题，看起来像"账号全坏了"
/// ```
///
/// **几十秒就能把一整池好账号冻掉，而且不可自愈**（冷冻要手动解）。
///
/// ## 判据：`status == 0` 是传输失败
///
/// `<see cref="UpstreamClient"/>` 沿用了旧版网关的约定：
/// **连不上时 `Status = 0`**（见 `UpstreamResult.Status` 的注释）。
/// 那个 0 不是 HTTP 状态码，是一个哨兵值 —— 用来区分
/// 「连不上」和「连上了但被拒」，因为**这两件事对用户完全不同**。
///
/// ## 为什么放在这里而不是账号池里
///
/// 它是**关于上游协议的知识**（0 代表什么、哪些算传输层），
/// 而账号池只该关心「该不该惩罚这个号」。
/// 放在协议层，网关、账号池、探活都能引用同一个判据 —— 各写一份就会出现
/// 「账号池认为 0 是环境问题、网关认为 0 是 server 错误」这种不一致。
/// </summary>
public static class TransportFailure
{
    /// <summary>
    /// 这次失败是不是**连接层**的（DNS / 连接被拒 / TLS / 超时）。
    ///
    /// 判据就一条：`status == 0`。
    /// **不要**用 `Kind == Server` 去判 —— 真实的 5xx（上游返回了响应体）
    /// 也是 `Server`，而那一类**应该**惩罚账号（那个号可能真有问题）。
    /// 这正是 t79 缺陷的成因：两者被归成了同一个 Kind。
    /// </summary>
    public static bool IsTransport(int status) => status == 0;

    /// <summary>
    /// 这次失败该不该记到账号头上（累计连续失败、可能冷冻）。
    ///
    /// ```
    /// 传输失败（status == 0）  → false  环境问题，不惩罚账号
    /// 上游 5xx（有响应体）      → true   可能是这个号/这条链路的问题
    /// 限流                      → 由 MarkRateLimited 单独处理（它按模型冷却）
    /// ```
    ///
    /// @param status 上游状态码。0 = 连接层失败。
    /// @param kind   错误分类。**仅用于区分限流**，不用于判断传输层 ——
    ///               因为限流和 5xx 的 `Kind` 是分得开的，而传输层与 5xx 的
    ///               `Kind` **分不开**（都是 `Server`）。所以传输层只看 status。
    /// </summary>
    public static bool ShouldCountAgainstAccount(int status, string? kind)
    {
        // 限流不走这条路径（它有自己的 MarkRateLimited），但真走到这里
        // 也不该当"账号坏了"记 —— 限流是**临时**的，不是账号质量问题。
        if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited) return false;

        // ★ 核心：连接层失败是环境问题
        if (IsTransport(status)) return false;

        return true;
    }
}

/// <summary>
/// 请求体改写管线。
///
/// ## 为什么要在转发前改客户端的请求
///
/// 上游**只支持流式**，而且对请求体有几条硬要求。客户端发来的却是各种
/// 形态（有的要非流式、有的用 `developer` 角色、`tool_choice` 写法五花八门）。
/// 所以每次转发都要过一遍这个管线：
///
/// 1. **强制 `stream: true`** —— 上游非流式直接拒。网关拿到流之后再自己
///    拼成完整响应（见 `ChatStreamCollector`）。
/// 2. **`developer` 角色归一成 `system`** —— OpenAI 新规范用 `developer`，
///    上游不认识。
/// 3. **`tool_choice` 归一成字符串形态** —— 上游只认 `"auto"` / `"none"` /
///    函数名，不认 OpenAI 的对象形态 `{"type":"function","function":{"name":"x"}}`。
///    而且 `none` 还要**把 tools 一起删掉**（留着 tools + none 上游会困惑）。
/// 4. **国际版首条消息必须是 system** —— 缺就补一条。
///
/// ## 为什么用 JsonNode 而不是反序列化成模型
///
/// 客户端的请求体是**任意 OpenAI 兼容 JSON**，字段比我们认识的多得多
/// （各家客户端都有自己的扩展字段）。反序列化成强类型模型会丢掉未知字段，
/// 那些字段上游可能是要的。所以用可变的 DOM 改，只碰我们确切知道的那几个。
/// </summary>
public static class RequestPipeline
{
    /// <summary>国际版缺 system 时补的那句。</summary>
    private const string InternationalSystemPrompt =
        "You are a helpful assistant.";

    /// <summary>
    /// 按变体准备请求体。
    ///
    /// ## `model` 参数的三态契约（重要）
    ///
    /// | 传入 | 行为 |
    /// |---|---|
    /// | `null`（默认） | **完全不碰** body 里的 `model` 字段 —— 保持调用方原样 |
    /// | 非空字符串 | 把 `body.model` 改写成该值 |
    /// | 空字符串 `""` | 同 `null`（视为「没有指定」，不是「把模型删掉」） |
    ///
    /// **为什么 `null` 必须保持原样**：<see cref="Prepare"/> 是 Core 的公开 API，
    /// 调用方不一定都想改模型 —— 有的只想做 stream / 角色 / tool_choice 归一。
    /// 如果 `null` 被当成「清空模型」，那些调用方会静默地把模型删掉，
    /// 而**编译期完全看不出来**（可选参数，不传就是 null）。
    ///
    /// **为什么要有这个参数**：网关会把客户端写死的别名（`gpt-4o`）落到
    /// 自己能用的模型（`hy3`）。如果只落「选号」不落「请求体」，就会出现
    /// 两个模型名 —— 上游收到它不认识的别名，而冷却记账却按 `hy3` 记，
    /// 结果是「限了 A 模型却把 B 模型拉黑」（HANDOVER 坑 4 的配额浪费）。
    /// 把改写并进管线，是因为这本来就是管线该做的事。
    ///
    /// @returns 改写后的 JSON 文本。解析失败时**原样返回** —— 宁可让上游
    ///          报错，也不要把客户端的东西改坏。
    /// </summary>
    public static string Prepare(UpstreamVariant variant, string source, string? model = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(source);
        }
        catch (JsonException)
        {
            return source;
        }

        if (node is not JsonObject body) return source;

        // ⚠ 只有**明确给了非空**模型时才改写。
        // null / "" = 调用方不想管模型，body 里原来是什么就留什么。
        if (!string.IsNullOrEmpty(model))
        {
            body["model"] = model;
        }

        body["stream"] = true;
        NormalizeDeveloperRole(body);
        NormalizeToolChoice(body);

        if (variant.IsInternational)
        {
            EnsureSystemFirst(body);
        }

        return body.ToJsonString();
    }

    /// <summary>OpenAI 新规范用 `developer`，上游只认 `system`。</summary>
    private static void NormalizeDeveloperRole(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return;

        foreach (var message in messages)
        {
            if (message is not JsonObject obj) continue;
            if (obj["role"]?.GetValue<string>() == "developer")
            {
                obj["role"] = "system";
            }
        }
    }

    /// <summary>
    /// `tool_choice` 的各种写法归一。
    ///
    /// 三种情况：
    ///   · 字符串 "none"        → 删掉 tool_choice **和** tools
    ///   · 对象 {"type":"none"} → 同上
    ///   · 对象 {"type":"function","function":{"name":"x"}} → 改成字符串 "x"
    ///   · "auto" / "required"  → 原样（上游认这两个字符串）
    /// </summary>
    private static void NormalizeToolChoice(JsonObject body)
    {
        if (!body.ContainsKey("tool_choice")) return;

        var choice = body["tool_choice"];

        // 字符串形态
        if (choice is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (string.Equals(text.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                body.Remove("tool_choice");
                SuppressTools(body);
            }
            return;
        }

        // 对象形态
        if (choice is JsonObject obj)
        {
            var type = obj["type"] is JsonValue t && t.TryGetValue<string>(out var ts)
                ? ts.Trim().ToLowerInvariant()
                : "";

            switch (type)
            {
                case "none":
                    body.Remove("tool_choice");
                    SuppressTools(body);
                    return;

                case "auto":
                case "required":
                    body["tool_choice"] = type;
                    return;

                case "function":
                    var name = ExtractFunctionName(obj);
                    body["tool_choice"] = string.IsNullOrEmpty(name) ? "auto" : name;
                    return;

                default:
                    body.Remove("tool_choice");
                    return;
            }
        }

        // 认不出的形态（数字、数组…）—— 删掉比留着强
        body.Remove("tool_choice");
    }

    private static string ExtractFunctionName(JsonObject choice)
    {
        // 两种写法都认：{function:{name}} 和 {name}
        if (choice["function"] is JsonObject fn && fn["name"] is JsonValue n1 && n1.TryGetValue<string>(out var s1))
        {
            return s1.Trim();
        }
        if (choice["name"] is JsonValue n2 && n2.TryGetValue<string>(out var s2))
        {
            return s2.Trim();
        }
        return "";
    }

    /// <summary>`tool_choice: "none"` 时要把 tools 也删掉。</summary>
    private static void SuppressTools(JsonObject body)
    {
        body.Remove("tools");
        body.Remove("functions");
    }

    /// <summary>国际版要求首条消息是 system。缺就**前置**一条（不合并、不替换）。</summary>
    private static void EnsureSystemFirst(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return;
        if (messages.Count > 0 && messages[0] is JsonObject first && first["role"]?.GetValue<string>() == "system")
        {
            return;
        }

        messages.Insert(0, new JsonObject
        {
            ["role"] = "system",
            ["content"] = InternationalSystemPrompt,
        });
    }
}
