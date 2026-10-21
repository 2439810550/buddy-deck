using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 媒体与联网端点的共用工具：注入给模型的**媒体工具定义**。
///
/// ## 为什么不是关键词匹配
///
/// 第一版旧实现用的是关键词：命中「画」「生成」就去生图。两个死穴：
///
///   ① **死板**。「生成一个视频」命中「生成」→ 当成生图处理，用户说生成视频
///      拿到一张图。补词表只能修一个个案。
///   ② **一接外挂客户端就废**。那段代码前面有个 `!hasToolUse(request)` ——
///      一看客户端带了 tools 就跳过关键词。于是 Claude Code / pi / DSH 里
///      说「画一只猫」，模型只会回「我没办法直接画图哦」。
///
/// ## 正确做法：把工具注册给模型
///
/// 照 WorkBuddy 自己的做法（它挖 `app.asar` 里的 miora-mcp 插件）：
/// 把工具通过 function calling 注册给模型，**模型读 description 自己判断**
/// 该不该调、调哪个。判断权在模型，不在正则。
///
/// ## 网关为什么必须自己执行
///
/// 只有网关拿着 Miora 的凭证。客户端（Claude Code 等）不认识这些工具，
/// 让它去执行等于没做。所以：网关注入定义 → 上游回 tool_calls →
/// **网关自己执行** → 结果作为 tool 消息回填 → 让模型接着说。
///
/// ## description 的写法
///
/// 直接照旧版 `media-tools.js` 的原文（那是官方调好的措辞），
/// 特别是「什么时候该用、什么时候不该用」的边界说明 ——
/// 那部分才是真正影响模型判断的内容。
/// </summary>
public static class MediaTools
{
    /// <summary>
    /// 工具名前缀。
    ///
    /// 加前缀避免和客户端的同名工具撞（比如某些客户端自己有 `image` 工具）。
    /// 这也是网关判断「这个 tool_call 该不该我执行」的依据。
    /// </summary>
    public const string ToolPrefix = "workbuddy_";

    /// <summary>四个媒体工具的名字（**未加前缀**）。</summary>
    public static readonly IReadOnlyList<string> Names =
        ["generate_image", "edit_image", "generate_video", "image_to_video",
         // ⚠ 联网搜索与抓取（用户实测反馈「搜索拓展没有」）——
         //   这两个原来**不在这个清单里**，所以 `IsMediaTool` 判它们不是我们的、
         //   注入时也不会带上定义 ⇒ 模型永远不知道自己能联网。
         "web_search", "fetch_page"];

    /// <summary>加了前缀的工具名。</summary>
    public static string Prefixed(string name) => ToolPrefix + name;

    /// <summary>全部工具名（加了前缀）。</summary>
    public static IReadOnlyList<string> AllPrefixedNames => [.. Names.Select(Prefixed)];

    /// <summary>这个工具名是不是我们的。</summary>
    public static bool IsMediaTool(string? name)
        => !string.IsNullOrEmpty(name) && name.StartsWith(ToolPrefix, StringComparison.Ordinal);

    /// <summary>去掉前缀拿原始名。不是我们的工具返回 null。</summary>
    public static string? StripPrefix(string? name)
        => IsMediaTool(name) ? name![ToolPrefix.Length..] : null;

    /// <summary>
    /// 转成 OpenAI 的 `tools` 数组格式。
    /// </summary>
    /// <param name="only">只要这些（按**未加前缀**的名）。null = 全部。</param>
    public static JsonArray BuildTools(IReadOnlyList<string>? only = null)
    {
        var array = new JsonArray();

        foreach (var definition in Definitions)
        {
            if (only is not null && !only.Contains(definition.Name, StringComparer.Ordinal)) continue;

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Prefixed(definition.Name),
                    ["description"] = definition.Description,
                    // ⚠ 必须 DeepClone：JsonNode 不能同时挂在两个父节点上
                    //   （第二次 Add 会抛 "The node already has a parent"）。
                    //   而同一个 definition 会被**多次**使用 —— 一个进程里
                    //   每个请求都注入一次。
                    ["parameters"] = JsonNode.Parse(definition.ParametersJson)!.DeepClone(),
                },
            });
        }

        return array;
    }

    /// <summary>
    /// 把媒体工具**合并**进客户端请求的 `tools`。
    ///
    /// 两条规则（都重要）：
    ///
    ///   ① **客户端已经有同名工具 → 不动**。尊重客户端的定义 ——
    ///      万一是它自己的实现，抢过来执行反而是坏的。
    ///   ② **客户端一个 tools 都没有 → 也要注入**。这正是
    ///      Claude Code / pi / DSH 的场景，也是旧版关键词方案失效的地方。
    /// </summary>
    /// <param name="request">客户端的请求体（**会被就地改动** —— 调用方传自己的副本）。</param>
    /// <param name="enabled">总开关（false 时什么都不做）。</param>
    /// <returns>注入和跳过的工具名（好记日志、也好让测试断言）。</returns>
    public static (List<string> Injected, List<string> Skipped) Inject(JsonObject request, bool enabled = true)
    {
        var injected = new List<string>();
        var skipped = new List<string>();

        if (!enabled) return (injected, skipped);

        var existing = request["tools"] as JsonArray ?? [];

        // 客户端已声明的函数名
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in existing)
        {
            var name = tool?["function"]?["name"]?.GetValue<string>() ?? tool?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name)) declared.Add(name);
        }

        var additions = new JsonArray();
        foreach (var definition in Definitions)
        {
            var name = Prefixed(definition.Name);

            if (declared.Contains(name))
            {
                skipped.Add(name);
                continue;
            }

            // ⚠ 只记名字，**不在这里构造节点** —— 节点一旦挂进 additions
            //   就再也不能挂进 merged（JsonNode 只能有一个父节点），
            //   从临时数组搬过去会抛 "The node already has a parent"。
            injected.Add(name);
        }

        if (injected.Count > 0)
        {
            // 客户端**已有的**工具节点要 DeepClone 再搬 —— 它们原来挂在
            // request 的旧 tools 数组上（马上被替换掉），直接 Add 会因父节点冲突抛异常。
            var merged = new JsonArray();
            foreach (var tool in existing) merged.Add(tool?.DeepClone());

            foreach (var definition in Definitions)
            {
                var name = Prefixed(definition.Name);
                if (!injected.Contains(name, StringComparer.Ordinal)) continue;

                merged.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = definition.Description,
                        ["parameters"] = JsonNode.Parse(definition.ParametersJson)!.DeepClone(),
                    },
                });
            }

            request["tools"] = merged;
        }

        return (injected, skipped);
    }

    /// <summary>一个工具的定义。</summary>
    private sealed record Definition(string Name, string Description, string ParametersJson);

    /// <summary>
    /// 四个工具的定义。
    ///
    /// ⚠ description 直接沿用旧版 `media-tools.js` 的官方措辞 —— 那是调过的，
    /// 尤其是**否定清单**（"Do NOT call it when…"）那几行，它们防止模型
    /// 在用户只是「问」而不是「要」的时候乱调工具。
    /// ParametersJson 用字符串而不是 JsonObject，是为了让定义表读起来
    /// 和旧版并排对得上（迁移时能逐字比对）。
    /// </summary>
    private static readonly IReadOnlyList<Definition> Definitions =
    [
        new(
            "generate_image",
            """
            Generate an image from a text description and show it to the user.

            Use this when the user asks you to CREATE, DRAW, PAINT, or GENERATE a picture/image/illustration/photo.
            Examples of when to call it:
              - "画一只戴墨镜的橘猫" (draw an orange cat with sunglasses)
              - "generate an image of a cyberpunk city"
              - "帮我做个 logo 草图" (make a logo sketch for me)

            Do NOT call it when the user is:
              - asking HOW to draw something, or discussing image generation in general
              - asking about the tool itself: how long it takes, what it costs, what sizes are supported,
                how many you can make, whether it is available. Those are QUESTIONS — answer them in words.
              - saying something vague like "让我看看猫长什么样" / "show me what a cat looks like" —
                that is a request for information, not a request to create an image. Ask what they want first.
              - asking for a flowchart / architecture diagram / UML / mermaid (write the code instead)
              - asking you to WRITE a prompt for another tool (just write the prompt)

            Write the prompt in the same language the user used, describing what should be IN the picture.
            Do not include instruction words like "画" or "generate" in the prompt itself — describe the subject.
            """,
            """
            {"type":"object","properties":{
              "prompt":{"type":"string","description":"What the image should show. Describe the subject, style, and composition. Do not include the words \"draw\" or \"generate\"."},
              "size":{"type":"string","description":"Optional pixel size such as \"1024x1024\", \"1024x1536\", or \"1536x1024\". Omit to let the service decide."}},
             "required":["prompt"]}
            """),

        new(
            "edit_image",
            """
            Edit an existing image according to a text instruction.

            Use this when the user wants to MODIFY an image they provided or that was generated earlier —
            change colors, remove/add an element, restyle it, and so on.
            The image can be given as a public https URL, a data: URI, or a fileKey returned by a previous
            image tool call (prefer fileKey — it avoids a base64 round-trip).

            Do NOT use this to create a brand-new image from scratch — use the text-to-image tool for that.
            """,
            """
            {"type":"object","properties":{
              "prompt":{"type":"string","description":"The edit instruction, e.g. \"make the background a sunset\"."},
              "image":{"type":"string","description":"The source image: https URL | data:image/...;base64,... | fileKey from a previous tool result."},
              "size":{"type":"string","description":"Optional output size."}},
             "required":["prompt","image"]}
            """),

        new(
            "generate_video",
            """
            Generate a short video from a text description.

            Use this when the user asks for a VIDEO, CLIP, ANIMATION, or MOVIE.
            Examples:
              - "生成一个视频" (generate a video)
              - "一只猫在跳舞的短片" (a short clip of a cat dancing)
              - "make an animation of waves at sunset"

            IMPORTANT — video generation takes MINUTES and is much more expensive than an image.
            Only call it when the user clearly asked for video. If the request is ambiguous between
            a still image and a video, ask the user which they want instead of guessing.

            Do NOT call it when the user is asking ABOUT video generation rather than asking FOR a video:
              - "视频生成要多久" / "how long does video take"  → answer the question in words
              - "视频生成贵吗" / "is video expensive"          → answer the question in words
              - "支持生成视频吗" / "can you make videos"       → say yes, then ask what they want

            This tool returns a task id. The user can poll for the result; do not wait for it in a loop.
            """,
            """
            {"type":"object","properties":{
              "prompt":{"type":"string","description":"What the video should show. Describe the subject and the motion."},
              "duration":{"type":"number","description":"Optional target duration in seconds."},
              "resolution":{"type":"string","description":"Optional resolution such as \"720p\" or \"1080p\"."},
              "aspect_ratio":{"type":"string","description":"Optional aspect ratio such as \"16:9\" or \"9:16\"."}},
             "required":["prompt"]}
            """),

        new(
            "image_to_video",
            """
            Generate a video that animates a given still image. Optionally specify an ending frame and the
            video will interpolate between the two.

            Use this when the user has an image (provided or generated earlier) and wants it to MOVE or
            become a video — e.g. "让这张图动起来" (make this image move), "把首尾帧做成视频".

            The image may be a public https URL, a data: URI, or a fileKey from a previous tool result.
            """,
            """
            {"type":"object","properties":{
              "first_frame":{"type":"string","description":"Starting frame: https URL | data:image/...;base64,... | fileKey."},
              "last_frame":{"type":"string","description":"Optional ending frame. Same formats as first_frame."},
              "prompt":{"type":"string","description":"Optional description of the motion between the frames."},
              "duration":{"type":"number","description":"Optional target duration in seconds."},
              "resolution":{"type":"string","description":"Optional resolution such as \"720p\"."},
              "aspect_ratio":{"type":"string","description":"Optional aspect ratio."}},
             "required":["first_frame"]}
            """),

        // ── 联网搜索与抓取（用户实测反馈「搜索拓展没有」）──────────
        //
        // ⚠ 这两个原来**完全没注入** —— 网关有 `/v1/search` 与 `/v1/webfetch`
        //   两个端点，但它们只是给客户端直接调的 HTTP 接口，
        //   模型**不知道有它们**，所以永远不会请求。
        //
        //   实测确认过：让模型"搜一下今天的新闻"，它只会说"我无法联网"——
        //   因为它手上的工具清单里从来没有搜索这一项。
        new(
            "web_search",
            """
            Search the web and get current information.

            Use this when the user asks about something you cannot know from training alone:
              - recent events, news, prices, releases, scores
              - anything the user explicitly says to look up ("搜一下", "查查", "search for")
              - facts you are unsure about and that may have changed

            Do NOT use it when:
              - the question is about general knowledge that does not change (math, history, concepts)
              - the user is asking you to WRITE something rather than find something
              - you already have the answer in this conversation

            The results come back as readable text with source URLs — cite them when you answer.
            """,
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"The search query, in the same language as the user's question."},
              "max_results":{"type":"integer","description":"How many results to return. Defaults to 5."}},
             "required":["query"]}
            """),

        new(
            "fetch_page",
            """
            Fetch a web page and return its readable text.

            Use this when the user gives you a URL and wants to know what is on it, or when a search
            result looks important enough to read in full.

            Do NOT use it for URLs you were given as an image to edit — use the image tool for that.
            """,
            """
            {"type":"object","properties":{
              "url":{"type":"string","description":"The absolute http(s) URL to fetch."},
              "max_length":{"type":"integer","description":"Optional cap on returned characters."}},
             "required":["url"]}
            """),
    ];

    /// <summary>
    /// 把工具参数的「图片」字段转成上游要的 media payload。
    ///
    /// 上游接受三种：`url` / `base64` / `file_key`。按输入形态判断：
    /// ```
    /// http(s)://…         → { url }
    /// data:image/…;base64,… → { base64 }（剥掉 data URI 前缀）
    /// bga/… 或 file/…     → { file_key }
    /// 其他                 → { base64 }（裸 base64）
    /// ```
    /// </summary>
    public static JsonObject ToMediaPayload(string? input)
    {
        var value = (input ?? "").Trim();

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["url"] = value };
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            return comma < 0
                ? new JsonObject { ["base64"] = value }
                : new JsonObject { ["base64"] = value[(comma + 1)..] };
        }

        // fileKey（上游自己的文件键）
        if (value.StartsWith("bga/", StringComparison.Ordinal) || value.StartsWith("file/", StringComparison.Ordinal))
        {
            return new JsonObject { ["file_key"] = value };
        }

        return new JsonObject { ["base64"] = value };
    }
}
