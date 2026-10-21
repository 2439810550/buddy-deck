using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuddyDeck.App.Services;

/// <summary>
/// 把**旧版软件的账号库**读成新版能导入的东西。
///
/// ## 它解决的是什么
///
/// 用户实测原话：**「没法把老软件里的账户导入进来」**。
/// 旧版把账号明文存在 `~/.workbuddy-api/accounts.cn.json`，
/// 而新版账号页原先只有「粘贴登录文件 JSON 内容」一个文本框 ——
/// 普通用户不会去翻隐藏目录、更不会复制 48 KB 的 JSON。
///
/// 这个类型做三件事：**找到文件 → 解析 → 校验/去重**，并给出
/// 「将导入 N 个 / 跳过 M 个（原因）」的结果。
///
/// ## 为什么它是纯逻辑、不碰 WPF
///
/// 照 `TimeText` / `AccountPresentation` / `ImageInput` 的先例：
/// 逻辑放这里就能脱离界面用普通 xUnit 跑。**文件选择器**在
/// <see cref="IFilePicker"/> 后面（WPF 实现在 `WpfFilePicker`），
/// 因为 `OpenFileDialog` 要 STA 线程 + 窗口句柄。
///
/// ## ⚠ 一处真实数据的坑，与一处**已被推翻的**假设
///
/// **① 字段名与新版基本一致，但可选字段不能写空值。** 照 `to_vault_entry`
/// （`import_accounts.py:54-74`）：必填字段逐个显式映射，可选字段
/// **有才带** —— 否则会往保险库里写一堆 `null`，而读到 null 的下游
/// 得各写一遍判空。
///
/// **② 「`label` 是 GBK 双重编码乱码」这个假设是错的，已废弃。**
/// `tools/import_accounts.py:27-51` 有一段 `fix_label` 声称要把
/// 「绠€鍗曞揩涔?」还原成「简单快乐」，我曾据此实现了一整套还原。
/// **实测真实文件 `~/.workbuddy-api/accounts.cn.json：label 全部是正常
/// 中文**（主号 / 简单快乐 / 杨佳葳 …），没有任何乱码 ——
/// 之前看到的"乱码"是 `Get-Content` 的**终端显示问题**。
/// 详见 <see cref="LabelOf"/> 的注释（那里记了为什么必须**删掉**
/// 而不是留着无害）。
/// </summary>
public static class AccountImport
{
    /// <summary>旧版账号库的默认位置（`~/.workbuddy-api/accounts.cn.json`）。</summary>
    public static string DefaultLegacyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy-api",
        "accounts.cn.json");

    /// <summary>旧版数据目录（给界面显示「去哪找」用）。</summary>
    public static string LegacyDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy-api");

    // ══════════════════════════════════════════════════════════
    // 结果类型
    // ══════════════════════════════════════════════════════════

    /// <summary>解析/读取的失败分类。</summary>
    public enum Failure
    {
        None = 0,

        /// <summary>文件不存在（含「旧版目录里没有这个文件」）。</summary>
        Missing = 1,

        /// <summary>读文件出错（权限、被占用）。</summary>
        IoError = 2,

        /// <summary>不是合法 JSON。</summary>
        NotJson = 3,

        /// <summary>是 JSON 但没有 `accounts` 数组 —— 不是旧版账号库的形状。</summary>
        WrongShape = 4,

        /// <summary>`accounts` 是空数组。</summary>
        NoAccounts = 5,
    }

    /// <summary>一条账号被跳过的原因。</summary>
    public enum SkipReason
    {
        /// <summary>没有 token —— 导进来也用不了。</summary>
        NoToken = 0,

        /// <summary>没有 uid —— 无法定位身份。</summary>
        NoUid = 1,

        /// <summary>本次文件内重复（同一 id 出现两次）。</summary>
        DuplicateInFile = 2,

        /// <summary>形状不是对象（数组里混进了字符串之类）。</summary>
        NotAnObject = 3,
    }

    /// <summary>一条被跳过的账号。</summary>
    /// <param name="Display">给用户看的标识（uid 前 8 位或标签）。</param>
    /// <param name="Reason">为什么跳过。</param>
    public sealed record Skipped(string Display, SkipReason Reason);

    /// <summary>
    /// 一次解析的结果。
    ///
    /// <see cref="Accounts"/> 是**已经转成新版 VaultEntry 形状的 JSON**，
    /// 而不是某个 C# 模型 —— 因为导入最终要交给网关的
    /// `ImportAccountsAsync(payload)`，那条路吃的是 JSON 文本。
    /// 中间多加一层模型只会多一次序列化往返。
    /// </summary>
    public sealed record Result
    {
        public bool Ok => Reason == Failure.None;

        /// <summary>失败原因（成功时 <see cref="Failure.None"/>）。</summary>
        public Failure Reason { get; init; }

        /// <summary>失败时给用户看的一句话（成功时为空串）。</summary>
        public string Message { get; init; } = "";

        /// <summary>转换后的账号（新版形状）。</summary>
        public IReadOnlyList<JsonObject> Accounts { get; init; } = [];

        /// <summary>被跳过的账号及原因。</summary>
        public IReadOnlyList<Skipped> Skipped { get; init; } = [];

        /// <summary>文件里原本有多少条（含被跳过的）。</summary>
        public int TotalInFile { get; init; }

        /// <summary>给界面的「将导入 N 个账号」。</summary>
        public string Summary => Skipped.Count == 0
            ? $"将导入 {Accounts.Count} 个账号"
            : $"将导入 {Accounts.Count} 个账号，跳过 {Skipped.Count} 个";

        /// <summary>跳过原因的汇总文案（按原因归类计数）。</summary>
        public string SkipSummary
        {
            get
            {
                if (Skipped.Count == 0) return "";

                var groups = Skipped
                    .GroupBy(s => s.Reason)
                    .Select(g => $"{Describe(g.Key)} {g.Count()} 个");

                return "跳过：" + string.Join("，", groups);
            }
        }

        /// <summary>把结果包成可以喂给网关的导入载荷。</summary>
        public string ToPayload()
        {
            var doc = new JsonObject
            {
                ["version"] = 1,
                ["accounts"] = new JsonArray(Accounts.Select(a => (JsonNode)a.DeepClone()).ToArray()),
            };

            return doc.ToJsonString();
        }

        /// <summary>
        /// 确认框上要显示的**多行**文案：总数 + 跳过汇总 + 跳过明细。
        ///
        /// ## ⚠ 为什么这个格式化放在这里，而不是 ViewModel 里
        ///
        /// 因为 `AccountsViewModel` 用了 `[ObservableProperty]` / `[RelayCommand]`，
        /// 而 **CommunityToolkit 的源生成器不会为 `&lt;Compile Include&gt;`
        /// 链接进测试程序集的文件运行** —— 链接 ViewModel 必然
        /// CS0246 + CS0759。这是**机制限制**（本项目已实测确认三次）。
        ///
        /// 所以按既有约定：**要测的逻辑放 Services，ViewModel 只留绑定 glue。**
        /// 这段文案逻辑是纯函数，放这里就能被 45+ 条测试里的几条直接打。
        /// </summary>
        public string ConfirmationText()
        {
            var lines = new List<string> { Summary };

            if (Skipped.Count == 0) return string.Join("\n", lines);

            lines.Add(SkipSummary);

            // 明细最多列几条 —— 再多会把确认框撑满，而用户真正要判断的是
            // 「有没有我的号被跳过」，前几条 + 总数足够
            foreach (var skipped in Skipped.Take(MaxListedSkips))
            {
                lines.Add($"· {skipped.Display}：{Describe(skipped.Reason)}");
            }

            if (Skipped.Count > MaxListedSkips)
            {
                lines.Add($"· …还有 {Skipped.Count - MaxListedSkips} 个");
            }

            return string.Join("\n", lines);
        }

        internal static Result Fail(Failure reason, string message)
            => new() { Reason = reason, Message = message };
    }

    /// <summary>确认框里最多列几条跳过明细。</summary>
    public const int MaxListedSkips = 5;

    /// <summary>跳过原因的中文说明（进界面）。</summary>
    public static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.NoToken => "没有登录凭据",
        SkipReason.NoUid => "缺少用户标识",
        SkipReason.DuplicateInFile => "文件内重复",
        SkipReason.NotAnObject => "格式不对",
        _ => "未知原因",
    };

    // ══════════════════════════════════════════════════════════
    // ① 从默认位置读（「一键导入」）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从旧版默认位置读（`~/.workbuddy-api/accounts.cn.json`）。
    ///
    /// 「一键导入」走这条。**文件不存在时给的是可操作的提示**
    /// （告诉用户去哪找），而不是一句「失败」——
    /// 这个功能的失败态几乎总是「那台机器上没装过旧版」，
    /// 用户需要知道的是「所以你应该用文件导入」。
    /// </summary>
    public static Result FromLegacyDirectory(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultLegacyPath : path;

        if (!File.Exists(target))
        {
            return Result.Fail(Failure.Missing,
                $"没找到旧版账号库（{LegacyDirectory}）。" +
                "如果旧版装在这台机器上，请确认它至少登录过一次；" +
                "否则请用「从文件导入」选择你导出的账号文件。");
        }

        return FromFile(target);
    }

    // ══════════════════════════════════════════════════════════
    // ② 从指定文件读（「从文件导入」）
    // ══════════════════════════════════════════════════════════

    /// <summary>读一个文件并解析。所有失败都有明确分类与中文提示。</summary>
    public static Result FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Fail(Failure.Missing, "没有选择文件");
        }

        string raw;
        try
        {
            if (!File.Exists(path))
            {
                return Result.Fail(Failure.Missing, "找不到这个文件（可能已被移动或删除）");
            }

            raw = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Result.Fail(Failure.IoError, $"读不了这个文件：{ex.Message}");
        }

        return Parse(raw);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 解析（纯函数，测试的主力入口）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析旧版账号库的 JSON 文本。
    ///
    /// **这是整个类型的心脏**，测试主要打这里 —— 它不碰文件系统，
    /// 所以空/超大/畸形/字段缺失都能直接构造。
    /// </summary>
    public static Result Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Fail(Failure.NotJson, "文件是空的");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            // ⚠ 把解析器的原始信息带上：用户拿到的文件可能真的是别的 JSON，
            //   一句「格式不对」帮不了他定位。
            return Result.Fail(Failure.NotJson, $"这不是合法的 JSON：{ex.Message}");
        }

        if (node is not JsonObject doc)
        {
            return Result.Fail(Failure.WrongShape, "账号文件的顶层应当是一个 JSON 对象");
        }

        if (doc["accounts"] is not JsonArray accounts)
        {
            // ⚠ 明确说「这个形状不对」而不是「没有账号」——
            //   用户可能选成了 `auth.cn.json`（登录凭据，另一个文件）。
            return Result.Fail(Failure.WrongShape,
                "这个文件里没有 accounts 列表 —— 它可能不是旧版的账号库" +
                "（旧版的账号库文件名是 accounts.cn.json）");
        }

        if (accounts.Count == 0)
        {
            return Result.Fail(Failure.NoAccounts, "这个账号库里一个账号都没有");
        }

        var converted = new List<JsonObject>();
        var skipped = new List<Skipped>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in accounts)
        {
            if (item is not JsonObject account)
            {
                skipped.Add(new Skipped("（不是对象）", SkipReason.NotAnObject));
                continue;
            }

            var (entry, reason) = Convert(account, seenIds);
            if (entry is null)
            {
                skipped.Add(new Skipped(DisplayOf(account), reason!.Value));
                continue;
            }

            converted.Add(entry);
        }

        if (converted.Count == 0)
        {
            // 全被跳过了 —— **不能报成功**（否则用户点导入后什么都不会发生，
            // 而界面说「成功 0 个」比说清原因更没用）。
            return new Result
            {
                Reason = Failure.NoAccounts,
                Message = $"文件里有 {accounts.Count} 条记录，但没有一条可用：{SkipSummaryOf(skipped)}",
                Skipped = skipped,
                TotalInFile = accounts.Count,
            };
        }

        return new Result
        {
            Accounts = converted,
            Skipped = skipped,
            TotalInFile = accounts.Count,
        };
    }

    private static string SkipSummaryOf(IReadOnlyList<Skipped> skipped)
        => string.Join("，", skipped
            .GroupBy(s => s.Reason)
            .Select(g => $"{Describe(g.Key)} {g.Count()} 个"));

    /// <summary>
    /// 一条旧版账号 → 新版 VaultEntry 形状。
    ///
    /// @returns 转换成功时第二项为 null；失败时第一项为 null、第二项是原因。
    /// </summary>
    private static (JsonObject? Entry, SkipReason? Reason) Convert(JsonObject account, HashSet<string> seenIds)
    {
        var accessToken = Text(account, "accessToken");
        var refreshToken = Text(account, "refreshToken");

        // 两个 token 都没有 = 这条记录没有任何可用凭据。
        // ⚠ 只看 accessToken 太严：旧版有些记录 accessToken 空但 refreshToken 在，
        //   那种导进来还能靠刷新救活，不该丢。
        if (accessToken.Length == 0 && refreshToken.Length == 0)
        {
            return (null, SkipReason.NoToken);
        }

        var uid = Text(account, "uid");
        if (uid.Length == 0)
        {
            return (null, SkipReason.NoUid);
        }

        var id = Text(account, "id");
        if (id.Length == 0) id = uid;   // 旧版偶尔没有 id —— 用 uid 兜底，别丢账号

        if (!seenIds.Add(id))
        {
            return (null, SkipReason.DuplicateInFile);
        }

        var entry = new JsonObject
        {
            ["id"] = id,
            ["uid"] = uid,
            ["enterpriseId"] = Text(account, "enterpriseId"),
            ["domain"] = Text(account, "domain"),
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["expiresAtMs"] = Number(account, "expiresAtMs"),
            ["label"] = LabelOf(account),
        };

        // 可选字段：**有才带**（对齐 import_accounts.py:71-73）——
        // 写一堆 null 进去会让下游每处都得判空。
        foreach (var name in (string[])["refreshExpiresAtMs", "addedAt", "lastSeenAt", "lastCheckinAt"])
        {
            if (account[name] is JsonNode value && !IsEmpty(value))
            {
                entry[name] = value.DeepClone();
            }
        }

        return (entry, null);
    }

    private static bool IsEmpty(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0,
        _ => false,
    };

    /// <summary>
    /// 给界面看的标识：标签（没有就给「账号 xxxxxxxx」）。
    ///
    /// 直接转发到 <see cref="LabelOf"/> —— 保留这个名字是因为调用点
    /// （跳过列表）读起来是「这条记录的显示名」，语义比「label」宽。
    /// </summary>
    private static string DisplayOf(JsonObject account) => LabelOf(account);

    // ══════════════════════════════════════════════════════════
    // ④ label（**不做任何"乱码还原"** —— 理由见下）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 取 label；没有时给一个能认出来的默认名。
    ///
    /// ## ⚠⚠ 这里曾经有一整套「GBK 双重编码还原」，已**整个删除**
    ///
    /// 起因：`tools/import_accounts.py:27-51` 里有一段 `fix_label`，
    /// 注释写着「旧版面板历史上把 UTF-8 中文按 GBK 解过一次再存回去」，
    /// 并举了「简单快乐 → 绠€鍗曞揩涔?」的例子。我据此实现了
    /// latin-1 → GBK 的还原 + 特征检测。
    ///
    /// **实测推翻了这个前提**（读真实文件 `~/.workbuddy-api/accounts.cn.json`）：
    /// ```
    /// label 实际值：主号 / 少钦号 / 简单快乐 / 杨佳葳 / 张天翼 …
    /// 全部是正常中文，一个乱码都没有。
    /// ```
    /// 之前看到的「乱码」是 **`Get-Content` 的终端显示问题**，不是文件内容 ——
    /// 用 `read` 工具或按字节读，中文完全正常。
    ///
    /// ## 为什么不是「留着无害」，而是必须删
    ///
    /// 它**有能力改坏正确的数据**：
    ///
    /// 那种形态**本身有损、不可逆** —— `€`（U+20AC）超出 Latin-1 的
    /// 0x00–0xFF 范围，按 latin-1 编回会丢成 `?`；即使改用 GBK 编回，
    /// `€` 仍然不可逆（得回 `简单快�?`）。
    ///
    /// 换句话说：**真实数据里没有它，而一旦真有、还原也救不回来** ——
    /// 一个「只在错误前提成立时才有用、且用错就改坏数据」的转换，
    /// 留在数据路径上没有正收益。
    ///
    /// ⇒ 标签**直接透传**。
    ///
    /// **经验保留（判断编码问题必须用 `read` 工具或字节层，不能用
    /// `Get-Content` 的终端显示），实现删除。**
    /// </summary>
    private static string LabelOf(JsonObject account)
    {
        var raw = Text(account, "label");
        if (raw.Length > 0) return raw;

        // 没有标签时给一个能认出来的默认名 —— 空标签在列表里是一行空白，
        // 用户分不清「这个号叫什么」和「界面坏了」。
        var uid = Text(account, "uid");
        return uid.Length >= 8 ? $"账号 {uid[..8]}" : "账号";
    }

    // ══════════════════════════════════════════════════════════
    // 小工具
    // ══════════════════════════════════════════════════════════

    /// <summary>读一个字符串字段；不是字符串或缺失时返回空串。</summary>
    private static string Text(JsonObject account, string name)
        => account[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>
    /// 读一个数值字段。
    ///
    /// ⚠ **逐类型试**，不能只试 long —— 本项目已经踩过两次
    /// （`UsageLedger` 的 `{"prompt_tokens":2541}` 与
    /// `AnthropicEndpoints` 的 `max_tokens`）：JSON 数值按**最窄类型**
    /// 背书，`TryGetValue&lt;long&gt;` 对 int 背书的值返回 false **且不抛**，
    /// 于是过期时间被静默读成 0。
    /// </summary>
    private static long Number(JsonObject account, string name)
    {
        if (account[name] is not JsonValue v) return 0;

        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed)) return parsed;
        return 0;
    }
}

/// <summary>
/// 「让用户选一个文件」这个动作的抽象。
///
/// ## 为什么要有这个接口
///
/// `Microsoft.Win32.OpenFileDialog.ShowDialog()` 有两个硬约束：
///   · 需要 **STA 线程**（xUnit 默认 MTA）
///   · 需要**窗口句柄 / 消息泵**（无头测试环境没有）
///
/// 抽出来之后，<see cref="AccountImport"/> 的解析与校验逻辑
/// 就能在普通测试里 100% 覆盖，而 WPF 那一行只出现在
/// `WpfFilePicker` 里。
///
/// @returns 用户选中的路径；**取消选择时返回 null**（那是正常操作，不是错误）。
/// </summary>
public interface IFilePicker
{
    /// <summary>弹一个打开文件的对话框。返回选中的路径；取消返回 null。</summary>
    string? PickJsonFile(string title);

    /// <summary>
    /// 弹一个**保存**文件的对话框（导出用）。返回选中的路径；取消返回 null。
    ///
    /// ⚠ 与 <see cref="PickJsonFile"/> 分开而不是合并成一个带参方法：
    ///   打开/保存是两个不同的系统对话框（默认文件名、覆盖确认的行为都不同），
    ///   合并会退化成"传个 bool 决定弹哪个"—— 那种签名在调用点看不出意图。
    /// </summary>
    string? PickSaveFile(string title, string suggestedName);
}
