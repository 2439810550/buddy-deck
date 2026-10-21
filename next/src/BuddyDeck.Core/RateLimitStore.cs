using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 限流历史的**落盘存储** —— 让"哪些模型被限流过、何时解除"重启不丢。
///
/// ## 为什么需要它（用户实测反馈）
///
/// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，**但是显示全绿**。
/// 应该告诉用户这些模型限流了、**啥时候解除限流**」。
///
/// `AccountPool.RateLimits` 已经在内存里累积了这条信息（成功不清），
/// 但它**只在进程内** —— 重启面板就全没了，用户看到的又是"全绿"。
///
/// ## 设计要点
///
/// · **键用 uid（36 位完整）**，不用截断 tag ——
///   实测：积分端点的 accountTag 是 uid 前 8 位，**相等匹配永远失败**。
///   存储必须用稳定完整键，展示层要短再自己截。
/// · **原子写**：与 Vault 同款（临时文件 + 替换）——
///   这个文件记录的是"该信任哪些模型"，写一半断电不能留半截。
/// · **坏文件不抛**：以空历史起步 + 记 Warning ——
///   抛了会让整个池起不来；而"没有历史"只是降级（界面少一块），不是故障。
/// · **文件损坏 ≠ 数据错误**：区分「解析失败」（坏文件）与「空历史」，
///   前者必须留日志，否则无从排查。
/// </summary>
public sealed partial class RateLimitStore
{
    private readonly Func<string> _path;
    private readonly ILogger _log;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
    };

    /// <summary>
    /// @param path 落盘路径 —— **惰性求值**（与 SettingsStore 同款），
    ///             因为网关启动时保险库目录可能还没定。
    /// </summary>
    public RateLimitStore(Func<string> path, ILogger? log = null)
    {
        _path = path;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>当前存储文件路径。</summary>
    public string Path => _path();

    // ⚠ CA1848 要求用 LoggerMessage 委托（源生成器），不能用 LogWarning 直接调。
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "限流历史读不了（{Message}）：{Path} —— 本次按空历史处理")]
    private partial void LogReadFailed(string message, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "限流历史文件损坏，按空历史起步：{Path} —— {Message}")]
    private partial void LogCorrupted(string path, string message);

    // ── 数据形状 ──────────────────────────────────────────────
    //
    // { "version": 1,
    //   "entries": {
    //     "<uid>": {
    //       "<model>": { "count", "lastAtMs", "lastResetAtMs", "recovered" } } } }
    //
    // 二层嵌套（uid → model），因为**查询总是按这两维**：
    //   · 账号列表按 uid 取 → 界面卡片
    //   · pool-health 按模型取 → 限流矩阵
    //   展平成一维（`uid|model` 当键）会让两边的过滤都变遍历。

    private sealed record StoredEntry
    {
        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("lastAtMs")]
        public long LastAtMs { get; init; }

        [JsonPropertyName("lastResetAtMs")]
        public long LastResetAtMs { get; init; }

        [JsonPropertyName("recovered")]
        public bool Recovered { get; init; }
    }

    private sealed record StoredDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; init; } = 1;

        [JsonPropertyName("entries")]
        public Dictionary<string, Dictionary<string, StoredEntry>> Entries { get; init; } = [];
    }

    // ── 读 ────────────────────────────────────────────────────

    /// <summary>
    /// 读全部限流历史。
    ///
    /// @returns (uid, model) → 记录。**文件不存在或损坏都返回空字典** ——
    ///          但损坏时会记 Warning（静默变空表会让用户以为从未限流过，
    ///          与 `/admin/pools` 的 accountCount 恒 0 同一条教训）。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelRateLimit>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>(
            StringComparer.Ordinal);

        var path = _path();
        if (!File.Exists(path)) return result;

        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了（被锁/权限）—— 历史读不到，但池还能跑。
            LogReadFailed(ex.Message, path);
            return result;
        }

        StoredDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<StoredDocument>(raw, Options);
        }
        catch (JsonException ex)
        {
            // ⚠ **文件损坏 ≠ 没有限流记录** —— 必须留日志，
            //   否则用户会以为"从未限流过"（与 accountCount 恒 0 同一条教训）。
            LogCorrupted(path, ex.Message);
            return result;
        }

        if (doc?.Entries is null) return result;

        foreach (var (uid, models) in doc.Entries)
        {
            if (models is null || models.Count == 0) continue;

            var inner = new Dictionary<string, ModelRateLimit>(StringComparer.Ordinal);
            foreach (var (model, e) in models)
            {
                inner[model] = new ModelRateLimit(
                    Count: e.Count,
                    LastAtMs: e.LastAtMs,
                    LastResetAtMs: e.LastResetAtMs,
                    Recovered: e.Recovered);
            }

            result[uid] = inner;
        }

        return result;
    }

    // ── 写 ────────────────────────────────────────────────────

    /// <summary>
    /// 全量落盘。**原子写**（临时文件 + 替换）——
    /// 这个文件决定"用户信不信哪些模型能用"，写一半断电不能留半截。
    /// </summary>
    public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelRateLimit>> history)
    {
        var doc = new StoredDocument();
        var entries = new Dictionary<string, Dictionary<string, StoredEntry>>(StringComparer.Ordinal);

        foreach (var (uid, models) in history)
        {
            var inner = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
            foreach (var (model, rl) in models)
            {
                inner[model] = new StoredEntry
                {
                    Count = rl.Count,
                    LastAtMs = rl.LastAtMs,
                    LastResetAtMs = rl.LastResetAtMs,
                    Recovered = rl.Recovered,
                };
            }

            entries[uid] = inner;
        }

        var path = _path();
        var json = JsonSerializer.Serialize(
            new StoredDocument { Version = 1, Entries = entries }, Options);

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));

        // File.Move(overwrite) 在 Windows 上是原子的（MoveFileEx + REPLACE_EXISTING）
        File.Move(temp, path, overwrite: true);

        HardenPermissions(path);
    }

    /// <summary>按保险库同款策略收紧权限（Unix 0600）。Windows 上跳过。</summary>
    private static void HardenPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                 or PlatformNotSupportedException)
        {
            // 某些文件系统不支持 —— 不能因为设不上权限就写不进去
        }
    }
}
