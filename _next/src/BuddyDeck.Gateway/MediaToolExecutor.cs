using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 执行模型的媒体工具调用（`generate_image` / `edit_image` /
/// `generate_video` / `image_to_video`）。
///
/// ## 为什么网关必须自己执行
///
/// 只有网关拿着 Miora 的凭证。客户端（Claude Code / pi / DSH）**不认识**
/// 这些工具，让它们执行等于没做。所以流程是：
///
/// ```
/// 网关注入工具定义（MediaTools.Inject）
///   → 模型回 tool_calls（工具名带 workbuddy_ 前缀）
///   → **网关抢在自己这里执行**（本类）
///   → 结果作为 role:"tool" 消息回填
///   → 让模型接着说
/// ```
///
/// ## 和客户端的边界（踩过的坑）
///
/// **只有带 `workbuddy_` 前缀的工具才由网关执行。**
/// 客户端的工具（Read / Bash / get_weather…）必须原样交回客户端 ——
/// 网关既没有那些实现，也不该替客户端跑。
///
/// 旧版踩过：非流式路径一开始没做这个区分，看到任何 tool_calls 都去执行。
/// 结果客户端调 `get_weather`，模型收到「未知工具：get_weather」，
/// 于是回了一句「天气工具好像不可用」—— **用户看到的是一切正常，
/// 只是工具永远查不出数据**。这种「静默降级成合理回答」是最难查的一类。
///
/// ## 结果为什么要写成那种文字
///
/// 模型拿到工具结果后常常**自己改写一通**，把地址弄丢或者加一堆技术说明。
/// 所以返回给它的文本里明确写了「把上面那行 markdown 图片原样放进回复里」——
/// 说清楚要它干什么，比让它自由发挥可靠得多。
/// </summary>
public sealed partial class MediaToolExecutor
{
    private readonly MioraClient _miora;
    private readonly ILogger<MediaToolExecutor> _log;

    /// <summary>
    /// 联网搜索 / 抓取 —— **可空**。
    ///
    /// 为 null 时那两个工具回"没配置"的明确失败，而不是抛 ——
    /// 生图那些工具不该因为搜索客户端缺席就一起坏掉。
    /// </summary>
    private readonly SearchClient? _search;

    public MediaToolExecutor(MioraClient miora, ILogger<MediaToolExecutor> log, SearchClient? search = null)
    {
        _miora = miora;
        _log = log;
        _search = search;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[agent] 执行工具 {Tool}")]
    private partial void LogTool(string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[agent] 工具 {Tool} 执行失败：{Message}")]
    private partial void LogToolFailed(string tool, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "[agent] 生图完成 {File}（模型 {Model}）")]
    private partial void LogImageDone(string file, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "[agent] {Label}任务已提交 {TaskId}")]
    private partial void LogVideoSubmitted(string label, string taskId);

    // ══════════════════════════════════════════════════════════
    //  联网搜索 / 抓取（用户实测反馈「搜索拓展没有」）
    // ══════════════════════════════════════════════════════════
    //
    // 这两个原来**完全没注册** —— 网关有 `/v1/search` 与 `/v1/webfetch` 端点，
    // 但它们只是 HTTP 接口，**模型不知道自己能联网**。
    // 实测：让模型"搜一下今天的新闻"，它只会说"我无法联网"。

    private async Task<ToolOutcome> WebSearchAsync(JsonObject args, CancellationToken ct)
    {
        if (_search is null) return new ToolOutcome(false, "搜索没有配置（这个实例没接 SearchClient）");

        var query = args["query"]?.GetValue<string>()?.Trim() ?? "";
        if (query.Length == 0) return new ToolOutcome(false, "缺少 query 参数");

        var max = ReadInt(args, "max_results") ?? 5;
        max = Math.Clamp(max, 1, 20);

        var result = await _search.SearchAsync(query, maxResults: max, ct: ct).ConfigureAwait(false);

        if (result.Results.Count == 0)
        {
            // ⚠ 空结果**也是成功**（不是"失败"）—— 模型需要知道"搜了但没找到"，
            //   那样它才会换个说法再搜，而不是以为工具坏了。
            return new ToolOutcome(true, $"没有搜到与「{query}」相关的结果。可以换一种说法再试。");
        }

        var sb = new StringBuilder();
        sb.Append("搜索结果（").Append(result.Results.Count).Append(" 条）：\n\n");

        for (var i = 0; i < result.Results.Count; i++)
        {
            var item = result.Results[i];
            sb.Append(i + 1).Append(". ").AppendLine(item.Title);
            if (!string.IsNullOrWhiteSpace(item.Site)) sb.Append("   来源：").AppendLine(item.Site);
            sb.Append("   ").AppendLine(item.Url);
            if (!string.IsNullOrWhiteSpace(item.Snippet)) sb.Append("   ").AppendLine(item.Snippet);
            sb.AppendLine();
        }

        sb.Append("回答时请引用上面的 URL。");

        return new ToolOutcome(true, sb.ToString());
    }

    private async Task<ToolOutcome> FetchPageAsync(JsonObject args, CancellationToken ct)
    {
        if (_search is null) return new ToolOutcome(false, "抓取没有配置（这个实例没接 SearchClient）");

        var url = args["url"]?.GetValue<string>()?.Trim() ?? "";
        if (url.Length == 0) return new ToolOutcome(false, "缺少 url 参数");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            // ⚠ 只放行 http(s)：`file:` / `javascript:` 这类绝不能让模型触发。
            return new ToolOutcome(false, "url 必须是 http(s) 开头");
        }

        var maxLength = ReadInt(args, "max_length") ?? 20000;
        maxLength = Math.Clamp(maxLength, 500, 100000);

        var page = await _search.FetchPageAsync(url, maxLength: maxLength, ct: ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(page.Title)) sb.Append("标题：").AppendLine(page.Title);
        sb.Append("URL：").AppendLine(page.Url);
        sb.AppendLine();
        sb.Append(page.Content);

        if (page.Content.Length >= maxLength)
        {
            // 如实告诉模型"截断了"—— 否则它会以为文章就这么长，
            // 然后给出"文章到此结束"这种错误结论。
            // ⚠ CA1305：插值字符串会跟随当前区域设置（千分位/非 ASCII 数字）。
            //   这里对模型输出，必须**固定 InvariantCulture** ——
            //   否则在阿拉伯语/印地语区域会输出本地数字，模型反而看不懂。
            var cap = maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine().AppendLine().Append("（内容已截断到 " + cap + " 字符）");
        }

        return new ToolOutcome(true, sb.ToString());
    }

    /// <summary>读一个可选整数参数。</summary>
    private static int? ReadInt(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        return null;
    }

    /// <summary>一次工具执行的结果。</summary>
    /// <param name="Ok">成功没有。</param>
    /// <param name="Text">回填给模型的文字。</param>
    public readonly record struct ToolOutcome(bool Ok, string Text);

    /// <summary>
    /// 把 tool_calls 分成「该网关执行的」和「客户端的」。
    ///
    /// 见类注释里的坑：**不能见到 tool_calls 就执行**。
    /// </summary>
    public static (List<JsonObject> Mine, List<JsonObject> Foreign) SplitCalls(JsonArray calls)
    {
        var mine = new List<JsonObject>();
        var foreign = new List<JsonObject>();

        foreach (var item in calls)
        {
            if (item is not JsonObject call) continue;

            var name = call["function"]?["name"]?.GetValue<string>();
            if (MediaTools.IsMediaTool(name)) mine.Add(call);
            else foreign.Add(call);
        }

        return (mine, foreign);
    }

    /// <summary>
    /// 回填用的结果数组。
    ///
    /// ⚠ **必须覆盖全部调用（含外部工具）** —— 上游要求每个 tool_call
    /// 都有对应的 tool 消息，缺一个就报错。外部工具给一条说明性回复。
    /// </summary>
    public static List<ToolOutcome> ResultsForAll(
        IReadOnlyList<JsonObject> all,
        IReadOnlyList<JsonObject> mine,
        IReadOnlyList<ToolOutcome> mineResults)
    {
        var results = new List<ToolOutcome>();

        foreach (var call in all)
        {
            // 用引用相等找它在 mine 里的位置 —— 两边的元素来自同一个 JsonArray，
            // 是同一些对象实例（所以不能用值相等，JsonObject 也没实现它）。
            var index = -1;
            for (var i = 0; i < mine.Count; i++)
            {
                if (ReferenceEquals(mine[i], call)) { index = i; break; }
            }

            results.Add(index >= 0
                ? mineResults[index]
                : new ToolOutcome(false, "（这个工具由客户端执行，网关没有处理）"));
        }

        return results;
    }

    /// <summary>顺序执行一组调用（顺序而不是并发：生图很重，并发会把上游打爆）。</summary>
    public async Task<List<ToolOutcome>> ExecuteAsync(
        IReadOnlyList<JsonObject> calls,
        Func<string, string> makeUrl,
        CancellationToken ct)
    {
        var results = new List<ToolOutcome>();

        foreach (var call in calls)
        {
            var name = call["function"]?["name"]?.GetValue<string>() ?? "";
            LogTool(name);
            results.Add(await RunAsync(call, makeUrl, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>执行一个工具调用。</summary>
    private async Task<ToolOutcome> RunAsync(JsonObject call, Func<string, string> makeUrl, CancellationToken ct)
    {
        var rawName = call["function"]?["name"]?.GetValue<string>() ?? "";
        var bare = MediaTools.StripPrefix(rawName);
        if (bare is null) return new ToolOutcome(false, $"未知工具：{rawName}");

        JsonObject args;
        try
        {
            var raw = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
            args = JsonNode.Parse(raw) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return new ToolOutcome(false,
                $"工具参数不是合法 JSON：{Truncate(call["function"]?["arguments"]?.GetValue<string>() ?? "", 200)}");
        }

        try
        {
            return bare switch
            {
                "generate_image" => await GenerateImageAsync(args, makeUrl, ct).ConfigureAwait(false),
                "edit_image" => await EditImageAsync(args, makeUrl, ct).ConfigureAwait(false),
                "generate_video" => await GenerateVideoAsync(args, ct).ConfigureAwait(false),
                "image_to_video" => await ImageToVideoAsync(args, ct).ConfigureAwait(false),
                // 联网搜索与抓取（用户实测反馈「搜索拓展没有」）——
                // 这两个原来**根本没注册**，所以模型不知道自己能联网。
                "web_search" => await WebSearchAsync(args, ct).ConfigureAwait(false),
                "fetch_page" => await FetchPageAsync(args, ct).ConfigureAwait(false),
                _ => new ToolOutcome(false, $"工具 {bare} 还没实现"),
            };
        }
        catch (MioraException ex)
        {
            LogToolFailed(bare, ex.Message);
            return new ToolOutcome(false, $"工具执行失败：{ex.Message}");
        }
    }

    private async Task<ToolOutcome> GenerateImageAsync(JsonObject args, Func<string, string> makeUrl, CancellationToken ct)
    {
        var prompt = args["prompt"]?.GetValue<string>()?.Trim() ?? "";
        if (prompt.Length == 0) return new ToolOutcome(false, "缺少 prompt 参数");

        var body = new JsonObject { ["prompt"] = prompt };
        if (args["size"]?.GetValue<string>() is { Length: > 0 } size) body["size"] = size;

        var result = await _miora.SubmitAsync("image", "generate", body, null, ct).ConfigureAwait(false);
        if (result.Files.Count == 0)
        {
            return new ToolOutcome(false, result.ErrorMessage ?? "生图失败，上游没有返回图片");
        }

        var media = await _miora.DownloadAsync(result.Files[0].Url, ct).ConfigureAwait(false);
        var url = makeUrl(media.FileName);
        LogImageDone(media.FileName, result.Model);

        return new ToolOutcome(true, ImageResultText("图片已生成", url, result.Files[0].FileKey));
    }

    private async Task<ToolOutcome> EditImageAsync(JsonObject args, Func<string, string> makeUrl, CancellationToken ct)
    {
        var prompt = args["prompt"]?.GetValue<string>()?.Trim() ?? "";
        if (prompt.Length == 0) return new ToolOutcome(false, "缺少 prompt 参数");

        var image = args["image"]?.GetValue<string>()?.Trim() ?? "";
        if (image.Length == 0) return new ToolOutcome(false, "缺少 image 参数");

        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["images"] = new JsonArray(MediaTools.ToMediaPayload(image)),
        };
        if (args["size"]?.GetValue<string>() is { Length: > 0 } size) body["size"] = size;

        var result = await _miora.SubmitAsync("image", "edit", body, null, ct).ConfigureAwait(false);
        if (result.Files.Count == 0)
        {
            return new ToolOutcome(false, result.ErrorMessage ?? "改图失败，上游没有返回图片");
        }

        var media = await _miora.DownloadAsync(result.Files[0].Url, ct).ConfigureAwait(false);
        LogImageDone(media.FileName, result.Model);

        return new ToolOutcome(true, ImageResultText("图片已修改", makeUrl(media.FileName), result.Files[0].FileKey));
    }

    private async Task<ToolOutcome> GenerateVideoAsync(JsonObject args, CancellationToken ct)
    {
        var body = BuildVideoArgs(args);
        return await SubmitVideoAsync(body, "文生视频", ct).ConfigureAwait(false);
    }

    private async Task<ToolOutcome> ImageToVideoAsync(JsonObject args, CancellationToken ct)
    {
        var first = args["first_frame"]?.GetValue<string>()?.Trim() ?? "";
        if (first.Length == 0) return new ToolOutcome(false, "缺少 first_frame 参数");

        var input = new JsonObject { ["first_frame_image"] = MediaTools.ToMediaPayload(first) };

        var last = args["last_frame"]?.GetValue<string>()?.Trim() ?? "";
        if (last.Length > 0) input["last_frame_image"] = MediaTools.ToMediaPayload(last);

        var body = BuildVideoArgs(args);
        body["input"] = input;

        return await SubmitVideoAsync(body, last.Length == 0 ? "图生视频" : "首尾帧视频", ct).ConfigureAwait(false);
    }

    private static JsonObject BuildVideoArgs(JsonObject args)
    {
        var body = new JsonObject();

        if (args["prompt"]?.GetValue<string>()?.Trim() is { Length: > 0 } prompt) body["prompt"] = prompt;
        if (ReadDouble(args["duration"]) is { } duration) body["duration"] = duration;
        if (args["resolution"]?.GetValue<string>() is { Length: > 0 } resolution) body["resolution"] = resolution;
        if (args["aspect_ratio"]?.GetValue<string>() is { Length: > 0 } ratio) body["aspect_ratio"] = ratio;

        return body;
    }

    /// <summary>
    /// 提交视频任务并**立刻返回**（不等渲染）。
    ///
    /// 为什么不等：视频要几分钟到十几分钟。把等待压在工具调用里会让
    /// 整条 agent 循环挂住，而模型自己也不该干等 —— 旧版的注释写得很清楚：
    /// 「不要在这里等待或轮询 —— 提交就结束了」。
    /// </summary>
    private async Task<ToolOutcome> SubmitVideoAsync(JsonObject body, string label, CancellationToken ct)
    {
        var submit = await _miora.SubmitRawAsync("video", body, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(submit.TaskId))
        {
            return new ToolOutcome(false, "提交视频任务失败，上游没有返回任务 id");
        }

        LogVideoSubmitted(label, submit.TaskId);

        return new ToolOutcome(true, string.Join("\n",
        [
            $"{label}任务已提交。",
            "",
            $"taskId: {submit.TaskId}",
            $"查询进度: /v1/video/status/{submit.TaskId}",
            "",
            "视频要几分钟渲染。告诉用户任务已提交，并把这个查询地址给他。",
            "不要在这里等待或轮询 —— 提交就结束了。",
        ]));
    }

    /// <summary>
    /// 工具返回给模型的文字。
    ///
    /// ⚠ 明确让模型「把 markdown 原样放进回复」：模型拿到工具结果后常常
    /// 自己改写一通，把地址弄丢或者加一堆技术细节。**说清楚要它干什么，
    /// 比让它自由发挥可靠得多。**
    /// </summary>
    public static string ImageResultText(string title, string url, string fileKey)
    {
        var lines = new List<string>
        {
            $"{title}。",
            "",
            $"![生成结果]({url})",
            "",
            "把上面那行 markdown 图片原样放进你的回复里，用户就能看到图片。",
            "不要改写这个地址，也不要额外解释技术细节。",
        };

        if (!string.IsNullOrEmpty(fileKey)) lines.Add($"（fileKey: {fileKey}）");

        return string.Join("\n", lines);
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
