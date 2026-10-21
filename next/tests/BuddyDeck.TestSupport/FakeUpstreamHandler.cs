using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BuddyDeck.TestSupport;

/// <summary>
/// 一次被假上游收到的请求。**全部字段都是原样快照**，断言直接读就行。
///
/// 为什么请求体要在这里读出来存好：<see cref="HttpContent"/> 只能读一次
/// （<c>LoadIntoBufferAsync</c> 之后重复读要么抛要么拿到空），处理器读完
/// 调用方就再也读不到了。不存的话「请求体改写管线到底改了没有」这类断言
/// 就没法写。
/// </summary>
public sealed record CapturedRequest
{
    /// <summary>HTTP 方法（大写）。</summary>
    public required string Method { get; init; }

    /// <summary>完整 URL，含 query。</summary>
    public required string Url { get; init; }

    /// <summary>URL 的路径部分（不含 query）。路由和断言基本都用它。</summary>
    public required string Path { get; init; }

    /// <summary>请求头字典（大小写不敏感，值是多值的 —— 同名头可能出现多次）。</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    /// <summary>请求体全文。没有正文时为 null（GET 一般是 null）。</summary>
    public string? Body { get; init; }

    /// <summary>第几次请求（从 0 开始）。<see cref="UpstreamScript"/> 按它取脚本。</summary>
    public int Sequence { get; init; }

    /// <summary>
    /// 解码后的 Content-Type。
    ///
    /// **注意它不在 <see cref="Headers"/> 里** —— Content-Type 属于 content
    /// headers，挂在 <c>RequestMessage.Content.Headers</c> 上，
    /// 不挂在 <c>RequestMessage.Headers</c> 上。这里单独提出来，
    /// 免得每个断言都要绕一圈。
    /// </summary>
    public string ContentType { get; init; } = "";

    /// <summary>
    /// 请求体按 JSON 解析（解析不了返回 null）。
    ///
    /// 返回的是**全新的副本** —— 调用方可以随便改它做断言，不会影响别的地方。
    /// </summary>
    public JsonNode? Json()
    {
        if (string.IsNullOrEmpty(Body)) return null;
        try
        {
            return JsonNode.Parse(Body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>取一个头。没有或多个值抛异常 —— 那种情况下断言本身要复检。</summary>
    public string Header(string name)
    {
        if (!Headers.TryGetValue(name, out var values) || values.Length == 0)
        {
            throw new KeyNotFoundException($"请求里没有头 {name}（{Method} {Url}）");
        }

        return values.Length == 1
            ? values[0]
            : throw new InvalidOperationException($"头 {name} 有 {values.Length} 个值：{string.Join(" | ", values)}");
    }

    /// <summary>取一个头；没有就返回 null（不抛）。</summary>
    public string? HeaderOrNull(string name)
        => Headers.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;

    /// <summary>有没有这个头。</summary>
    public bool HasHeader(string name) => Headers.ContainsKey(name);

    public override string ToString() => $"{Method} {Url}";
}

/// <summary>
/// 可编程的假上游 —— 一个 <see cref="HttpMessageHandler"/>，在测试里冒充
/// WorkBuddy 上游。
///
/// ## 为什么不用真的打上游
///
/// · 慢、要网络、会被限流
/// · **测不了错误路径** —— 想让上游返回「429 + code 6004」得先真被限流
/// · 会消耗真实账号的配额（而配额是全队共用的，见 HANDOVER 第八节）
///
/// ## 两种用法，别混
///
/// <code>
/// // ① 固定响应：这次请求永远回同一个东西
/// var fake = new FakeUpstreamHandler(_ =&gt; UpstreamResponses.Text("""{"code":0}"""));
/// var fake = new FakeUpstreamHandler(UpstreamResponses.SseText("…"));      // 同一个构造器
///
/// // ② 脚本化：第 N 次请求回不同的东西（「先 429 再 200」靠这个）
/// fake.Script(UpstreamResponses.SseText("…"), UpstreamResponses.TooManyRequests());
/// </code>
///
/// ## 记录了什么
///
/// 每次请求的**方法、URL、全部请求头、请求体全文**都进
/// <see cref="Requests"/>，按到达顺序。<see cref="Last"/> 是最后一次。
/// 另有 <see cref="Paths"/> / <see cref="RequestedModels"/> / <see cref="AuthorizationHeaders"/>
/// 这三个常用视图。
///
/// 线程安全：网关的流式路径会并发发请求，记录用锁保护。
/// </summary>
public sealed class FakeUpstreamHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _responder;
    private readonly List<CapturedRequest> _requests = [];
    private readonly object _gate = new();
    private UpstreamScript? _script;

    /// <summary>
    /// @param responder 收到请求后回什么。参数是**已经记录好的**请求快照，
    ///                  所以可以直接在里面按路径/模型分支 —— 也可以直接忽略它。
    ///                  ⚠ 每次调用都必须返回一个**新的**响应对象（用
    ///                  <see cref="UpstreamResponses"/> 里的工厂天然满足）。
    /// </param>
    public FakeUpstreamHandler(Func<CapturedRequest, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>永远回同一个响应。</summary>
    public FakeUpstreamHandler(HttpResponseMessage response)
        => _responder = _ => UpstreamResponses.Clone(response);

    /// <summary>固定回同一个响应（语法糖，读起来更像句子）。</summary>
    public static FakeUpstreamHandler Returning(HttpResponseMessage response) => new(response);

    /// <summary>
    /// 按方法 + 路径路由，未命中回 404（**不是**静默回 200）——
    /// 静默成功会让「打错路径」这类 bug 变成一条永远绿的测试。
    /// </summary>
    public static FakeUpstreamHandler Routing(params UpstreamRoute[] routes)
    {
        var map = routes.ToDictionary(
            r => $"{r.Method.ToUpperInvariant()} {r.Path}",
            r => r,
            StringComparer.OrdinalIgnoreCase);

        return new FakeUpstreamHandler(request =>
        {
            if (map.TryGetValue($"{request.Method} {request.Path}", out var route)) return UpstreamResponses.Clone(route.Response);

            return UpstreamResponses.Text(
                UpstreamResponses.EnvelopeBody(404, null, $"fake upstream 没有这条路由：{request.Method} {request.Path}"),
                HttpStatusCode.NotFound);
        });
    }

    /// <summary>
    /// 装上脚本队列：第 N 次请求用第 N 个响应。
    ///
    /// 队列用完（请求次数超出脚本长度）后**继续用最后一步** —— 不断言次数的
    /// 用例不用把每一步都写全（比如「第一次 429，之后一直 200」）。
    /// </summary>
    public FakeUpstreamHandler Script(params Func<CapturedRequest, HttpResponseMessage>[] steps)
    {
        lock (_gate) _script = new UpstreamScript(steps);
        return this;
    }

    /// <summary>脚本队列的重载：直接给现成的响应。</summary>
    public FakeUpstreamHandler Script(params HttpResponseMessage[] steps)
        => Script(steps.Select<HttpResponseMessage, Func<CapturedRequest, HttpResponseMessage>>(
            r => _ => UpstreamResponses.Clone(r)).ToArray());

    /// <summary>收到的全部请求（快照副本，按到达顺序）。</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    /// <summary>最后一次请求。一次都没收到时抛 —— 那种情况下断言本来就要复检。</summary>
    public CapturedRequest Last
    {
        get
        {
            lock (_gate)
            {
                if (_requests.Count == 0) throw new InvalidOperationException("假上游一次请求都没收到");
                return _requests[^1];
            }
        }
    }

    /// <summary>一共收到几次请求。</summary>
    public int Count
    {
        get { lock (_gate) return _requests.Count; }
    }

    /// <summary>请求路径序列 —— 断言「第一次打 A、第二次打 B」用。</summary>
    public IReadOnlyList<string> Paths => Requests.Select(r => r.Path).ToArray();

    /// <summary>每个请求体里的 model 字段（没有就是 null）—— 断言「换模型重试」用。</summary>
    public IReadOnlyList<string?> RequestedModels
        => Requests.Select(r => r.Json()?["model"]?.GetValue<string>()).ToArray();

    /// <summary>每个请求的 Authorization 头 —— 断言「换号了」用。</summary>
    public IReadOnlyList<string?> AuthorizationHeaders
        => Requests.Select(r => r.HeaderOrNull("Authorization")).ToArray();

    /// <summary>清空记录（脚本不动）。</summary>
    public void Clear()
    {
        lock (_gate) _requests.Clear();
    }

    /// <summary>清空记录并卸掉脚本。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _requests.Clear();
            _script = null;
        }
    }

    /// <summary>包成可以直接喂给 <c>UpstreamClient</c> 的 HttpClient。</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var captured = await request.ToCapturedAsync(cancellationToken).ConfigureAwait(false);

        CapturedRequest recorded;
        Func<CapturedRequest, HttpResponseMessage>? scripted;

        lock (_gate)
        {
            recorded = captured with { Sequence = _requests.Count };
            _requests.Add(recorded);
            scripted = _script?.Next(recorded.Sequence);
        }

        var response = (scripted ?? _responder)(recorded);
        return response is null
            ? UpstreamResponses.Text("""{"error":"假上游的响应工厂返回了 null"}""", HttpStatusCode.InternalServerError)
            : response;
    }
}

/// <summary>按方法 + 路径路由的一条规则。</summary>
public sealed record UpstreamRoute(string Method, string Path, HttpResponseMessage Response)
{
    /// <summary>POST 的 chat 补全路由。</summary>
    public static UpstreamRoute Chat(HttpResponseMessage response)
        => new("POST", "/v2/chat/completions", response);

    /// <summary>POST 的 token 刷新路由。</summary>
    public static UpstreamRoute Refresh(HttpResponseMessage response)
        => new("POST", "/v2/plugin/auth/token/refresh", response);

    /// <summary>GET 的模型目录路由。</summary>
    public static UpstreamRoute Catalog(HttpResponseMessage response)
        => new("GET", "/v3/config", response);

    /// <summary>METHOD * —— 任意方法。</summary>
    public static UpstreamRoute AnyMethod(string path, HttpResponseMessage response)
        => new("*", path, response);
}

/// <summary>内部工具：把 <see cref="HttpRequestMessage"/> 拍成快照。</summary>
internal static class RequestCapture
{
    /// <summary>
    /// 拍一份快照。请求体和请求头都读完存好 —— <see cref="HttpContent"/>
    /// 只能读一次，不存的话调用方就再也看不到请求体了。
    /// </summary>
    public static async Task<CapturedRequest> ToCapturedAsync(
        this HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = request.Headers.ToDictionary(
            h => h.Key,
            h => h.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // content headers（Content-Type / Content-Length）也并进来，
        // 这样断言可以用同一个 HeaderOrNull 取到
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = header.Value.ToArray();
            }
        }

        var uri = request.RequestUri;

        return new CapturedRequest
        {
            Method = request.Method.Method,
            Url = uri?.ToString() ?? "",
            Path = uri?.AbsolutePath ?? "",
            Headers = headers,
            Body = body,
            ContentType = request.Content?.Headers.ContentType?.ToString() ?? "",
        };
    }
}
