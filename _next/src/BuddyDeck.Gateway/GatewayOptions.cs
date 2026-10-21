namespace BuddyDeck.Gateway;

/// <summary>
/// 网关的运行时配置。
///
/// 对齐旧版 `config.js` 的键名 —— 用户的启动参数、环境变量、面板设置
/// 都是按那套名字存的，换名字等于让所有现存配置失效。
///
/// 命令行优先级高于环境变量：`--port 8791` 覆盖 `WORKBUDDY_PORT`。
/// 这样临时起一个测试实例时不用去改用户的环境。
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>监听端口。默认 8787，和旧版一致（客户端里写死的接入地址就是这个）。</summary>
    public int Port { get; set; } = 8787;

    /// <summary>监听地址。**默认只绑回环** —— 这是本机信任边界的核心。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>要求的 Bearer key。空字符串 = 不鉴权（和旧版语义一致）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// apiKey 是否被**显式指定**（命令行或环境变量），即使值是空串。
    ///
    /// ## 为什么需要它
    ///
    /// 「显式传了空串」（= 明确不鉴权）和「没传」（= 用默认/文件里的值）
    /// 是两件事。App 拉网关时总是显式传 `--api-key`（空也传，表示「不鉴权」），
    /// 如果不区分，网关启动时会把空 key 当成「没传」，回退去读 settings.json
    /// 里上次遗留的 key → 鉴权意外开启 → 客户端没带 key → 401。
    /// </summary>
    public bool ApiKeySpecified { get; set; }

    /// <summary>变体 id（workbuddy / workbuddy-ai）。</summary>
    public string Variant { get; set; } = Core.UpstreamVariant.China.Id;

    /// <summary>保险库路径（auth.cn.json / auth.ai.json）。空 = 按变体推导。</summary>
    public string VaultPath { get; set; } = "";

    /// <summary>用户没指定模型、或者指定的模型目录里没有时用哪个。</summary>
    public string DefaultModel { get; set; } = "hy3";

    /// <summary>日志文件目录。滚动文件写这里。</summary>
    public string LogDirectory { get; set; } = "";

    /// <summary>聊天请求体大小上限。旧版是 64 MB（要传图片 data URI）。</summary>
    public long MaxRequestBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 指标里「最近请求」保留多少条。默认 50（见 <see cref="MetricsOptions"/>）。
    ///
    /// 为什么要可配：排障时想看更多，长跑时想省内存 —— 同一个开关
    /// 两种场景都能满足，比写死一个值然后两边都不满意强。
    /// </summary>
    public int MetricsRecentCapacity { get; set; } = 50;

    /// <summary>
    /// 单次客户端请求内最多向上游尝试几次（含第一次）。默认 3。
    ///
    /// 为什么要它：上游限流时网关会冷却当前号、换下一个号重试
    /// （见 <c>GatewayApp.RunWithRetryAsync</c>）。次数不设上限就成了
    /// 死循环 —— 每个号被限一次，池子转一圈又一圈。
    ///
    /// 为什么默认 3：账号池里通常只有几个号，试满 3 个还全被限，
    /// 基本可以断定是全局限流（而不是某个号的问题），再试也是白试。
    /// </summary>
    public int MaxUpstreamAttempts { get; set; } = 3;

    /// <summary>
    /// 覆盖上游基地址（调试/端到端验证用）。
    ///
    /// 为什么需要：端到端验证要拿一个**真的 HTTP 假上游**替掉
    /// copilot.tencent.com，才能把「真 TCP 连接 + 真 SSE 分块传输」
    /// 那条链路也验到（内存里的假处理器验不了）。
    ///
    /// 空 = 用变体自带的真实地址。**生产不要设它。**
    /// </summary>
    public string UpstreamBaseOverride { get; set; } = "";

    /// <summary>解析命令行参数。支持 `--port 8791` 和 `--port=8791` 两种写法。</summary>
    public static GatewayOptions Parse(string[] args)
    {
        var options = new GatewayOptions();
        options.ApplyEnvironment();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

            var body = arg[2..];
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            string? value;

            if (eq >= 0)
            {
                value = body[(eq + 1)..];
                body = body[..eq];
            }
            else
            {
                // 下一个参数不是开关时才是值；否则当成布尔开关（值为空串）
                value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "";
            }

            options.Apply(body, value);
        }

        return options;
    }

    /// <summary>环境变量。命令行随后覆盖。</summary>
    private void ApplyEnvironment()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["port"] = Environment.GetEnvironmentVariable("WORKBUDDY_PORT"),
            ["host"] = Environment.GetEnvironmentVariable("WORKBUDDY_HOST"),
            ["api-key"] = Environment.GetEnvironmentVariable("WORKBUDDY_API_KEY"),
            ["variant"] = Environment.GetEnvironmentVariable("WORKBUDDY_VARIANT"),
            ["vault"] = Environment.GetEnvironmentVariable("WORKBUDDY_VAULT"),
            ["default-model"] = Environment.GetEnvironmentVariable("WORKBUDDY_DEFAULT_MODEL"),
            ["log-dir"] = Environment.GetEnvironmentVariable("WORKBUDDY_LOG_DIR"),
            ["upstream-base"] = Environment.GetEnvironmentVariable("WORKBUDDY_UPSTREAM_BASE"),
        };

        foreach (var (key, value) in map)
        {
            if (!string.IsNullOrEmpty(value)) Apply(key, value);
        }
    }

    private void Apply(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "port":
                Port = ParsePort(value, Port);
                break;
            case "host":
                if (value.Length > 0) Host = value;
                break;
            case "api-key":
                ApiKey = value;
                ApiKeySpecified = true;
                break;
            case "variant":
                if (value.Length > 0) Variant = value;
                break;
            case "vault":
                if (value.Length > 0) VaultPath = value;
                break;
            case "default-model":
                if (value.Length > 0) DefaultModel = value;
                break;
            case "log-dir":
                if (value.Length > 0) LogDirectory = value;
                break;
            case "max-request-bytes":
                if (long.TryParse(value, out var bytes) && bytes > 0) MaxRequestBytes = bytes;
                break;
            case "request-timeout":
                if (int.TryParse(value, out var seconds) && seconds > 0) RequestTimeoutSeconds = seconds;
                break;
            case "metrics-recent":
                if (int.TryParse(value, out var recent) && recent > 0) MetricsRecentCapacity = recent;
                break;
            case "max-attempts":
                if (int.TryParse(value, out var attempts) && attempts > 0) MaxUpstreamAttempts = attempts;
                break;
            case "upstream-base":
                if (value.Length > 0) UpstreamBaseOverride = value;
                break;
            default:
                // 未知开关忽略 —— 旧版客户端可能带着我们不认识的参数启动
                break;
        }
    }

    /// <summary>
    /// 端口解析。非法值**不静默回退** —— 悄悄用 8787 可能导致撞上用户
    /// 正在跑的网关，那比启动失败更糟（见 AGENTS.md 的铁律）。
    /// </summary>
    private static int ParsePort(string value, int fallback)
    {
        if (int.TryParse(value, out var port) && port is > 0 and <= 65535) return port;
        throw new ArgumentException($"端口必须是 1-65535 之间的整数，收到：{value}");
    }

    /// <summary>解析出来的变体。</summary>
    public Core.UpstreamVariant ResolveVariant()
        => Core.UpstreamVariant.Find(Variant)
           ?? throw new ArgumentException($"未知变体：{Variant}（可选 {string.Join(" / ", Core.UpstreamVariant.All.Select(v => v.Id))}）");

    /// <summary>
    /// 保险库路径。
    ///
    /// ## 三条规则（t47 修的真实缺陷）
    ///
    /// ```
    /// ① 没显式给        → AppContext.BaseDirectory/<变体文件名>   （原语义，未改）
    /// ② 给了**绝对路径** → 原样返回                                （原语义，未改）
    /// ③ 给了**相对路径** → 相对**用户启动时的 cwd** 解析成绝对路径   ← t47 新增
    /// ```
    ///
    /// ## ③ 为什么必须这么修（这是真实发生过的静默降级）
    ///
    /// 用户这样启动：
    /// ```
    /// cd 工作目录
    /// dotnet run --project src\BuddyDeck.Gateway -- --vault .local\vault.json
    /// ```
    ///
    /// 旧行为把 `.local\vault.json` **原样**返回。而进程的当前目录是
    /// `src\BuddyDeck.Gateway\`（`dotnet run` 会切过去），**不是用户的 shell cwd** ——
    /// 于是它去找 `src\BuddyDeck.Gateway\.local\vault.json`，那个文件不存在。
    ///
    /// **后果是最阴的那种**：
    /// ```
    /// 启动日志正常打印路径        ✓（打印的是用户给的那个字符串，看着对）
    /// GET /healthz              ✓ 200
    /// GET /v1/models            ✓ 200（不读保险库）
    /// POST /v1/chat/completions ✗ 401 not_signed_in「保险库里没有账号」
    /// ```
    /// **配置看起来被接受了、启动看起来正常，只有真正用到时才降级成「没账号」** ——
    /// 用户会以为是自己账号坏了。
    ///
    /// ## 为什么用「用户 cwd」而不是「报错要求绝对路径」
    ///
    /// 相对路径的**通用直觉**就是「相对于我敲命令时所在的地方」——
    /// 命令行工具（git、docker、dotnet 自己）全是这个语义。
    /// 报错要求绝对路径虽然也安全，但**违背直觉**、且要求用户改脚本。
    ///
    /// 关键实现点：cwd 必须在**进程启动那一刻**取（<see cref="StartupDirectory"/>），
    /// 不能在每次调用时取 —— 因为 `dotnet run` / 别处可能已经改过当前目录，
    /// 那时取到的是「被改过的」那个，而不是用户所在的那个。
    /// </summary>
    public string ResolveVaultPath()
    {
        if (string.IsNullOrEmpty(VaultPath))
        {
            // ① 没给：原语义不变 —— 程序运行目录
            var name = ResolveVariant().VaultFilename;
            return Path.Combine(AppContext.BaseDirectory, name);
        }

        // ② 绝对路径：原样（不做任何规范化，避免改变既有行为）
        if (Path.IsPathRooted(VaultPath)) return VaultPath;

        // ③ 相对路径：相对「用户启动时所在目录」
        return Path.GetFullPath(Path.Combine(StartupDirectory, VaultPath));
    }

    /// <summary>
    /// **用户启动网关时所在的目录**（进程启动那一刻的当前目录）。
    ///
    /// 为什么单独存一个静态字段而不是每次调 `Directory.GetCurrentDirectory()`：
    /// 那个值会**随进程运行而变**（`dotnet run`、别处的 `Directory.SetCurrentDirectory`
    /// 都会改它）。我们要的是「用户在哪敲的命令」，那是个**不可变的启动事实**。
    /// </summary>
    private static readonly string StartupDirectory = Directory.GetCurrentDirectory();

    /// <summary>
    /// 日志目录。没显式给就用程序运行目录下的 logs/。
    ///
    /// ⚠ 与保险库路径同款的相对路径问题（t47）—— 相对路径也按启动目录解析。
    /// </summary>
    public string ResolveLogDirectory()
    {
        if (string.IsNullOrEmpty(LogDirectory)) return Path.Combine(AppContext.BaseDirectory, "logs");
        return Path.IsPathRooted(LogDirectory)
            ? LogDirectory
            : Path.GetFullPath(Path.Combine(StartupDirectory, LogDirectory));
    }
}
