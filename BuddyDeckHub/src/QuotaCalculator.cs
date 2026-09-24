namespace BuddyDeckHub;

/// <summary>
/// 计费 —— 照抄 one-api 那套「倍率层」。
///
///     实际消耗配额 = 输入token × 输入倍率 + 输出token × 输出倍率
///     再乘上分组的倍率
///
/// 为什么要插这一层，而不是直接用 token 数记账：
///
///   1. 换计量口径不用动账本。今天按 token，明天想按积分，只改这里的函数
///   2. 不同模型天然差价。deepseek-v4-flash 有一百万上下文，
///      和 glm-4.6 的 168K 不是一个量级的东西
///   3. 免费模型填倍率 0 就完事，不用到处写特例判断
///
/// 倍率的标定基准：把 deepseek-v4.1-flash 当 1.0。
/// 它是默认模型、也是最便宜的那档，拿它当尺子最直观。
/// </summary>
public static class QuotaCalculator
{
    /// <summary>1 个「配额单位」= 多少 token。取值让常用模型算出来的数好看一点。</summary>
    public const long QuotaPerToken = 1;

    /// <summary>
    /// 模型倍率表。输入/输出分开，因为输出通常贵得多。
    /// 名字匹配是「前缀 + 不区分大小写」，方便一个前缀覆盖一个系列。
    /// </summary>
    private static readonly (string Prefix, double In, double Out, string Note)[] Rates =
    [
        // ── 免费档：倍率 0，不扣配额 ─────────────────────────────
        // 这两个是上游给的免费模型。签到积分耗尽后仍然能用，
        // 所以「随便用用」的人放这个组里最合适，完全不消耗额度。
        ("deepseek-v4.1-flash", 0.0, 0.0, "免费"),
        ("glm-5.3-flash",       0.0, 0.0, "免费"),

        // ── 便宜档 ───────────────────────────────────────────────
        ("deepseek-v3-1",  0.5, 1.0, "轻量"),
        ("deepseek-v3-2",  0.5, 1.0, "轻量"),
        ("deepseek-v3-0324", 0.5, 1.0, "轻量"),
        ("deepseek-r1",    0.6, 1.2, "轻量推理"),
        ("glm-4.6",        0.6, 1.2, "轻量"),
        ("glm-4.7",        0.6, 1.2, "轻量"),
        ("hunyuan-3b",     0.2, 0.4, "极小"),
        ("hunyuan-7b",     0.3, 0.6, "很小"),
        ("hunyuan-chat",   0.6, 1.2, "轻量"),
        ("hunyuan-2.0",    0.8, 1.6, "轻量"),

        // ── 标准档：基准 1.0 ─────────────────────────────────────
        ("glm-5.0",        1.0, 2.0, "标准"),
        ("glm-5.1",        1.0, 2.0, "标准"),
        ("glm-4.6v",       1.0, 2.0, "标准多模态"),

        // ── 强模型档：贵 ─────────────────────────────────────────
        ("glm-5.3",        2.0, 4.0, "强"),
        ("glm-5v",         2.0, 4.0, "强多模态"),
        ("deepseek-v4-pro", 2.5, 5.0, "旗舰"),
        ("deepseek-v4-flash", 1.2, 2.4, "长上下文"),
        ("kimi-k2.5",      1.5, 3.0, "长上下文"),
        ("kimi-k2.6",      1.5, 3.0, "长上下文"),
        ("kimi-k2-thinking", 2.5, 5.0, "推理"),
        ("kimi-k3",        2.5, 5.0, "旗舰"),
        ("minimax-m2.5",   1.2, 2.4, "标准"),
        ("minimax-m2.7",   1.5, 3.0, "标准"),
        ("minimax-m3",     2.0, 4.0, "强"),
        ("hy3",            1.5, 3.0, "标准"),
        ("auto",           1.0, 2.0, "自动"),
    ];

    /// <summary>倍率表对外暴露一份只读视图，管理界面要展示。</summary>
    public static IReadOnlyList<object> RateTable() =>
        Rates.Select(r => (object)new
        {
            model = r.Prefix,
            input = r.In,
            output = r.Out,
            note = r.Note,
        }).ToList();

    /// <summary>查一个模型的倍率。没登记的按「未定价」处理（保守，按标准档算）。</summary>
    public static (double In, double Out, string Note) RateOf(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        foreach (var r in Rates)
            if (m.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                return (r.In, r.Out, r.Note);
        return (1.0, 2.0, "未定价");
    }

    /// <summary>这个模型是不是免费的（倍率 0）。</summary>
    public static bool IsFree(string model)
    {
        var (i, o, _) = RateOf(model);
        return i == 0.0 && o == 0.0;
    }

    /// <summary>
    /// 算这次调用消耗多少配额。
    ///
    /// <paramref name="groupRate"/> 是分组倍率：free 组填 0，整单不扣。
    /// </summary>
    public static long Compute(string model, long promptTokens, long completionTokens, double groupRate)
    {
        if (groupRate <= 0) return 0;
        var (inRate, outRate, _) = RateOf(model);
        if (inRate == 0 && outRate == 0) return 0;

        var raw = promptTokens * inRate + completionTokens * outRate;
        return (long)Math.Ceiling(raw * groupRate * QuotaPerToken);
    }

    /// <summary>
    /// 预扣估算 —— 流式请求开始时还不知道会产出多少。
    ///
    /// 照抄中转站的做法：按 max_tokens 预扣一笔，流结束后按真实 usage 结算。
    /// 差额退还。这样既不放过余额不足的人，也不冤枉正常用户。
    /// </summary>
    public static long Estimate(string model, long promptTokens, long maxTokens, double groupRate)
    {
        if (groupRate <= 0) return 0;
        // 客户端常常不传 max_tokens。这时候按 prompt 的量级估一个，
        // 别用 4096 那种偏大的值把正常用户挡住。
        var guess = maxTokens > 0 ? maxTokens : Math.Clamp(promptTokens, 512, 8192);
        return Compute(model, promptTokens, guess, groupRate);
    }

    /// <summary>把配额数换算成人话，给管理界面用。</summary>
    public static string Human(long quota) => quota switch
    {
        < 1_000 => $"{quota}",
        < 1_000_000 => $"{quota / 1000.0:0.#}K",
        _ => $"{quota / 1_000_000.0:0.##}M",
    };
}
