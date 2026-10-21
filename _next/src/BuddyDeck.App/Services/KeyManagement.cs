using System.Globalization;

namespace BuddyDeck.App.Services;

/// <summary>
/// 「密钥」页的**纯逻辑**：显示格式、状态判定、校验、错误文案。
///
/// ## 为什么它独立于界面
///
/// 照本项目既有约定（`StatsPresentation` / `ModelsPresentation` /
/// `AccountPresentation` / `ShellNavigation` 同一模式）：
///
/// `KeysViewModel` 要用 `[ObservableProperty]` / `[RelayCommand]`，
/// 而 **CommunityToolkit 的源生成器不为 `&lt;Compile Include&gt;` 链接进
/// 测试程序集的文件运行** —— 链接 ViewModel 必然 CS0246 + CS0759
/// （本项目已多次实测确认）。所以**要测的逻辑放这里，VM 只留绑定 glue**。
///
/// ## 它做什么
///
/// ```
/// 前缀显示      明文只出现一次，之后一律显示前缀
/// 状态判定      active / revoked（以及"已撤销但仍在列表里"）
/// 时间格式化    创建时间 → 相对时间 / 绝对时间
/// 名称校验      创建前的本地预检（省一次往返、错误更早更清楚）
/// 错误文案      把网关/网络的失败翻译成用户能读懂的话
/// ```
///
/// ## ⚠ 它**故意不做**的一件事：不持有明文
///
/// `IssuedApiKey` 的明文**只在创建那一刻出现一次**（结构性保证，
/// 见 `KeyStore.Issue` 注释）。本文件的所有类型**都没有能装明文的字段**，
/// 唯一处理明文的函数是 <see cref="NewKeyNotice"/> —— 它把明文**包成一段
/// 一次性提示文案**，调用方展示完就该丢掉，不存进任何 VM 属性。
///
/// **为什么要在注释里说这么细**：GET 列表里永远不回明文，所以"不小心
/// 把明文缓存起来"最可能发生在**创建成功那一刻**。把这条写在纯逻辑层，
/// 是因为它约束的是**数据形状**（没有字段可装），不只是"注意别存"。
/// </summary>
public static class KeyManagement
{
    /// <summary>密钥前缀标记（与 KeyStore 生成的格式一致：`bdk-` + 8 位随机段）。</summary>
    public const string KeyPrefixMarker = "bdk-";

    /// <summary>
    /// 名称长度上限。
    ///
    /// 为什么要有上限：名称会进列表、可能进日志。没有上限时一个
    /// 几 MB 的名称能把列表布局撑坏、把日志刷爆。
    /// 64 是给「我的笔记本 - 公司 CI（备用）」这类描述留足余量的值。
    /// </summary>
    public const int MaxNameLength = 64;

    // ══════════════════════════════════════════════════════════
    // 状态
    // ══════════════════════════════════════════════════════════

    /// <summary>密钥状态。</summary>
    public enum KeyState
    {
        /// <summary>可用。</summary>
        Active = 0,

        /// <summary>已撤销 —— 不可恢复（撤销是终态，不是禁用）。</summary>
        Revoked = 1,
    }

    /// <summary>状态的中文名（直接进界面）。</summary>
    public static string StateLabel(KeyState state) => state switch
    {
        KeyState.Active => "可用",
        KeyState.Revoked => "已撤销",
        _ => "未知",
    };

    /// <summary>
    /// 状态对应的**资源 key**（界面按它取颜色，不在这里定色值）。
    ///
    /// 与 `StatusAppearance` 的分工一致：纯逻辑只决定"该用哪个 key"，
    /// **具体色值由主题字典管** —— 否则换主题要改 C#。
    /// </summary>
    public static string StateResourceKey(KeyState state) => state switch
    {
        KeyState.Active => "SystemFillColorSuccessBrush",
        KeyState.Revoked => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    };

    /// <summary>
    /// 是不是"已撤销"。
    ///
    /// ⚠ 单独一个函数而不是让界面判 `state == Revoked`：
    /// 上游将来可能加"过期"状态，届时**只有这里要改**。
    /// </summary>
    public static bool IsRevoked(KeyState state) => state == KeyState.Revoked;

    /// <summary>
    /// 这一行还允不允许点「撤销」。
    ///
    /// 已撤销的不给按钮 —— **撤销是终态**（不提供"恢复"）。
    /// 给一个点了没反应（或报错）的按钮，比不给按钮更糟：
    /// 用户会以为按钮坏了。
    /// </summary>
    public static bool CanRevoke(KeyState state) => state == KeyState.Active;

    // ══════════════════════════════════════════════════════════
    // 前缀显示
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 列表里显示的密钥串。
    ///
    /// ## ⚠ 显示的永远是**前缀**，绝不是完整密钥
    ///
    /// 完整密钥只在创建那一刻由服务端返回一次；之后任何界面、
    /// 任何接口都拿不到它。所以这个函数的输入本来就只是 prefix。
    ///
    /// 这里额外做的两件事：
    /// ```
    /// ① 补 `…` 后缀 → 让人一眼看出"这不是完整密钥，别拿去用"
    /// ② 前缀为空时给 `(未知)` → 而不是显示一个空白的、看起来像 bug 的格子
    /// ```
    ///
    /// ①的理由不只是好看：**用户会把界面上看到的字符串复制去试**。
    /// 不加省略号的话，他可能以为自己复制的就是密钥本身。
    /// </summary>
    public static string DisplayPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "(未知)";

        var trimmed = prefix.Trim();

        // 已经带省略号的（上游或旧数据可能带），不重复加
        return trimmed.EndsWith("…", StringComparison.Ordinal) ? trimmed : trimmed + "…";
    }

    /// <summary>
    /// 名称的显示值。空名称给一个明确的占位，而不是空白。
    ///
    /// ⚠ 空名称是**合法**的（`Issue(name = "")` 允许），不是错误。
    /// 但列表里一片空白会让人以为数据坏了，所以显示成 `(未命名)`。
    /// </summary>
    public static string DisplayName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "(未命名)" : name.Trim();

    // ══════════════════════════════════════════════════════════
    // 时间
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 创建时间的显示。
    ///
    /// ## 为什么"相对时间"要配一个绝对时间的兜底
    ///
    /// 列表里一眼看「3 天前创建」比看 `2026-10-04 12:00` 快得多。
    /// 但相对时间**在两种情况下没用**：
    ///   · 很久以前（「421 天前」不如直接给日期）
    ///   · 时间在未来（时钟不同步 / 上游时间漂移 →「-3 小时前」很奇怪）
    ///
    /// 所以这里按这三档走，而不是一律相对时间。
    /// </summary>
    /// <param name="createdAtMs">创建时刻（Unix 毫秒）。</param>
    /// <param name="now">现在。**显式传入** —— 否则这个函数不可测。</param>
    public static string DisplayCreatedAt(long createdAtMs, DateTimeOffset now)
    {
        if (createdAtMs <= 0) return "(未知)";

        var created = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs);
        var delta = now - created;

        // 未来时间（时钟漂移）：给绝对时间，别显示成负数
        if (delta < TimeSpan.Zero) return Absolute(created);

        if (delta < TimeSpan.FromMinutes(1)) return "刚刚";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} 小时前";

        // 30 天以内用相对时间；更久给日期（「421 天前」没有信息量）
        if (delta < TimeSpan.FromDays(30)) return $"{(int)delta.TotalDays} 天前";

        return Absolute(created);
    }

    /// <summary>绝对时间（本地时区，`yyyy-MM-dd`）。</summary>
    private static string Absolute(DateTimeOffset when)
        => when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>悬停提示用的完整时间（精确到分 + 时区）。</summary>
    public static string TooltipCreatedAt(long createdAtMs)
    {
        if (createdAtMs <= 0) return "创建时间未知";

        return DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    // ══════════════════════════════════════════════════════════
    // 创建前的本地校验
    // ══════════════════════════════════════════════════════════

    /// <summary>校验结果。</summary>
    /// <param name="Ok">能不能创建。</param>
    /// <param name="Error">不能创建时的原因（可直接显示）。能创建时为空串。</param>
    public readonly record struct NameCheck(bool Ok, string Error);

    /// <summary>
    /// 校验用户输入的名称。
    ///
    /// ## 为什么要在本地先校验
    ///
    /// 名称是**纯本地的展示字段**（不参与鉴权，见 `ApiKeyRecord.Name` 注释）。
    /// 也就是说服务端**未必会认真校验它** —— 发一个超长名称上去，
    /// 可能被静默截断、也可能原样存下来然后在列表里撑坏布局。
    ///
    /// **本地校验的作用不是"替服务端把关"，而是"让错误在更早、更清楚的地方出现"。**
    /// 往返一次再报错，用户看到的是一个网络延迟后的失败，不如当场告诉他。
    /// </summary>
    public static NameCheck ValidateName(string? name)
    {
        // 空名称合法（未命名密钥是合理用法）
        if (string.IsNullOrWhiteSpace(name)) return new NameCheck(true, "");

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            return new NameCheck(false, $"名称最长 {MaxNameLength} 个字符（当前 {trimmed.Length} 个）");
        }

        // 控制字符会破坏列表渲染与日志（换行尤其）
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return new NameCheck(false, "名称里不能有换行或控制字符");
            }
        }

        return new NameCheck(true, "");
    }

    /// <summary>
    /// 创建时用的名称：把空白归一成"未提供"。
    ///
    /// 传空串给 `Issue` 与传 null 语义相同，但**显式归一**能让调用点
    /// 只有一种形状 —— 少了这条，两个调用点可能一个传 `""` 一个传 `null`，
    /// 将来 `Issue` 的默认值语义一变就只有一个坏。
    /// </summary>
    public static string NormalizeName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "" : name.Trim();

    // ══════════════════════════════════════════════════════════
    // 新建密钥的一次性提示
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 创建成功后给用户看的提示 —— **明文唯一一次出现的地方**。
    ///
    /// ## 措辞是功能的一部分
    ///
    /// 这条提示必须让用户明白三件事，缺一件他就会来问（或者更糟：以为丢了）：
    /// ```
    /// ① 这是完整密钥
    /// ② 只显示这一次，关掉就再也拿不到
    /// ③ 现在就该复制走
    /// ```
    ///
    /// **"只显示一次"必须写在最显眼的位置** —— 用户点掉对话框之后
    /// 就再也找不回来了（结构性保证：`ApiKeyRecord` 里没有明文可存）。
    /// 如果把这句话藏在小字里，用户会在三天后才发现自己没存。
    ///
    /// @param plaintext 服务端返回的完整密钥。**调用方展示完就该丢弃** ——
    ///                  不要存进 VM 属性、不要写日志。
    /// </summary>
    public static string NewKeyNotice(string plaintext, string? name)
    {
        var label = DisplayName(name);
        return $"""
            密钥「{label}」创建成功。

            {plaintext}

            ⚠ 请立即复制保存 —— 这是唯一一次显示完整密钥。
            关闭后只能看到前缀，无法再次查看或找回。
            """;
    }

    // ══════════════════════════════════════════════════════════
    // 错误文案
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一次操作的失败翻译成用户能读懂的话。
    ///
    /// ## 为什么不能直接把上游消息透出去
    ///
    /// 这里和签到/积分那两个接口**不一样**：那边上游的 `msg` 是写给用户看的
    /// （「您的使用量已超出频率限制…」），直接透出比自己编准。
    ///
    /// 而**管理面（`/admin/*`）的失败是本地网关产生的**，消息里可能带：
    /// ```
    /// · 文件路径（部署结构）
    /// · 内部异常文本
    /// · 英文技术术语（not_found / invalid_request_error）
    /// ```
    /// 这些对用户没有帮助，还有泄露部署信息的风险。
    /// 所以管理面的失败**翻译成本地话术**，原始消息丢日志。
    ///
    /// @param operation 操作名（「创建」「撤销」「刷新列表」）—— 拼进提示里，
    ///                  让用户知道**是哪一步**失败了（列表刷不出来和创建失败
    ///                  是两回事，处置也不同）。
    /// </summary>
    public static string FailureMessage(string operation, string? rawError)
    {
        var detail = Detail(rawError);
        return detail.Length == 0
            ? $"{operation}失败"
            : $"{operation}失败：{detail}";
    }

    /// <summary>
    /// 从原始错误里提炼一句人话。
    ///
    /// 已知的映射（其余原样展示，但**截断**）：
    /// ```
    /// 404          → 网关还没挂密钥端点（很可能就是后端未接线）
    /// 401 / 403    → 网关 key 不对（本地面板与网关的鉴权不匹配）
    /// 连接失败      → 网关没起来
    /// ```
    ///
    /// ⚠ `404` 这条值得单独说：**它最可能的成因是"后端未接线"**，
    /// 而那正是 t75 开工前核查出来的状态。把它写成一句明确的话，
    /// 比让用户看到 "not_found" 然后来问要好。
    /// </summary>
    public static string Detail(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError)) return "";

        var text = rawError.Trim();

        // 太长就截断 —— 长错误正文对用户没价值，还会撑坏提示框
        const int limit = 200;
        if (text.Length > limit) text = text[..limit] + "…";

        if (text.Contains("404", StringComparison.Ordinal)
            || text.Contains("not_found", StringComparison.OrdinalIgnoreCase))
        {
            return "网关还没有密钥接口（404）—— 该功能的后端可能尚未接线";
        }

        if (text.Contains("401", StringComparison.Ordinal)
            || text.Contains("403", StringComparison.Ordinal)
            || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "网关拒绝了这次请求（鉴权失败）—— 检查面板里的 API Key 设置";
        }

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("无法连接", StringComparison.Ordinal)
            || text.Contains("No connection", StringComparison.OrdinalIgnoreCase))
        {
            return "连不上网关 —— 它可能没有在运行";
        }

        return text;
    }

    // ══════════════════════════════════════════════════════════
    // 列表排序与统计
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 列表排序：**可用的在前，然后按创建时间倒序**。
    ///
    /// ## 为什么这样排
    ///
    /// ```
    /// 可用的在前  → 用户打开这页通常是想拿一个能用的 key
    /// 新的在前    → 刚创建的 key 最可能就是他要找的那个
    /// ```
    ///
    /// 已撤销的排后面、也按时间倒序 —— 保留它们是为了**追溯**
    /// （「这条是什么时候撤的」），不是给日常使用。
    ///
    /// ⚠ 排序键里带 `Id` 作为**最终决胜键**：创建时间相同的两条
    /// （同一毫秒内建了两个）如果不加决胜键，`OrderBy` 是不稳定的，
    /// 每次刷新顺序可能变 —— 用户会觉得"列表在乱跳"。
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> rows,
        Func<T, KeyState> stateOf,
        Func<T, long> createdAtOf,
        Func<T, string> idOf)
        => [.. rows
            .OrderBy(r => IsRevoked(stateOf(r)) ? 1 : 0)
            .ThenByDescending(createdAtOf)
            .ThenBy(idOf, StringComparer.Ordinal)];

    /// <summary>
    /// 列表摘要（「共 5 个，3 个可用」）。
    ///
    /// 空列表返回空串 —— 那时界面显示的是空态，不是"共 0 个"。
    /// 两个都显示会重复，且空态更有指导性。
    /// </summary>
    public static string Summary<T>(IReadOnlyList<T> rows, Func<T, KeyState> stateOf)
    {
        if (rows.Count == 0) return "";

        var active = rows.Count(r => !IsRevoked(stateOf(r)));
        return active == rows.Count
            ? $"共 {rows.Count} 个，全部可用"
            : $"共 {rows.Count} 个，{active} 个可用";
    }

    /// <summary>空态提示（列表为空时显示）。</summary>
    public const string EmptyHint = "还没有签发过密钥。创建一个，用它替代明文 token 接入客户端。";

    /// <summary>
    /// 撤销前的确认文案。
    ///
    /// ## 为什么必须确认、且要说清"不可恢复"
    ///
    /// 撤销是**终态**（不提供恢复），而且后果是**立刻的**：
    /// 用这个 key 的客户端下一次请求就会 401。
    /// 用户可能在多个地方配了它（CI、另一台机器、某个脚本），
    /// **撤销之后他自己未必记得全都改了**。
    ///
    /// 所以确认文案要把后果说全，而不是一句"确定吗？"。
    /// </summary>
    public static string RevokeConfirm(string? name)
        => $"""
            确定撤销密钥「{DisplayName(name)}」？

            撤销后无法恢复，所有正在使用它的客户端会立即开始收到 401。
            如果它被配在多处（CI、其他机器、脚本），那些地方都需要换新密钥。
            """;
}
