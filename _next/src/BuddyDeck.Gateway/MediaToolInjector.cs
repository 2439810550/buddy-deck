using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 把媒体工具**注入**聊天请求体。
///
/// ## 为什么单独一个类型
///
/// 注入逻辑要动请求体字符串（"prepared" 是已经过管线的 JSON 文本），
/// 而那段逻辑要能**被单独测试** —— 塞进 GatewayApp 就只能靠起真网关来测。
///
/// ## 为什么注入的是「原始类型」而不是「增量的」
///
/// 每次请求都重新算一遍 JSON（而不是缓存住解析结果再深拷贝）——
/// 请求体是可变的 JsonNode，缓存共享实例会被下游的 `RenameModel` 之类改坏。
/// 注入本身很轻（四个工具的 schema 拼一次），不值得为它冒共享可变状态的风险。
/// </summary>
public sealed partial class MediaToolInjector
{
    private readonly ILogger<MediaToolInjector> _log;

    /// <param name="log">日志。</param>
    /// <param name="enabled">总开关。false 时 <see cref="InjectInto"/> 原样返回 —— 保留这个口子，便于部署时快速关掉。</param>
    public MediaToolInjector(ILogger<MediaToolInjector> log, bool enabled = true)
    {
        _log = log;
        Enabled = enabled;
    }

    /// <summary>总开关。关掉之后聊天路径不再注入媒体工具。</summary>
    public bool Enabled { get; }

    [LoggerMessage(Level = LogLevel.Information, Message = "[media-tools] 注入 {Count} 个工具：{Tools}")]
    private partial void LogInjected(int count, string tools);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[media-tools] 跳过 {Count} 个（客户端已声明）：{Tools}")]
    private partial void LogSkipped(int count, string tools);

    /// <summary>
    /// 往一段「已过管线」的请求体 JSON 里注入媒体工具。
    ///
    /// @returns 注入后的 JSON 文本。**解析失败时原样返回** ——
    ///          宁可让上游报错，也不要因为注入把客户端的请求改坏。
    /// </summary>
    public string InjectInto(string preparedJson, bool suppressTools = false)
    {
        if (!Enabled) return preparedJson;

        // 用户明确说「别调工具」→ 一个都不注入。
        //
        // ⚠ 这个信号必须由调用方从**原始**请求体里判断：
        //   管线遇到 `tool_choice: "none"` 会把 tools 和 tool_choice
        //   一起删掉（见 RequestPipeline.NormalizeToolChoice），
        //   所以到了这里已经没有任何痕迹 —— 只能从外面传进来。
        //   不传的话就是「用户说了别调工具、网关却硬塞四个进去」，完全静默。
        if (suppressTools) return preparedJson;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(preparedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return preparedJson;
        }

        if (node is not JsonObject body) return preparedJson;

        var (injected, skipped) = MediaTools.Inject(body);

        // ⚠ CA1873：实参位置上不能有计算 —— 日志关掉时那些拼接就是白做的。
        //   所以先判级别、再在局部变量里算好，最后才传（HANDOVER 坑 6）。
        if (skipped.Count > 0 && _log.IsEnabled(LogLevel.Debug))
        {
            var names = string.Join(", ", skipped);
            LogSkipped(skipped.Count, names);
        }

        if (injected.Count == 0) return preparedJson;

        if (_log.IsEnabled(LogLevel.Information))
        {
            var names = string.Join(", ", injected);
            LogInjected(injected.Count, names);
        }

        return body.ToJsonString();
    }
}
