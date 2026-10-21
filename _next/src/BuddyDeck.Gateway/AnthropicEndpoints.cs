using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// Anthropic Messages 协议翻译层 —— 让 Claude Code / CC Switch 能接上本网关。
///
/// ## 为什么需要它
///
/// Claude Code 只会说 Anthropic 协议（`POST /v1/messages`），而上游是
/// OpenAI Chat 形状。把 CC Switch 的 `ANTHROPIC_BASE_URL` 指到本网关时，
/// 请求就是 Anthropic 形状 —— 没有这一层，**用 Claude Code 的人根本接不上**。
///
/// ## 挂载方式：与 <see cref="AdminEndpoints"/> 同一套「就地接管」
///
/// 暴露 <see cref="TryHandleAsync"/>，由 `GatewayApp` 在**鉴权之后**调一次；
/// 不是本端点族前缀的请求原样返回 false，网关继续走它自己的分派。
///
/// **为什么用这个形状而不是在 `HandleAsync` 里加一段 if**：
/// 那是单点文件的由来 —— 每条协议线都要去改同一个方法，谁都没法并行。
/// 就地接管让「新增一条协议」= 新文件 + 一行注册（见 docs/endpoint-registration.md）。
///
/// ## ⚠ 最容易漏、漏了整件事就废了的一点
///
/// Claude Code 会在 system 里塞一行客户端指纹（`x-anthropic-…header:`），
/// 上游拿它做渠道识别，**命中就回 HTTP 400 / code 11128
/// 「Illegal API invocation from an unapproved channel」**。
///
/// 所以 <see cref="StripClientFingerprint"/> 是**能不能用**的前提，
/// 不是锦上添花 —— 翻译写得再对，不删这一行 Claude Code 依然连不上。
/// 它不影响提示词语义（纯粹是遥测元数据），见 anthropic.js:41-55 的实测说明。
///
/// 参考实现：`D:\workfandaiapi\workbuddy_api\src\anthropic.js`（只读，抄设计不抄代码）。
/// </summary>
public sealed partial class AnthropicEndpoints : IEndpointFamily
{
    private readonly UpstreamClient _upstream;
    private readonly AccountPool _pool;
    private readonly ICredentialSource _credentials;
    private readonly ModelCatalog _catalog;
    private readonly RequestLog? _requestLog;
    private readonly ILogger<AnthropicEndpoints> _log;

    public AnthropicEndpoints(
        UpstreamClient upstream,
        AccountPool pool,
        ICredentialSource credentials,
        ModelCatalog catalog,
        RequestLog? requestLog,
        ILogger<AnthropicEndpoints> log)
    {
        _upstream = upstream;
        _pool = pool;
        _credentials = credentials;
        _catalog = catalog;
        _requestLog = requestLog;
        _log = log;
    }

    /// <summary>本端点族处理前缀。</summary>
    public const string Prefix = "/v1/messages";

    /// <summary>
    /// 是不是本端点族的请求。是的话就地处理并返回 true。
    ///
    /// 由 `GatewayApp` 在**鉴权之后**调用（走到这里时 Host 检查与 Bearer 校验都过了）。
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        // /v1/messages 与 /v1/messages/count_tokens
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (path.Length > Prefix.Length && path[Prefix.Length] != '/') return false;

        if (context.Request.Method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 405, "invalid_request_error",
                $"Anthropic Messages 只支持 POST，收到 {context.Request.Method}").ConfigureAwait(false);
            return true;
        }

        if (path == Prefix)
        {
            await MessagesAsync(context).ConfigureAwait(false);
            return true;
        }

        if (path == Prefix + "/count_tokens")
        {
            await CountTokensAsync(context).ConfigureAwait(false);
            return true;
        }

        await WriteErrorAsync(context, 404, "not_found",
            $"no route for POST {path}").ConfigureAwait(false);
        return true;
    }

    // ══ 主端点：POST /v1/messages ═════════════════════════════

    private async Task MessagesAsync(HttpContext context)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var requestedModel = body["model"]?.GetValue<string>();
        var resolvedModel = _catalog.Resolve(requestedModel);
        var wantsStream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;

        var entries = _credentials.Load();
        if (entries.Count == 0)
        {
            await WriteErrorAsync(context, 401, "authentication_error",
                "保险库里没有账号 —— 先在面板里导入一个 WorkBuddy 账号").ConfigureAwait(false);
            return;
        }

        _pool.Replace(entries);

        // 翻成 OpenAI chat 请求体
        var chat = TranslateRequest(body, resolvedModel);

        // 走与 /v1/chat/completions 完全相同的管线与重试逻辑：
        // 强制 stream:true（上游只支持流式）+ 模型改写。
        var prepared = RequestPipeline.Prepare(UpstreamVariant.China, chat.ToJsonString(), resolvedModel);

        var tried = new List<string>();
        UpstreamResult? lastResult = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var member = _pool.Pick(resolvedModel, tried);
            if (member is null)
            {
                await WriteNoAccountAsync(context, resolvedModel, lastResult).ConfigureAwait(false);
                return;
            }

            UpstreamResult result;
            try
            {
                result = await _upstream
                    .ChatStreamAsync(member.Entry.ToCredential(), prepared, null, null, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;   // 用户取消：什么都不用回
            }

            if (result.Ok && result.Response is not null)
            {
                using var response = result.Response;

                if (wantsStream)
                {
                    await StreamAsync(context, response, resolvedModel).ConfigureAwait(false);
                }
                else
                {
                    await CollectAsync(context, response, resolvedModel).ConfigureAwait(false);
                }
                return;
            }

            var kind = result.Kind ?? UpstreamErrorKind.Unknown;
            if (!IsRetryable(kind))
            {
                await WriteUpstreamErrorAsync(context, result).ConfigureAwait(false);
                return;
            }

            // 可重试：冷却这个号（**按实际发出去的模型记**，与 t11 的教训一致）
            if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited)
            {
                _pool.MarkRateLimited(member.Id, resolvedModel, null);
            }
            else
            {
                // ⚠ 传 result.Status + kind，不要用无条件记账的旧重载 ——
                //   否则连接层失败（status == 0）会把健康账号记到冷冻。
                //   真机实测过：代理没生效 → 8 个号全冻 → 一律 429。
                _pool.MarkFailure(member.Id, resolvedModel, result.Status, result.Kind);
            }

            tried.Add(member.Id);
            lastResult = result;
        }

        await WriteNoAccountAsync(context, resolvedModel, lastResult).ConfigureAwait(false);
    }

    /// <summary>一次客户端请求内最多试几个号（与 /v1/chat/completions 的默认值保持一致）。</summary>
    private const int MaxAttempts = 3;

    private static bool IsRetryable(string kind) => kind switch
    {
        UpstreamErrorKind.RateLimited => true,
        UpstreamErrorKind.ModelRateLimited => true,
        UpstreamErrorKind.Server => true,
        _ => false,
    };

    // ══ 非流式：收完整条流再回翻 ═════════════════════════════

    private static async Task CollectAsync(HttpContext context, HttpResponseMessage upstream, string model)
    {
        var collector = new ChatStreamCollector();

        await using var stream = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
        await foreach (var payload in SseReader.ReadDataAsync(stream, context.RequestAborted).ConfigureAwait(false))
        {
            var chunk = SseSanitizer.Process(payload);
            if (chunk.IsDone) break;
            collector.Feed(chunk);
        }

        var completion = collector.Build(model, NewId("msg"));
        var message = TranslateResponse(completion, model);

        await WriteJsonAsync(context, 200, message).ConfigureAwait(false);
    }

    // ══ 流式：按 Anthropic 命名事件语义输出 ═══════════════════

    /// <summary>
    /// 把上游的匿名 data-only SSE 转成 Anthropic 的命名事件流。
    ///
    /// ## 契约要点（Claude Code 严格按这个解析，错一个就卡住）
    ///
    /// ```
    /// message_start                必须第一个、只发一次
    ///   content_block_start        每个 block 一段
    ///     content_block_delta      可多段
    ///   content_block_stop
    ///   …（text / tool_use 共用一套从 0 开始的连续 index）
    /// message_delta                带 stop_reason
    /// message_stop
    /// ```
    ///
    /// **与 OpenAI 的根本差别**：OpenAI 是 `data: {choices:[{delta}]}` 匿名流；
    /// Anthropic 是 `event: <名字>` + `data: {...}` 的**命名**事件。所以不能
    /// 简单转发 —— 必须自己维护「当前开着哪个 block、index 到几了」。
    /// </summary>
    private async Task StreamAsync(HttpContext context, HttpResponseMessage upstream, string model)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var state = new StreamState(model);

        try
        {
            await using var stream = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);

            await foreach (var payload in SseReader.ReadDataAsync(stream, context.RequestAborted).ConfigureAwait(false))
            {
                var chunk = SseSanitizer.Process(payload);
                if (chunk.IsDone) break;

                // ⚠ 每一片都要**边收边发**：push 里直接写 + flush。
                // 不能先攒完再发 —— 那就把流式抹掉了（同 t1 的 StreamAsync）。
                await state.PushAsync(context, chunk.Raw).ConfigureAwait(false);
            }

            await state.FinishAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端走了：什么都别写
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) LogStreamFailed(ex);
            await state.FailAsync(context, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 流式翻译器的状态机。
    ///
    /// 单独一个类型是因为它必须**跨分片持有状态**（当前开着的 block、已分配的 index、
    /// tool_use 的 index→block 映射、累计 usage）。写成一堆局部变量会很难读，
    /// 而这里的 index 管理一旦错位，Claude Code 的解析就乱了。
    /// </summary>
    private sealed class StreamState
    {
        private readonly string _messageId = NewId("msg");
        private readonly string _model;
        private readonly Dictionary<int, int> _toolBlocks = [];

        private int _blockIndex = -1;
        private int? _openIndex;
        private string? _openType;
        private bool _started;
        private bool _finished;
        private string? _stopReason;
        private long _outputTokens;

        public StreamState(string model)
        {
            _model = model;
            _messageId = NewId("msg");
        }

        public async Task PushAsync(HttpContext context, string? raw)
        {
            if (_finished || string.IsNullOrEmpty(raw)) return;

            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch (System.Text.Json.JsonException) { return; }
            if (node is not JsonObject chunk) return;

            if (chunk["usage"] is JsonObject usage && usage["completion_tokens"] is JsonValue cv
                && cv.TryGetValue<long>(out var ct))
            {
                _outputTokens = ct;
            }

            if (chunk["choices"] is not JsonArray choices || choices.Count == 0) return;
            if (choices[0] is not JsonObject choice) return;

            var delta = choice["delta"] as JsonObject ?? [];

            await StartMessageAsync(context).ConfigureAwait(false);

            // ── reasoning_content → thinking block ─────────────
            var reasoning = delta["reasoning_content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(reasoning))
            {
                if (_openType != "thinking") await OpenBlockAsync(context, "thinking", new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = "",
                }).ConfigureAwait(false);
                await DeltaAsync(context, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = reasoning })
                    .ConfigureAwait(false);
            }

            // ── content → text block ───────────────────────────
            var text = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
            {
                if (_openType != "text") await OpenBlockAsync(context, "text", new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "",
                }).ConfigureAwait(false);
                await DeltaAsync(context, new JsonObject { ["type"] = "text_delta", ["text"] = text })
                    .ConfigureAwait(false);
            }

            // ── tool_calls → tool_use block ────────────────────
            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls)
                {
                    if (call is not JsonObject tc) continue;

                    var key = 0;
                    if (tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx)) key = idx;

                    if (!_toolBlocks.TryGetValue(key, out var blockIndex))
                    {
                        var fn = tc["function"] as JsonObject ?? [];
                        var id = tc["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(id)) id = NewId("toolu");

                        blockIndex = await OpenBlockAsync(context, "tool_use", new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = id,
                            ["name"] = fn["name"]?.GetValue<string>() ?? "",
                            ["input"] = new JsonObject(),
                        }).ConfigureAwait(false);

                        _toolBlocks[key] = blockIndex;
                    }

                    var args = (tc["function"] as JsonObject)?["arguments"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(args))
                    {
                        await WriteEventAsync(context, "content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = blockIndex,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "input_json_delta",
                                ["partial_json"] = args,
                            },
                        }).ConfigureAwait(false);
                    }
                }
            }

            var finish = choice["finish_reason"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(finish)) _stopReason = ToAnthropicStopReason(finish);
        }

        /// <summary>
        /// 收尾。**必须调用** —— 不调的话 Claude Code 会一直等下去。
        ///
        /// ⚠ 有一条实测得来的修正：有些模型发了 tool_calls 却把 finish_reason
        /// 报成 `stop`。而 Claude Code **靠 stop_reason 决定要不要执行工具** ——
        /// 报成 end_turn 的话工具调用就被静默丢弃了。所以这里以**实际收到的内容**
        /// 为准：只要开过 tool_use block，就报 tool_use。
        /// </summary>
        public async Task FinishAsync(HttpContext context)
        {
            if (_finished) return;
            _finished = true;

            await StartMessageAsync(context).ConfigureAwait(false);
            await CloseBlockAsync(context).ConfigureAwait(false);

            var reason = _stopReason;
            if (_toolBlocks.Count > 0 && (reason is null || reason == "end_turn")) reason = "tool_use";
            reason ??= "end_turn";

            await WriteEventAsync(context, "message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject
                {
                    ["stop_reason"] = reason,
                    ["stop_sequence"] = null,
                },
                ["usage"] = new JsonObject { ["output_tokens"] = _outputTokens },
            }).ConfigureAwait(false);

            await WriteEventAsync(context, "message_stop", new JsonObject { ["type"] = "message_stop" })
                .ConfigureAwait(false);
        }

        public async Task FailAsync(HttpContext context, string message)
        {
            if (_finished) return;
            _finished = true;

            await CloseBlockAsync(context).ConfigureAwait(false);
            await WriteEventAsync(context, "error", new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject
                {
                    ["type"] = "api_error",
                    ["message"] = message,
                },
            }).ConfigureAwait(false);
        }

        private async Task StartMessageAsync(HttpContext context)
        {
            if (_started) return;
            _started = true;

            await WriteEventAsync(context, "message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = _messageId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = _model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                },
            }).ConfigureAwait(false);
        }

        private async Task<int> OpenBlockAsync(HttpContext context, string type, JsonObject block)
        {
            await CloseBlockAsync(context).ConfigureAwait(false);

            _blockIndex++;
            _openIndex = _blockIndex;
            _openType = type;

            await WriteEventAsync(context, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _blockIndex,
                ["content_block"] = block,
            }).ConfigureAwait(false);

            return _blockIndex;
        }

        private async Task CloseBlockAsync(HttpContext context)
        {
            if (_openIndex is null) return;

            await WriteEventAsync(context, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = _openIndex.Value,
            }).ConfigureAwait(false);

            _openIndex = null;
            _openType = null;
        }

        private Task DeltaAsync(HttpContext context, JsonObject payload)
            => WriteEventAsync(context, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = _openIndex ?? 0,
                ["delta"] = payload,
            });
    }

    /// <summary>写一个 Anthropic 命名事件并立刻 flush（不 flush 就不是流式）。</summary>
    private static async Task WriteEventAsync(HttpContext context, string name, JsonNode payload)
    {
        var text = $"event: {name}\ndata: {payload.ToJsonString(RelaxedJson)}\n\n";
        await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ══ count_tokens ══════════════════════════════════════════

    /// <summary>
    /// `POST /v1/messages/count_tokens` —— Claude Code 的上下文管理会用。
    ///
    /// 没有上游分词器，只能粗估。估法照旧版 `protocol.js:estimateTokens`：
    /// 英文约 3 字符 1 token、中日文约 1 字符 1 token（宽字符按码点 > 0x2e80 判）。
    /// 偏保守 —— 估多了会让 Claude Code 提前压缩上下文，估少了会超限被上游拒。
    /// </summary>
    private static async Task CountTokensAsync(HttpContext context)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var total = EstimateTokens(SystemText(body["system"]));

        if (body["messages"] is JsonArray messages)
        {
            foreach (var message in messages)
            {
                if (message is JsonObject m) total += EstimateTokens(m["content"]);
            }
        }

        if (body["tools"] is JsonArray tools) total += EstimateTokens(tools);

        await WriteJsonAsync(context, 200, new JsonObject { ["input_tokens"] = total }).ConfigureAwait(false);
    }

    /// <summary>
    /// 粗估 token 数。宽字符（CJK 等）逐字算 1，窄字符每 3 个算 1。
    ///
    /// @param value 字符串按原样算；其它 JSON 按序列化后的文本算（与旧版一致）。
    /// </summary>
    internal static long EstimateTokens(JsonNode? value)
    {
        if (value is null) return 1;

        var text = value is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : value.ToJsonString();

        long wide = 0;
        foreach (var ch in text)
        {
            if (char.ConvertToUtf32(text, text.IndexOf(ch)) > 0x2e80) wide++;
        }

        var narrow = text.Length - wide;
        return Math.Max(1, (long)Math.Ceiling(narrow / 3.0) + wide);
    }

    // ══ 请求翻译 ══════════════════════════════════════════════

    /// <summary>
    /// Anthropic `/v1/messages` 请求体 → OpenAI Chat Completions 请求体。
    ///
    /// ## 三处容易翻错的映射
    ///
    /// 1. **system 抽离**：Anthropic 把 system 放在**顶层字段**（字符串或 text block 数组），
    ///    OpenAI 把它当成一条 `role:"system"` 消息，且必须在最前面。
    /// 2. **tool_result 要挪到 role:"tool"**：Anthropic 把工具结果塞在 **user 消息的
    ///    content block 里**，而 OpenAI 要求独立的 `role:"tool"` 消息，**且必须紧跟在
    ///    带 tool_calls 的 assistant 之后** —— 顺序错了上游会拒。
    /// 3. **tool_choice 的 `any` → `required`**：Anthropic 的 `any` 在 OpenAI 里叫
    ///    `required`，名字不一样但语义相同。
    /// </summary>
    internal static JsonObject TranslateRequest(JsonObject body, string model)
    {
        var messages = new JsonArray();

        var systemText = SystemText(body["system"]);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });
        }

        if (body["messages"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item is not JsonObject message) continue;

                var role = message["role"]?.GetValue<string>();

                if (role == "assistant")
                {
                    AppendAssistant(messages, message);
                    continue;
                }

                AppendUser(messages, message);
            }
        }

        var chat = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st,
        };

        // max_tokens / temperature / top_p 直传（名字一致）
        //
        // ⚠ 不能用 `mt.TryGetValue<long>` —— `TryGetValue<T>` 要求底层类型**精确匹配**，
        // 而 JSON 里的数值按最窄类型背书：`4096` 是 int、`0.7` 可能是 int/double/decimal。
        // 直接 TryGetValue<long>/<double> 拿到的是 false（不抛异常），后果是
        // **这些参数被静默丢弃**：客户端设了 max_tokens 却没生效，而且没有任何报错。
        //
        // 这与 observability 在 UsageLedger 里踩到的是同一个坑（`{"prompt_tokens": 2541}`
        // 的 TryGetValue<long> 返回 false，导致所有 token 抽成 null）。修法同源：逐类型试。
        if (body["max_tokens"] is JsonValue mt && TryInt(mt, out var maxTokens))
        {
            chat["max_tokens"] = maxTokens;
        }
        if (body["temperature"] is JsonValue tv && TryFloat(tv, out var temperature))
        {
            chat["temperature"] = temperature;
        }
        if (body["top_p"] is JsonValue pv && TryFloat(pv, out var topP))
        {
            chat["top_p"] = topP;
        }
        // stop_sequences → stop
        if (body["stop_sequences"] is JsonArray stops && stops.Count > 0)
        {
            chat["stop"] = stops.DeepClone();
        }

        var tools = TranslateTools(body["tools"]);
        if (tools is not null) chat["tools"] = tools;

        var choice = TranslateToolChoice(body["tool_choice"]);
        if (choice is not null) chat["tool_choice"] = choice;

        return chat;
    }

    /// <summary>
    /// 从 <see cref="JsonValue"/> 里抽一个整数，**逐类型试**（int → long → double → 字符串）。
    ///
    /// ⚠ 为什么不能直接 `value.TryGetValue&lt;long&gt;(out var n)`：
    /// `TryGetValue&lt;T&gt;` 要求底层类型**精确匹配**。JSON 里的 `4096` 由
    /// <see cref="JsonValue"/> 按最窄类型背书（int），于是 `TryGetValue&lt;long&gt;`
    /// 返回 <c>false</c> —— **不抛异常**。后果是参数被静默丢弃，
    /// 客户端设了却不生效，而日志里没有任何异常。
    ///
    /// 这与 observability 在 <c>UsageLedger</c> 踩到的是同一个坑
    /// （`{"prompt_tokens": 2541}` 的 `TryGetValue&lt;long&gt;` 返回 false，
    /// 导致所有 token/credit 抽成 null、聚合全为 0）。修法同源。
    /// </summary>
    private static bool TryInt(JsonValue value, out long result)
    {
        result = 0;
        if (value.TryGetValue<long>(out result)) return true;
        if (value.TryGetValue<int>(out var i)) { result = i; return true; }
        if (value.TryGetValue<double>(out var d))
        {
            result = (long)d;
            return true;
        }
        if (value.TryGetValue<decimal>(out var m))
        {
            result = (long)m;
            return true;
        }
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    /// <summary>同上，抽一个浮点（采样参数用）。</summary>
    private static bool TryFloat(JsonValue value, out double result)
    {
        result = 0;
        if (value.TryGetValue<double>(out result)) return true;
        if (value.TryGetValue<long>(out var l)) { result = l; return true; }
        if (value.TryGetValue<int>(out var i)) { result = i; return true; }
        if (value.TryGetValue<decimal>(out var m)) { result = (double)m; return true; }
        if (value.TryGetValue<string>(out var s)
            && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    private static void AppendAssistant(JsonArray messages, JsonObject message)
    {
        var blocks = ToBlocks(message["content"]);

        var text = new StringBuilder();
        var toolCalls = new JsonArray();

        foreach (var block in blocks)
        {
            var type = block["type"]?.GetValue<string>();
            if (type == "text")
            {
                text.Append(block["text"]?.GetValue<string>() ?? "");
            }
            else if (type == "tool_use")
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = block["id"]?.GetValue<string>() is { Length: > 0 } id ? id : NewId("call"),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = block["name"]?.GetValue<string>() ?? "",
                        // ⚠ OpenAI 的 arguments 是**字符串**（JSON 序列化过的），
                        // 而 Anthropic 的 input 是**对象**。这里必须序列化。
                        ["arguments"] = (block["input"] as JsonObject ?? new JsonObject()).ToJsonString(),
                    },
                });
            }
        }

        var converted = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.ToString(),
        };

        if (toolCalls.Count > 0) converted["tool_calls"] = toolCalls;

        messages.Add(converted);
    }

    private static void AppendUser(JsonArray messages, JsonObject message)
    {
        var blocks = ToBlocks(message["content"]);

        // ⚠ 先放 tool 消息：OpenAI 要求它们紧跟在带 tool_calls 的 assistant 之后。
        // 夹在普通 user 消息后面的话，上游会认为 tool_call_id 无对应调用。
        foreach (var block in blocks)
        {
            if (block["type"]?.GetValue<string>() != "tool_result") continue;

            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = block["tool_use_id"]?.GetValue<string>() ?? "",
                ["content"] = ToolResultText(block["content"]),
            });
        }

        var parts = new JsonArray();
        foreach (var block in blocks)
        {
            if (block["type"]?.GetValue<string>() == "tool_result") continue;
            var part = ToChatPart(block);
            if (part is not null) parts.Add(part);
        }

        if (parts.Count == 1 && parts[0]!["type"]!.GetValue<string>() == "text")
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = parts[0]!["text"]!.GetValue<string>(),
            });
        }
        else if (parts.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }
        else if (blocks.Count == 0)
        {
            // ⚠ 空消息要保住「用户说了话」这个轮次，但内容给空串 ——
            // 直接跳过会让上游看到 two assistant 连着，那更糟。
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "" });
        }
    }

    /// <summary>Anthropic 的 block → OpenAI 的 content part。不认识的类型返回 null 丢弃。</summary>
    private static JsonObject? ToChatPart(JsonObject block)
    {
        switch (block["type"]?.GetValue<string>())
        {
            case "text":
                return new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = block["text"]?.GetValue<string>() ?? "",
                };

            case "image":
                if (block["source"] is not JsonObject source) return null;
                var sourceType = source["type"]?.GetValue<string>();

                if (sourceType == "base64" && source["data"]?.GetValue<string>() is { Length: > 0 } data)
                {
                    var media = source["media_type"]?.GetValue<string>() ?? "image/png";
                    return new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{media};base64,{data}" },
                    };
                }
                if (sourceType == "url" && source["url"]?.GetValue<string>() is { Length: > 0 } url)
                {
                    return new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = url },
                    };
                }
                return null;

            default:
                // thinking / document / 未知类型：上游不认，丢掉。
                // 丢掉而不是报错 —— 客户端发来的 block 类型常比我们认识的多，
                // 报错会让整条消息发不出去。
                return null;
        }
    }

    /// <summary>tool_result 的 content 可以是字符串或 block 数组。</summary>
    private static string ToolResultText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray blocks) return "";

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is JsonObject o && o["type"]?.GetValue<string>() == "text")
            {
                parts.Add(o["text"]?.GetValue<string>() ?? "");
            }
        }
        return string.Join("\n", parts);
    }

    private static JsonArray? TranslateTools(JsonNode? tools)
    {
        if (tools is not JsonArray source) return null;

        var converted = new JsonArray();
        foreach (var item in source)
        {
            if (item is not JsonObject tool) continue;

            var name = tool["name"]?.GetValue<string>()?.Trim() ?? "";
            // 服务端工具（如 web_search）没有 input_schema，上游不认 —— 丢掉
            if (name.Length == 0 || tool["input_schema"] is not JsonObject schema) continue;

            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = tool["description"]?.GetValue<string>() ?? "",
                    ["parameters"] = schema.DeepClone(),
                },
            });
        }

        return converted.Count > 0 ? converted : null;
    }

    /// <summary>Anthropic 的 tool_choice → OpenAI 的写法。</summary>
    private static JsonNode? TranslateToolChoice(JsonNode? choice)
    {
        if (choice is not JsonObject obj) return null;

        return obj["type"]?.GetValue<string>() switch
        {
            // ⚠ 名字不同但语义相同：Anthropic 的 any = OpenAI 的 required
            "any" => "required",
            "auto" => "auto",
            "none" => "none",
            "tool" => obj["name"]?.GetValue<string>() is { Length: > 0 } name
                ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name } }
                : "auto",
            _ => null,
        };
    }

    /// <summary>content 可以是字符串，也可以是一组 block。统一成 block 列表。</summary>
    private static List<JsonObject> ToBlocks(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return [new JsonObject { ["type"] = "text", ["text"] = s }];
        }
        if (content is not JsonArray array) return [];

        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item is JsonObject o) list.Add(o);
        }
        return list;
    }

    // ══ 响应翻译 ══════════════════════════════════════════════

    /// <summary>
    /// OpenAI Chat Completions 文档 → Anthropic `type:"message"` 文档。
    ///
    /// 两个容易错的点：
    /// - **reasoning_content → thinking block**（放在最前，与旧版一致）
    /// - **发了 tool_calls 但 finish_reason 是 stop 时要报 `tool_use`** ——
    ///   Claude Code 靠 stop_reason 决定要不要执行工具，报错就静默丢弃工具调用。
    /// </summary>
    internal static JsonObject TranslateResponse(JsonObject completion, string model)
    {
        var choice = (completion["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject ?? [];

        var content = new JsonArray();

        var reasoning = message["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(reasoning))
        {
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning });
        }

        var text = message["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        var callCount = 0;
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                if (call is not JsonObject c) continue;
                callCount++;

                var fn = c["function"] as JsonObject ?? [];
                var raw = fn["arguments"]?.GetValue<string>();

                JsonNode input;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    input = new JsonObject();
                }
                else
                {
                    try
                    {
                        input = JsonNode.Parse(raw!) ?? new JsonObject();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // ⚠ 参数不是合法 JSON：**包一层塞进去，别把整个调用丢了** ——
                        // 丢掉的话 Claude Code 会以为模型没调工具，行为完全变了。
                        input = new JsonObject { ["_raw"] = raw };
                    }
                }

                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = c["id"]?.GetValue<string>() is { Length: > 0 } id ? id : NewId("toolu"),
                    ["name"] = fn["name"]?.GetValue<string>() ?? "",
                    ["input"] = input,
                });
            }
        }

        if (content.Count == 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
        }

        var stopReason = ToAnthropicStopReason(choice?["finish_reason"]?.GetValue<string>());
        if (callCount > 0 && (stopReason is null || stopReason == "end_turn")) stopReason = "tool_use";

        var (inputTokens, outputTokens) = UsageOf(completion["usage"]);

        return new JsonObject
        {
            ["id"] = completion["id"]?.GetValue<string>() is { Length: > 0 } mid ? mid : NewId("msg"),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = stopReason ?? "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
            },
        };
    }

    /// <summary>OpenAI 的 finish_reason → Anthropic 的 stop_reason。</summary>
    internal static string? ToAnthropicStopReason(string? finishReason) => finishReason switch
    {
        "tool_calls" or "function_call" => "tool_use",
        "length" => "max_tokens",
        "stop" => "end_turn",
        "content_filter" => "refusal",
        null or "" => null,
        _ => "end_turn",
    };

    /// <summary>usage 缺字段时补齐 —— 客户端对 undefined/null 很敏感。</summary>
    private static (long Input, long Output) UsageOf(JsonNode? usage)
    {
        if (usage is not JsonObject obj) return (0, 0);

        // ⚠ 同样不能只试 long —— 上游回 `{"prompt_tokens": 2541}` 时 2541 是 int 背书，
        // 只试 long 会拿到 false，于是 token 数全部显示 0（与 UsageLedger 那个坑同源）。
        long Get(string key) => obj[key] is JsonValue v && TryInt(v, out var n) ? n : 0;

        return (Get("prompt_tokens"), Get("completion_tokens"));
    }

    // ══ 指纹行剔除（能不能用的前提）═══════════════════════════

    /// <summary>
    /// 客户端指纹行。Claude Code 会在 system 里塞这样一行：
    ///
    /// ```
    /// x-anthropic-billing-header: cc_version=2.1.250.7b8; cc_entrypoint=sdk-cli;
    /// ```
    ///
    /// 上游拿它做渠道识别，**命中就回 HTTP 400 / code 11128**
    /// 「Illegal API invocation from an unapproved channel」。
    ///
    /// 正则与旧版 `anthropic.js:55` 对齐：行首可空白 + `x-anthropic-` +
    /// 任意小写字母数字连字符 + `header` + 冒号。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ClientFingerprintLine =
        new(@"^\s*x-anthropic-[a-z0-9-]*header\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// **断言强度自检开关** —— 置真时跳过指纹剔除，用来证明那一批测试
    /// 真的在验「剔除发生了」，而不是空过。
    ///
    /// ## 为什么留一个开关而不是「临时改代码再改回来」
    ///
    /// 临时改代码有两个问题：① 可能忘了改回来；② 用 `return text;` 短路会触发
    /// CS0162 不可达代码（那就变成编译失败，不是测试失败，验不出东西）。
    ///
    /// 环境变量 `BUDDYDECK_SELFTEST_NO_FINGERPRINT_STRIP=1` 才会打开，
    /// 默认关闭 —— 生产与常规测试路径不受影响。
    ///
    /// 自检方法：
    /// ```powershell
    /// $env:BUDDYDECK_SELFTEST_NO_FINGERPRINT_STRIP="1"
    /// dotnet test --filter "FullyQualifiedName~指纹"
    /// # → 应有多条变红（证明它们真的在验剔除）
    /// ```
    /// </summary>
    internal static bool DisableFingerprintStripForSelfCheck { get; set; } =
        Environment.GetEnvironmentVariable("BUDDYDECK_SELFTEST_NO_FINGERPRINT_STRIP") == "1";

    /// <summary>
    /// 把客户端指纹行从一段文本里剔掉。
    ///
    /// ⚠ **这是 Claude Code 能不能用的前提，不是优化**：
    /// 翻译写得再对，带上这一行上游就回 400/11128，整条链路废掉。
    ///
    /// 实测（旧版逐块逐行二分）：整份 Claude Code 系统提示词里**只有这一行**
    /// 会触发；它纯粹是遥测元数据，不含任何提示词语义，删掉不改变模型收到的指令。
    ///
    /// 不含 `x-anthropic-` 关键字时**原样返回**（快速路径：绝大多数请求走这条）。
    /// </summary>
    internal static string StripClientFingerprint(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (DisableFingerprintStripForSelfCheck) return text;
        if (!text.Contains("x-anthropic-", StringComparison.OrdinalIgnoreCase)) return text;

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (!ClientFingerprintLine.IsMatch(line)) kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>system 字段可以是字符串，也可以是一组 text block。取纯文本并剔指纹。</summary>
    internal static string SystemText(JsonNode? system)
    {
        if (system is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return StripClientFingerprint(s);
        }

        if (system is not JsonArray blocks) return "";

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is JsonObject o && o["type"]?.GetValue<string>() == "text")
            {
                parts.Add(o["text"]?.GetValue<string>() ?? "");
            }
        }

        return StripClientFingerprint(string.Join("\n\n", parts));
    }

    // ══ 输出 ══════════════════════════════════════════════════

    internal static string NewId(string prefix)
        => $"{prefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Guid.NewGuid():N}"[..Math.Min(
            prefix.Length + 1 + 8 + 12, prefix.Length + 1 + 8 + 12)];

    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        var payload = body.ToJsonString(RelaxedJson);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Anthropic 的错误信封 —— **形状与 `/v1/*` 不同**。
    ///
    /// OpenAI 是 `{"error":{"message","type","code"}}`；
    /// Anthropic 是 `{"type":"error","error":{"type","message"}}`。
    /// Claude Code 按后者解析，所以这一族必须用它自己的形状。
    /// </summary>
    private static Task WriteErrorAsync(HttpContext context, int status, string type, string message)
        => WriteJsonAsync(context, status, new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = type,
                ["message"] = string.IsNullOrWhiteSpace(message) ? type : message,
            },
        });

    /// <summary>上游失败 → Anthropic 错误信封（错误类型按上游分类映射）。</summary>
    private static async Task WriteUpstreamErrorAsync(HttpContext context, UpstreamResult result)
    {
        var kind = result.Kind ?? UpstreamErrorKind.Unknown;
        var status = kind switch
        {
            UpstreamErrorKind.Unauthorized => 401,
            UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => 429,
            UpstreamErrorKind.BadRequest => 400,
            _ => 502,
        };

        var type = kind switch
        {
            UpstreamErrorKind.Unauthorized => "authentication_error",
            UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => "rate_limit_error",
            UpstreamErrorKind.BadRequest => "invalid_request_error",
            _ => "api_error",
        };

        var detail = string.IsNullOrWhiteSpace(result.Message) ? "上游没有给出原因" : result.Message!;
        if (detail.Length > 400) detail = detail[..400] + "…";

        await WriteErrorAsync(context, status, type, detail).ConfigureAwait(false);
    }

    /// <summary>号用光了（或全被限流）：回 429，与 /v1/* 的 soft_rate 语义一致。</summary>
    private static async Task WriteNoAccountAsync(HttpContext context, string model, UpstreamResult? lastResult)
    {
        var isServer = lastResult?.Kind == UpstreamErrorKind.Server;
        var status = isServer ? 502 : 429;
        var type = isServer ? "api_error" : "rate_limit_error";

        var message = isServer
            ? $"试了 {MaxAttempts} 个账号，上游都返回服务端错误"
            : $"没有可用账号（{model} 正在冷却中，或者所有号都连续失败被冷冻）";

        await WriteErrorAsync(context, status, type, message).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpContext context)
    {
        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();

        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "request body is not valid JSON").ConfigureAwait(false);
            return null;
        }
    }

    // ══ 日志 ══════════════════════════════════════════════════

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic 流中断")]
    private partial void LogStreamFailed(Exception ex);
}
