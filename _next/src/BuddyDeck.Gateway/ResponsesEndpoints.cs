using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// OpenAI Responses API 翻译层 —— 让 Codex CLI 能接上本网关。
///
/// ## 为什么需要它
///
/// Codex 的 `model_providers.&lt;x&gt;.wire_api` 有两种取值，其中
/// `"responses"` 走的是 **Responses API**（`POST /v1/responses`），
/// 和我们网关原本只提供的 Chat Completions **不是一回事**：
///
/// ```
/// Chat Completions              Responses
/// ──────────────────────        ──────────────────────────
/// messages[] 平铺               instructions + input[]（带 type 的项数组）
/// tools[].function.{…}          tools[] 扁平（name 在顶层）
/// choices[].delta.content       response.output_text.delta
/// finish_reason                 status: completed / incomplete
/// 匿名 data-only SSE            命名事件 SSE + sequence_number
/// ```
///
/// 没有这一层，**用 Codex 的人根本接不上这个网关** —— 正如 M1 之前
/// 用 Claude Code 的人也接不上。
///
/// ## 挂载方式：与 <see cref="AnthropicEndpoints"/> 完全同一套「就地接管」
///
/// 实现 <see cref="IEndpointFamily"/>，暴露 <see cref="TryHandleAsync"/>，
/// 由 `GatewayApp` 在**鉴权之后**依次询问；不是本端点族前缀的请求
/// 原样返回 false（**一个字节都不写**），网关继续走它自己的分派。
///
/// 注册在 `Program.cs` 里排在 Anthropic / Media / Search **之后** ——
/// 本族只认 `/v1/responses` 这一个精确路径，与那三族的路径**无前缀重叠**，
/// 所以排最后不影响任何既有族（理由见注册处注释）。
///
/// ## ⚠ 这个项目里「装配」出过三次事故
///
/// `/admin/*` 写了路由没挂 → 十一个端点全 404；`UsageEndpoints` 类写了没注册
/// → 完整死代码；`AnthropicEndpoints` 接口建了没接线 → 端点不可达。
/// 所以「写完」有**三个层次**：① 类型存在 ② 被装配 ③ 在真实请求路径上被走到。
/// 本文件只保证 ①；② 靠 `Program.cs` 那一行注册；**③ 必须真网关 curl 验**。
///
/// ## 事件语义：难点在 output item 的「开-写-关」顺序
///
/// Codex 按 `output_index` 逐个接收，**index 必须连续、且上一项关掉才能开下一项**。
/// 这正是 <see cref="StreamState"/> 要维护的全部状态。
///
/// 参考实现：`D:\workfandaiapi\workbuddy_api\src\responses.js`（548 行，只读，
/// **抄设计不抄代码**）。逐项对照见本文件末尾的三张清单。
/// </summary>
public sealed partial class ResponsesEndpoints : IEndpointFamily
{
    private readonly UpstreamClient _upstream;
    private readonly AccountPool _pool;
    private readonly ICredentialSource _credentials;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<ResponsesEndpoints> _log;

    public ResponsesEndpoints(
        UpstreamClient upstream,
        AccountPool pool,
        ICredentialSource credentials,
        ModelCatalog catalog,
        ILogger<ResponsesEndpoints> log)
    {
        _upstream = upstream;
        _pool = pool;
        _credentials = credentials;
        _catalog = catalog;
        _log = log;
    }

    /// <summary>本端点族处理的路径。**精确路径，没有子路径**（与 Anthropic 的 count_tokens 不同）。</summary>
    public const string Path = "/v1/responses";

    /// <summary>一次客户端请求内最多试几个号（与 /v1/chat/completions 的默认值一致）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// 是不是本端点族的请求。是的话就地处理并返回 true。
    ///
    /// ⚠ 返回 false 时**一个字节都没写** —— 判断只看 `Request`，不碰 `Response`。
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        // ⚠ 精确匹配，不是 StartsWith —— Responses 没有 `count_tokens` 那类子路径。
        //   用 StartsWith 的话会把将来某个 `/v1/responses/xxx` 也吞掉。
        if (!string.Equals(path, Path, StringComparison.Ordinal)) return false;

        if (context.Request.Method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 405, "invalid_request_error",
                $"Responses 只支持 POST，收到 {context.Request.Method}").ConfigureAwait(false);
            return true;
        }

        await CreateAsync(context).ConfigureAwait(false);
        return true;
    }

    // ══ 主端点：POST /v1/responses ════════════════════════════

    private async Task CreateAsync(HttpContext context)
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

        // 请求翻译：Responses → Chat
        var chat = TranslateRequest(body, resolvedModel);

        // 走与 /v1/chat/completions 完全相同的管线：
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
                    await StreamAsync(context, response, resolvedModel, body).ConfigureAwait(false);
                }
                else
                {
                    await CollectAsync(context, response, resolvedModel, body).ConfigureAwait(false);
                }
                return;
            }

            var kind = result.Kind ?? UpstreamErrorKind.Unknown;
            if (!IsRetryable(kind))
            {
                await WriteUpstreamErrorAsync(context, result).ConfigureAwait(false);
                return;
            }

            // 可重试：冷却这个号（按**实际发出去的模型**记，与 t11 的教训一致）
            if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited)
            {
                _pool.MarkRateLimited(member.Id, resolvedModel, null);
            }
            else
            {
                // ⚠ 传 result.Status + kind —— 理由同 AnthropicEndpoints 那处：
                //   连接层失败（status == 0）不能记到账号头上。
                _pool.MarkFailure(member.Id, resolvedModel, result.Status, result.Kind);
            }

            tried.Add(member.Id);
            lastResult = result;
        }

        await WriteNoAccountAsync(context, resolvedModel, lastResult).ConfigureAwait(false);
    }

    private static bool IsRetryable(string kind) => kind switch
    {
        UpstreamErrorKind.RateLimited => true,
        UpstreamErrorKind.ModelRateLimited => true,
        UpstreamErrorKind.Server => true,
        _ => false,
    };

    // ══ 非流式：收完整条流再回翻 ═════════════════════════════

    private static async Task CollectAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        string model,
        JsonObject request)
    {
        var collector = new ChatStreamCollector();

        await using var stream = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
        await foreach (var payload in SseReader.ReadDataAsync(stream, context.RequestAborted).ConfigureAwait(false))
        {
            var chunk = SseSanitizer.Process(payload);
            if (chunk.IsDone) break;
            collector.Feed(chunk);
        }

        var completion = collector.Build(model, NewId("chatcmpl"));
        var response = TranslateResponse(completion, model, EchoOptions(request));

        await WriteJsonAsync(context, 200, response).ConfigureAwait(false);
    }

    // ══ 流式：按 Responses 命名事件语义输出 ═══════════════════

    /// <summary>
    /// 把上游的匿名 data-only SSE 转成 Responses 的命名事件流。
    ///
    /// ## 契约要点（Codex 严格按这个解析，错一个就卡住）
    ///
    /// ```
    /// response.created                必须第一个
    /// response.in_progress
    /// response.output_item.added      ← 开一项（output_index 从 0 连续递增）
    ///   response.content_part.added
    ///   response.output_text.delta …  ← 文本项的内容
    ///   response.output_text.done
    ///   response.content_part.done
    /// response.output_item.done       ← 关这一项，之后才能开下一项
    /// …
    /// response.completed              ← 带上完整 output 和 usage
    /// ```
    ///
    /// **与 Chat Completions 的根本差别**：那边是
    /// `data: {"choices":[{"delta":{"content":"x"}}]}` 的**匿名**分片，
    /// 客户端自己按 `delta` 累积；这边是**命名事件 + 显式的 item 生命周期**，
    /// 每一项有 id / output_index / status，且**必须成对开关**。
    /// 所以不能简单转发 —— 必须自己维护状态机（<see cref="StreamState"/>）。
    ///
    /// 每个事件都带 `sequence_number`（从 0 单调递增），与旧版
    /// `responses.js:326` 的 `send()` 一致。
    /// </summary>
    private async Task StreamAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        string model,
        JsonObject request)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var state = new StreamState(model, EchoOptions(request));

        try
        {
            await using var stream = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);

            await foreach (var payload in SseReader.ReadDataAsync(stream, context.RequestAborted).ConfigureAwait(false))
            {
                var chunk = SseSanitizer.Process(payload);
                if (chunk.IsDone) break;

                // ⚠ 每一片都要**边收边发**：PushAsync 里直接写 + flush。
                //    不能先攒完再发 —— 那就把流式抹掉了。
                await state.PushAsync(context, chunk).ConfigureAwait(false);            }

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
    /// 单独一个类型是因为它必须**跨分片持有状态**：当前开着哪一项、
    /// 已分配的 output_index、tool_call 的 index→item 映射、累计文本与 usage。
    ///
    /// ## 为什么 index 管理是这个类的全部难点
    ///
    /// Codex 按 `output_index` 收 item。规则：
    /// · **必须从 0 开始、连续递增**（跳号 = 后面的项全部丢失）
    /// · **上一项 `response.output_item.done` 之后才能开下一项**
    ///
    /// 上游的分片是交错的（文本 delta 里夹着 tool_calls），所以必须自己
    /// 决定「什么时候关掉当前项、什么时候开新项」—— 见
    /// <see cref="CloseOpenAsync"/> / <see cref="OpenMessageAsync"/>。
    /// </summary>
    private sealed class StreamState
    {
        private readonly string _responseId = NewId("resp");
        private readonly long _createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly string _model;
        private readonly JsonNode _tools;
        private readonly JsonNode _toolChoice;

        /// <summary>已经开出去的项（按 output_index 顺序），最终放进 response.completed。</summary>
        private readonly JsonArray _items = [];

        /// <summary>tool_call 的 index → 正在累积的那一项。</summary>
        private readonly Dictionary<int, OpenItem> _toolItems = [];

        private OpenItem? _open;
        private int _sequenceNumber;
        private bool _started;
        private bool _finished;
        private long _inputTokens;
        private long _outputTokens;
        private string? _incompleteReason;
        private bool _hasFunctionCall;

        public StreamState(string model, (JsonNode Tools, JsonNode ToolChoice) echo)
        {
            _model = model;
            _tools = echo.Tools;
            _toolChoice = echo.ToolChoice;
        }

        // ⚠ 参数**不能**用 `in SseChunk` —— 异步方法不允许 ref/in/out 参数
        //   （CS1988）。SseChunk 是 readonly record struct，按值传只是一次
        //   六个字段的拷贝，对这个热路径没有实测影响。
        public async Task PushAsync(HttpContext context, SseChunk chunk)
        {
            if (_finished) return;

            if (chunk.Usage is JsonObject usage)
            {
                var (input, output) = UsageOf(usage);
                if (input > 0) _inputTokens = input;
                _outputTokens = output;
            }

            if (chunk.Raw is null) return;

            JsonNode? node;
            try { node = JsonNode.Parse(chunk.Raw); }
            catch (System.Text.Json.JsonException) { return; }
            if (node is not JsonObject obj) return;

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) return;
            if (choices[0] is not JsonObject choice) return;

            var delta = choice["delta"] as JsonObject ?? [];

            // ⚠ 一旦确认这是本响应的一部分就要先发 response.created ——
            //    Codex 靠它拿 response.id，缺了整条流解析不了。
            //    放在这里而不是请求开始处：上游**报错时**我们走的是错误信封
            //    （一个字节都还没写），不能先发 created 把状态锁死。
            await EnsureStartedAsync(context).ConfigureAwait(false);

            // ── 正文 → message 项 ──────────────────────────────
            var text = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
            {
                if (_open?.Kind != ItemKind.Message) await OpenMessageAsync(context).ConfigureAwait(false);

                _open!.Text.Append(text);
                _open.Item["content"]!.AsArray()[0]!["text"] = _open.Text.ToString();

                await SendAsync(context, "response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta",
                    ["item_id"] = _open.Item["id"]!.GetValue<string>(),
                    ["output_index"] = _open.OutputIndex,
                    ["content_index"] = 0,
                    ["delta"] = text,
                }).ConfigureAwait(false);
            }

            // ── tool_calls → function_call 项 ──────────────────
            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls)
                {
                    if (call is not JsonObject tc) continue;

                    var key = 0;
                    if (tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx)) key = idx;

                    if (!_toolItems.TryGetValue(key, out var entry))
                    {
                        var fn = tc["function"] as JsonObject ?? [];
                        await OpenFunctionCallAsync(context, fn["name"]?.GetValue<string>() ?? "").ConfigureAwait(false);
                        entry = _open!;

                        if (tc["id"]?.GetValue<string>() is { Length: > 0 } callId)
                        {
                            entry.Item["call_id"] = callId;
                        }

                        _toolItems[key] = entry;
                    }

                    var args = (tc["function"] as JsonObject)?["arguments"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(args))
                    {
                        entry.Arguments.Append(args);
                        entry.Item["arguments"] = entry.Arguments.ToString();

                        await SendAsync(context, "response.function_call_arguments.delta", new JsonObject
                        {
                            ["type"] = "response.function_call_arguments.delta",
                            ["item_id"] = entry.Item["id"]!.GetValue<string>(),
                            ["output_index"] = entry.OutputIndex,
                            ["delta"] = args,
                        }).ConfigureAwait(false);
                    }
                }
            }

            if (choice["finish_reason"]?.GetValue<string>() == "length")
            {
                _incompleteReason = "max_output_tokens";
            }
        }

        /// <summary>
        /// 收尾。**必须调用** —— 不调的话 Codex 会一直等下去。
        ///
        /// ⚠ 有一处以**实际收到的内容**为准的修正（对齐旧版 `responses.js:521`）：
        /// 上游可能一个 item 都没开就结束了（空回复）。这时必须**补一个空的
        /// message 项**再完成 —— `output: []` 会让 Codex 认为响应结构坏掉。
        /// </summary>
        public async Task FinishAsync(HttpContext context)
        {
            if (_finished) return;
            _finished = true;

            await EnsureStartedAsync(context).ConfigureAwait(false);
            await CloseOpenAsync(context).ConfigureAwait(false);

            if (_items.Count == 0)
            {
                await OpenMessageAsync(context).ConfigureAwait(false);
                await CloseOpenAsync(context).ConfigureAwait(false);
            }

            var status = _incompleteReason is null ? "completed" : "incomplete";

            await SendAsync(context,
                status == "completed" ? "response.completed" : "response.incomplete",
                new JsonObject
                {
                    ["type"] = status == "completed" ? "response.completed" : "response.incomplete",
                    ["response"] = Shell(status, _items),
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// 中途失败 → `response.failed` 事件（**不是** HTTP 错误码）。
        ///
        /// ⚠ 这里已经进入了事件流（响应头 200 早发出去了），所以只能按
        /// Responses 的失败语义在**流内**报错，再也不能改状态码。
        /// </summary>
        public async Task FailAsync(HttpContext context, string message)
        {
            if (_finished) return;
            _finished = true;

            await CloseOpenAsync(context).ConfigureAwait(false);

            await SendAsync(context, "response.failed", new JsonObject
            {
                ["type"] = "response.failed",
                ["response"] = new JsonObject
                {
                    ["id"] = _responseId,
                    ["object"] = "response",
                    ["created_at"] = _createdAt,
                    ["status"] = "failed",
                    ["model"] = _model,
                    ["output"] = _items.DeepClone(),
                    ["parallel_tool_calls"] = true,
                    ["tool_choice"] = _toolChoice.DeepClone(),
                    ["tools"] = _tools.DeepClone(),
                    ["usage"] = null,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "server_error",
                        ["message"] = message,
                    },
                },
            }).ConfigureAwait(false);
        }

        private async Task EnsureStartedAsync(HttpContext context)
        {
            if (_started) return;
            _started = true;

            await SendAsync(context, "response.created", new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = Shell("in_progress", []),
            }).ConfigureAwait(false);

            await SendAsync(context, "response.in_progress", new JsonObject
            {
                ["type"] = "response.in_progress",
                ["response"] = Shell("in_progress", []),
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 关掉当前打开的项（如果开着）。
        ///
        /// ⚠ 这是**顺序契约的核心**：`output_item.done` 必须在下一项
        /// `output_item.added` 之前发出去。漏掉的话 Codex 会看到两个
        /// 同时「开着」的项，index 立刻错位。
        /// </summary>
        private async Task CloseOpenAsync(HttpContext context)
        {
            if (_open is null) return;

            var item = _open;
            _open = null;

            if (item.Kind == ItemKind.Message)
            {
                // 文本项的四段收尾：text.done → content_part.done → item.done
                await SendAsync(context, "response.output_text.done", new JsonObject
                {
                    ["type"] = "response.output_text.done",
                    ["item_id"] = item.Item["id"]!.GetValue<string>(),
                    ["output_index"] = item.OutputIndex,
                    ["content_index"] = 0,
                    ["text"] = item.Text.ToString(),
                }).ConfigureAwait(false);

                await SendAsync(context, "response.content_part.done", new JsonObject
                {
                    ["type"] = "response.content_part.done",
                    ["item_id"] = item.Item["id"]!.GetValue<string>(),
                    ["output_index"] = item.OutputIndex,
                    ["content_index"] = 0,
                    ["part"] = new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = item.Text.ToString(),
                        ["annotations"] = new JsonArray(),
                    },
                }).ConfigureAwait(false);
            }
            else
            {
                item.Item["arguments"] = item.Arguments.ToString();

                await SendAsync(context, "response.function_call_arguments.done", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done",
                    ["item_id"] = item.Item["id"]!.GetValue<string>(),
                    ["output_index"] = item.OutputIndex,
                    ["arguments"] = item.Arguments.ToString(),
                }).ConfigureAwait(false);
            }

            item.Item["status"] = "completed";

            await SendAsync(context, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = item.OutputIndex,
                ["item"] = item.Item.DeepClone(),
            }).ConfigureAwait(false);
        }

        private async Task OpenMessageAsync(HttpContext context)
        {
            await CloseOpenAsync(context).ConfigureAwait(false);

            var outputIndex = _items.Count;
            var item = new JsonObject
            {
                ["type"] = "message",
                ["id"] = NewId("msg"),
                ["status"] = "in_progress",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = "",
                    ["annotations"] = new JsonArray(),
                }),
            };

            _items.Add(item);
            _open = new OpenItem(ItemKind.Message, outputIndex, item);

            await SendAsync(context, "response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = outputIndex,
                ["item"] = item.DeepClone(),
            }).ConfigureAwait(false);

            await SendAsync(context, "response.content_part.added", new JsonObject
            {
                ["type"] = "response.content_part.added",
                ["item_id"] = item["id"]!.GetValue<string>(),
                ["output_index"] = outputIndex,
                ["content_index"] = 0,
                ["part"] = new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = "",
                    ["annotations"] = new JsonArray(),
                },
            }).ConfigureAwait(false);
        }

        private async Task OpenFunctionCallAsync(HttpContext context, string name)
        {
            await CloseOpenAsync(context).ConfigureAwait(false);

            var outputIndex = _items.Count;
            var item = new JsonObject
            {
                ["type"] = "function_call",
                ["id"] = NewId("fc"),
                ["call_id"] = NewId("call"),
                ["name"] = name,
                ["arguments"] = "",
                ["status"] = "in_progress",
            };

            _items.Add(item);
            _open = new OpenItem(ItemKind.FunctionCall, outputIndex, item);
            _hasFunctionCall = true;

            await SendAsync(context, "response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = outputIndex,
                ["item"] = item.DeepClone(),
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// response 外壳。`in_progress` 时 `usage` 与 `incomplete_details` 都是 null
        /// （对齐旧版 `responses.js:336`）—— 在跑的时候报用量是错的。
        /// </summary>
        private JsonObject Shell(string status, JsonArray output) => new()
        {
            ["id"] = _responseId,
            ["object"] = "response",
            ["created_at"] = _createdAt,
            ["status"] = status,
            ["model"] = _model,
            ["output"] = output,
            ["parallel_tool_calls"] = true,
            ["tool_choice"] = _toolChoice.DeepClone(),
            ["tools"] = _tools.DeepClone(),
            ["usage"] = status == "in_progress"
                ? null
                : new JsonObject
                {
                    ["input_tokens"] = _inputTokens,
                    ["output_tokens"] = _outputTokens,
                    ["total_tokens"] = _inputTokens + _outputTokens,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                    ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                },
            ["error"] = null,
            ["incomplete_details"] = status == "in_progress" || _incompleteReason is null
                ? null
                : new JsonObject { ["reason"] = _incompleteReason },
        };

        /// <summary>
        /// 写一个 Responses 命名事件并立刻 flush。
        ///
        /// ⚠ 每个事件都补 `sequence_number`（从 0 单调递增）——
        /// Codex 用它检测丢帧，缺了这个字段或跳号都会让它报错。
        /// </summary>
        private async Task SendAsync(HttpContext context, string name, JsonObject payload)
        {
            payload["sequence_number"] = _sequenceNumber++;
            await WriteEventAsync(context, name, payload).ConfigureAwait(false);
        }

        /// <summary>有没有开过 function_call 项（决定 status 用 completed 还是别的）。</summary>
        public bool HasFunctionCall => _hasFunctionCall;

        /// <summary>一个「正开着」的 output item。</summary>
        private sealed class OpenItem(ItemKind kind, int outputIndex, JsonObject item)
        {
            public ItemKind Kind { get; } = kind;
            public int OutputIndex { get; } = outputIndex;
            public JsonObject Item { get; } = item;
            public StringBuilder Text { get; } = new();
            public StringBuilder Arguments { get; } = new();
        }

        private enum ItemKind
        {
            Message,
            FunctionCall,
        }
    }

    /// <summary>写一个 Responses 命名事件并立刻 flush（不 flush 就不是流式）。</summary>
    private static async Task WriteEventAsync(HttpContext context, string name, JsonNode payload)
    {
        var text = $"event: {name}\ndata: {payload.ToJsonString(RelaxedJson)}\n\n";
        await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ══ 请求翻译 ══════════════════════════════════════════════

    /// <summary>
    /// Responses `/v1/responses` 请求体 → OpenAI Chat Completions 请求体。
    ///
    /// ## 五处容易翻错的映射
    ///
    /// 1. **instructions 抽离**：Responses 把系统提示放在**顶层字符串字段**，
    ///    Chat 要它当一条最前面的 `role:"system"` 消息。
    /// 2. **input 有两种形态**：可以直接是字符串（整段用户输入），
    ///    也可以是一组**带 `type` 的项**（message / function_call /
    ///    function_call_output / reasoning）。
    /// 3. **`developer` 角色归一到 `system`**：Chat 不认 developer。
    /// 4. **`function_call_output` 要挪到 `role:"tool"`**，且 Chat 要求它
    ///    紧跟在带 tool_calls 的 assistant 之后。
    /// 5. **并行工具调用要并进同一条 assistant 消息**：Responses 把每个调用
    ///    发成**独立的 item**，Chat 要求它们在同一个 `tool_calls` 数组里 ——
    ///    连续两个 assistant 消息上游会拒。
    ///
    /// 另外：`max_output_tokens` → `max_tokens`，tools 从扁平变嵌套。
    /// </summary>
    internal static JsonObject TranslateRequest(JsonObject body, string model)
    {
        var messages = new JsonArray();

        var instructions = body["instructions"];
        if (instructions is JsonValue iv && iv.TryGetValue<string>(out var instructionText)
            && !string.IsNullOrWhiteSpace(instructionText))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructionText });
        }

        var input = body["input"];

        if (input is JsonValue sv && sv.TryGetValue<string>(out var plainInput))
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = plainInput });
        }
        else if (input is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is not JsonObject entry) continue;

                switch (entry["type"]?.GetValue<string>())
                {
                    case "message":
                        AppendMessage(messages, entry);
                        break;

                    case "function_call":
                        AppendFunctionCall(messages, entry);
                        break;

                    case "function_call_output":
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = entry["call_id"]?.GetValue<string>() ?? "",
                            ["content"] = OutputText(entry["output"]),
                        });
                        break;

                    // reasoning / 未知类型：上游不认，丢掉。
                    // 丢掉而不是报错 —— Responses 的 item 类型比 Chat 认识的多。
                    default:
                        break;
                }
            }
        }

        var chat = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = body["stream"] is JsonValue stv && stv.TryGetValue<bool>(out var st) && st,
        };

        // ⚠ 不能用 `TryGetValue<long>` —— 见 AnthropicEndpoints.TryInt 的长注释：
        //   JSON 数值按最窄类型背书，`TryGetValue<long>` 对 int 背书的值返回 false
        //   且**不抛异常**，后果是参数被静默丢弃（客户端设了却不生效）。
        if (body["max_output_tokens"] is JsonValue mtv && TryInt(mtv, out var maxTokens))
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

        var tools = TranslateTools(body["tools"]);
        if (tools is not null) chat["tools"] = tools;

        var toolChoice = TranslateToolChoice(body["tool_choice"]);
        if (toolChoice is not null) chat["tool_choice"] = toolChoice;

        return chat;
    }

    private static void AppendMessage(JsonArray messages, JsonObject entry)
    {
        var rawRole = entry["role"]?.GetValue<string>() ?? "user";

        // developer 是 Responses 里的新名字，Chat 只认 system
        var role = rawRole == "developer" ? "system" : rawRole;

        var parts = new JsonArray();
        if (entry["content"] is JsonArray content)
        {
            foreach (var part in content)
            {
                if (part is not JsonObject p) continue;
                var converted = ToChatPart(p);
                if (converted is not null) parts.Add(converted);
            }
        }

        if (parts.Count == 1 && parts[0]!["type"]!.GetValue<string>() == "text")
        {
            // 单个文本 part 拍平成字符串 —— 上游对字符串形态支持最好
            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = parts[0]!["text"]!.GetValue<string>(),
            });
        }
        else if (parts.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
        }
        else if (entry["content"] is JsonValue cv && cv.TryGetValue<string>(out var text))
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }

        // ⚠ 一个 part 都不剩时**刻意不加消息**（对齐旧版 `responses.js:123-129`）：
        //   只有在 content 是字符串时才补。凭空塞一条空 user 消息会让上游
        //   看到两个连续的 assistant，那比丢一条消息更糟。
    }

    /// <summary>
    /// 把 `function_call` 项并进上一条 assistant 的 `tool_calls`。
    ///
    /// ⚠ 这是 Responses → Chat 最容易翻错的一处：Responses 把**每个并行调用
    /// 发成独立的 item**，而 Chat 要求它们在**同一条** assistant 消息的
    /// `tool_calls` 数组里。直接一条一个 assistant 消息的话，上游看到
    /// 连续的 assistant 会拒（或者只认最后一个调用）。
    /// </summary>
    private static void AppendFunctionCall(JsonArray messages, JsonObject entry)
    {
        var call = new JsonObject
        {
            ["id"] = entry["call_id"]?.GetValue<string>() is { Length: > 0 } callId ? callId : NewId("call"),
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = entry["name"]?.GetValue<string>() ?? "",
                ["arguments"] = entry["arguments"]?.GetValue<string>() ?? "",
            },
        };

        var last = messages.Count > 0 ? messages[messages.Count - 1] as JsonObject : null;
        if (last is not null && last["role"]?.GetValue<string>() == "assistant" && last["tool_calls"] is JsonArray existing)
        {
            existing.Add(call);
            return;
        }

        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = "",
            ["tool_calls"] = new JsonArray(call),
        });
    }

    /// <summary>Responses 的 content part → Chat 的 content part。不认识的返回 null 丢弃。</summary>
    private static JsonObject? ToChatPart(JsonObject part) => part["type"]?.GetValue<string>() switch
    {
        "input_text" or "output_text" or "text" => new JsonObject
        {
            ["type"] = "text",
            ["text"] = part["text"]?.GetValue<string>() ?? "",
        },

        "input_image" => part["image_url"]?.GetValue<string>() is { Length: > 0 } url
            ? new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = url },
            }
            // ⚠ 只给了 file_id 时**占个位比整条消息丢掉强**（对齐旧版
            //   `responses.js:41-44`）：上游拿不到我们上传的文件，
            //   但用户能看到模型至少知道「这里本该有张图」。
            : part["file_id"]?.GetValue<string>() is { Length: > 0 } fileId
                ? new JsonObject { ["type"] = "text", ["text"] = $"[image:{fileId}]" }
                : null,

        _ => null,
    };

    /// <summary>`function_call_output` 的 output 可以是字符串、part 数组，或任意对象。</summary>
    internal static string OutputText(JsonNode? output)
    {
        if (output is JsonValue v && v.TryGetValue<string>(out var s)) return s;

        if (output is JsonArray parts)
        {
            var texts = new List<string>();
            foreach (var part in parts)
            {
                if (part is JsonObject o && o["text"] is JsonValue tv && tv.TryGetValue<string>(out var t)) texts.Add(t);
            }
            return string.Join("\n", texts);
        }

        return output is null ? "" : output.ToJsonString();
    }

    /// <summary>
    /// Responses 的工具是**扁平**的（`name` 在顶层），Chat 的嵌在 `function` 里。
    ///
    /// 只认 `type:"function"`；`web_search` 之类服务端工具上游不认，丢掉。
    /// </summary>
    private static JsonArray? TranslateTools(JsonNode? tools)
    {
        if (tools is not JsonArray source) return null;

        var converted = new JsonArray();
        foreach (var item in source)
        {
            if (item is not JsonObject tool) continue;
            if (tool["type"]?.GetValue<string>() != "function") continue;

            var name = tool["name"]?.GetValue<string>()?.Trim() ?? "";
            if (name.Length == 0) continue;

            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = tool["description"]?.GetValue<string>() ?? "",
                    ["parameters"] = tool["parameters"] is JsonObject schema
                        ? schema.DeepClone()
                        // 没给 schema 时补一个空对象 schema —— 上游对缺失的
                        // parameters 会拒，而工具的语义本来就允许无参
                        : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            });
        }

        return converted.Count > 0 ? converted : null;
    }

    /// <summary>Responses 的 tool_choice → Chat 的写法。</summary>
    private static JsonNode? TranslateToolChoice(JsonNode? choice)
    {
        if (choice is JsonValue v && v.TryGetValue<string>(out var name)) return name;
        if (choice is not JsonObject obj) return null;

        // ⚠ 形状不同但语义相同：Responses 是 `{type:"function",name:"x"}`，
        //    Chat 是 `{type:"function",function:{name:"x"}}`。
        if (obj["type"]?.GetValue<string>() == "function" && obj["name"]?.GetValue<string>() is { Length: > 0 } fn)
        {
            return new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = fn } };
        }

        return null;
    }

    /// <summary>
    /// 抽一个整数，**逐类型试**（见 <see cref="AnthropicEndpoints"/> 里
    /// `TryInt` 的长注释 —— 这是本项目踩过两次的同一个坑）。
    /// </summary>
    private static bool TryInt(JsonValue value, out long result)
    {
        result = 0;
        if (value.TryGetValue<long>(out result)) return true;
        if (value.TryGetValue<int>(out var i)) { result = i; return true; }
        if (value.TryGetValue<double>(out var d)) { result = (long)d; return true; }
        if (value.TryGetValue<decimal>(out var m)) { result = (long)m; return true; }
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out var parsed))
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
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    // ══ 响应翻译 ══════════════════════════════════════════════

    /// <summary>
    /// Chat Completions 文档 → 一个完整的 Responses `object:"response"` 文档（非流式）。
    ///
    /// ## 三个容易错的点
    ///
    /// · **`finish_reason:"length"` → `status:"incomplete"` +
    ///   `incomplete_details.reason:"max_output_tokens"`** —— 不是完成。
    ///   报成 completed 的话 Codex 会以为回答说完了，静默截断。
    /// · **`reasoning_content` → 一个独立的 `reasoning` 项**（放在最前）。
    /// · **一个 item 都没有时补一个空 message 项** —— `output: []` 是坏结构。
    /// </summary>
    internal static JsonObject TranslateResponse(JsonObject completion, string model, (JsonNode Tools, JsonNode ToolChoice) echo)
    {
        var choice = (completion["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject ?? [];

        var output = OutputItems(message);
        var (inputTokens, outputTokens) = UsageOf(completion["usage"]);

        var finish = choice?["finish_reason"]?.GetValue<string>();
        var incomplete = finish == "length" ? "max_output_tokens" : null;

        return new JsonObject
        {
            ["id"] = NewId("resp"),
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = incomplete is null ? "completed" : "incomplete",
            ["model"] = model,
            ["output"] = output,
            ["parallel_tool_calls"] = true,
            ["tool_choice"] = echo.ToolChoice.DeepClone(),
            ["tools"] = echo.Tools.DeepClone(),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
            },
            ["error"] = null,
            ["incomplete_details"] = incomplete is null ? null : new JsonObject { ["reason"] = incomplete },
        };
    }

    /// <summary>Chat 的 message → Responses 的 output item 数组。</summary>
    internal static JsonArray OutputItems(JsonObject message)
    {
        var items = new JsonArray();

        var reasoning = message["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(reasoning))
        {
            items.Add(new JsonObject
            {
                ["type"] = "reasoning",
                ["id"] = NewId("rs"),
                ["summary"] = new JsonArray(new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = reasoning,
                }),
            });
        }

        var text = message["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(text))
        {
            items.Add(new JsonObject
            {
                ["type"] = "message",
                ["id"] = NewId("msg"),
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = text,
                    ["annotations"] = new JsonArray(),
                }),
            });
        }

        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                if (call is not JsonObject c) continue;
                var fn = c["function"] as JsonObject ?? [];

                items.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["id"] = NewId("fc"),
                    ["call_id"] = c["id"]?.GetValue<string>() is { Length: > 0 } id ? id : NewId("call"),
                    ["name"] = fn["name"]?.GetValue<string>() ?? "",
                    ["arguments"] = fn["arguments"]?.GetValue<string>() ?? "",
                    ["status"] = "completed",
                });
            }
        }

        // ⚠ 空回复也要有一个 message 项 —— `output: []` 会让 Codex 认为
        //   响应结构坏掉（对齐旧版 `responses.js:242-250`）。
        if (items.Count == 0)
        {
            items.Add(new JsonObject
            {
                ["type"] = "message",
                ["id"] = NewId("msg"),
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = "",
                    ["annotations"] = new JsonArray(),
                }),
            });
        }

        return items;
    }

    /// <summary>
    /// 回显请求里的 tools / tool_choice。
    ///
    /// 为什么要回显：Responses 的 `response` 文档里这两个字段是**必有的**，
    /// Codex 拿它们做会话状态管理。**回显的是原始请求值**（不是翻译后的
    /// Chat 形状），因为客户端看到的是自己的协议。
    ///
    /// `internal` 是为了让测试能直接构造回显参数验非流式路径。
    /// </summary>
    internal static (JsonNode Tools, JsonNode ToolChoice) EchoOptions(JsonObject request)
    {
        var tools = request["tools"] is JsonArray array ? array.DeepClone() : new JsonArray();

        // tool_choice 缺省是 "auto"（对齐旧版 `responses.js:264`）
        var toolChoice = request["tool_choice"]?.DeepClone() ?? JsonValue.Create("auto")!;

        return (tools, toolChoice);
    }

    /// <summary>usage 缺字段时补齐 —— 客户端对 undefined/null 很敏感。</summary>
    private static (long Input, long Output) UsageOf(JsonNode? usage)
    {
        if (usage is not JsonObject obj) return (0, 0);

        // ⚠ 同样不能只试 long —— 上游回 `{"prompt_tokens": 2541}` 时 2541 是
        //   int 背书，只试 long 会拿到 false，token 数全部显示 0。
        long Get(string key) => obj[key] is JsonValue v && TryInt(v, out var n) ? n : 0;

        return (Get("prompt_tokens"), Get("completion_tokens"));
    }

    // ══ 输出 ══════════════════════════════════════════════════

    internal static string NewId(string prefix)
        => $"{prefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Guid.NewGuid().ToString("N")}"[..(prefix.Length + 1 + 8 + 12)];

    internal static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
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
    /// Responses 的错误信封。
    ///
    /// ## 形状（对齐旧版 `responses.js:185-187`）
    ///
    /// ```json
    /// {"error":{"message":"…","type":"api_error","code":"api_error","param":null}}
    /// ```
    ///
    /// ⚠ 它和 Chat Completions 的 `{"error":{"message","type","code"}}` **很像但不完全一样**：
    /// Responses 多一个 `param` 字段。Codex 按 OpenAI Python SDK 的
    /// `APIError` 解析，`param` 缺失不会炸，但补上才是完整形状。
    /// </summary>
    private static Task WriteErrorAsync(HttpContext context, int status, string type, string message)
        => WriteJsonAsync(context, status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = string.IsNullOrWhiteSpace(message) ? type : message,
                ["type"] = type,
                ["code"] = type,
                ["param"] = null,
            },
        });

    /// <summary>上游失败 → Responses 错误信封（错误类型按上游分类映射）。</summary>
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
            UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => "rate_limit_exceeded",
            UpstreamErrorKind.BadRequest => "invalid_request_error",
            _ => "server_error",
        };

        var detail = string.IsNullOrWhiteSpace(result.Message) ? "上游没有给出原因" : result.Message!;
        if (detail.Length > 400) detail = detail[..400] + "…";

        await WriteErrorAsync(context, status, type, detail).ConfigureAwait(false);
    }

    /// <summary>
    /// 号用光了（或全被限流）。
    ///
    /// ⚠ 类型名与 Anthropic 那一族**刻意不同**：Responses 走 OpenAI 的命名
    /// （`rate_limit_exceeded` / `insufficient_quota`），不是 Anthropic 的
    /// `rate_limit_error`。对齐旧版 `responses.js:190-204` 的 `responsesErrorTypeOf`。
    /// </summary>
    private static async Task WriteNoAccountAsync(HttpContext context, string model, UpstreamResult? lastResult)
    {
        var isServer = lastResult?.Kind == UpstreamErrorKind.Server;
        var status = isServer ? 502 : 429;
        var type = isServer ? "server_error" : "rate_limit_exceeded";

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Responses 流中断")]
    private partial void LogStreamFailed(Exception ex);
}
