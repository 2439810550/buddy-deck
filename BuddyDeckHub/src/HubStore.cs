using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuddyDeckHub;

/// <summary>
/// 配置与账本的落盘。
///
/// 分两份存：
///   config.json  —— 渠道 / 成员 / 分组。人手改得动，出问题好排查
///   usage.db     —— 逐条调用日志 + 每日用量汇总。走 SQLite，量大也不怕
///
/// 为什么不用一个数据库全装：配置要能直接看、直接改、直接备份，
/// JSON 比 SQLite 舒服太多。日志反过来，量大且要聚合查询，必须 SQLite。
/// </summary>
public sealed class HubStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly SemaphoreSlim _configLock = new(1, 1);

    public string DatabasePath { get; }

    public HubConfig Config { get; private set; } = new();

    public HubStore(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _configPath = Path.Combine(dataDir, "config.json");
        DatabasePath = Path.Combine(dataDir, "usage.db");
    }

    // ──────────────────────────── 配置 ────────────────────────────

    public async Task LoadAsync()
    {
        if (!File.Exists(_configPath))
        {
            Config = new HubConfig();
            SeedDefaults();
            await SaveAsync();
            return;
        }

        var json = await File.ReadAllTextAsync(_configPath);
        Config = JsonSerializer.Deserialize<HubConfig>(json, JsonOpts) ?? new HubConfig();
        SeedDefaults();
    }

    /// <summary>补上还没配的分组，让配置文件首次生成时就是完整的。</summary>
    private void SeedDefaults()
    {
        void Ensure(string id, string name, double rate, string desc, List<string> models)
        {
            if (Config.Groups.All(g => g.Id != id))
                Config.Groups.Add(new Group
                {
                    Id = id, Name = name, Rate = rate,
                    Description = desc, Models = models,
                });
        }

        // free 组：只给免费模型，不扣配额。给「随便用用」的人。
        // 倍率 0 是关键 —— 免费模型本来就不烧积分，再扣配额没道理。
        Ensure("free", "免费组", 0.0, "只能用免费模型，不消耗配额",
            ["deepseek-v4.1-flash", "glm-5.3-flash"]);

        Ensure("default", "默认组", 1.0, "全部模型，按用量记账", ["*"]);
    }

    public async Task SaveAsync()
    {
        await _configLock.WaitAsync();
        try
        {
            // 先写临时文件再原子替换，避免写到一半断电留下半个 JSON
            var tmp = _configPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(Config, JsonOpts));
            File.Move(tmp, _configPath, overwrite: true);
        }
        finally { _configLock.Release(); }
    }

    // ──────────────────────────── 账本 ────────────────────────────

    public async Task InitDatabaseAsync()
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS calls (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ts            INTEGER NOT NULL,          -- Unix ms
                day           TEXT    NOT NULL,          -- 本地日期 YYYY-MM-DD
                member_id     TEXT    NOT NULL,
                member_name   TEXT    NOT NULL,
                token_prefix  TEXT    NOT NULL,
                channel_id    TEXT    NOT NULL,
                model         TEXT    NOT NULL,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                quota         INTEGER NOT NULL DEFAULT 0,
                latency_ms    INTEGER NOT NULL DEFAULT 0,
                status        INTEGER NOT NULL,
                error_kind    TEXT,
                streamed      INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_calls_day     ON calls(day);
            CREATE INDEX IF NOT EXISTS idx_calls_member  ON calls(member_id, day);
            CREATE INDEX IF NOT EXISTS idx_calls_ts      ON calls(ts DESC);

            CREATE TABLE IF NOT EXISTS daily_usage (
                day         TEXT    NOT NULL,
                member_id   TEXT    NOT NULL,
                quota       INTEGER NOT NULL DEFAULT 0,
                calls       INTEGER NOT NULL DEFAULT 0,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (day, member_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Microsoft.Data.Sqlite.SqliteConnection OpenDb()
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>某成员今天的已用配额。</summary>
    public async Task<long> TodayQuotaAsync(string memberId, string day)
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(quota,0) FROM daily_usage WHERE day=$d AND member_id=$m";
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$m", memberId);
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? l : 0;
    }

    /// <summary>某成员今天的调用次数（限速用）。</summary>
    public async Task<int> TodayCallsAsync(string memberId, string day)
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(calls,0) FROM daily_usage WHERE day=$d AND member_id=$m";
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$m", memberId);
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : 0;
    }

    /// <summary>最近 N 秒内的请求数，用于每分钟限速。</summary>
    public async Task<int> RecentCallsAsync(string memberId, int windowSeconds)
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM calls WHERE member_id=$m AND ts > $since";
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - windowSeconds * 1000L);
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : 0;
    }

    /// <summary>记一条调用，并累加当日汇总。一起提交，保证不会只记一半。</summary>
    public async Task RecordAsync(CallRecord r)
    {
        await using var conn = OpenDb();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO calls
                  (ts, day, member_id, member_name, token_prefix, channel_id, model,
                   prompt_tokens, completion_tokens, quota, latency_ms, status, error_kind, streamed)
                VALUES ($ts,$day,$mid,$mname,$prefix,$ch,$model,$pt,$ct,$q,$lat,$st,$ek,$stream)
                """;
            cmd.Parameters.AddWithValue("$ts", r.Timestamp);
            cmd.Parameters.AddWithValue("$day", r.Day);
            cmd.Parameters.AddWithValue("$mid", r.MemberId);
            cmd.Parameters.AddWithValue("$mname", r.MemberName);
            cmd.Parameters.AddWithValue("$prefix", r.TokenPrefix);
            cmd.Parameters.AddWithValue("$ch", r.ChannelId);
            cmd.Parameters.AddWithValue("$model", r.Model);
            cmd.Parameters.AddWithValue("$pt", r.PromptTokens);
            cmd.Parameters.AddWithValue("$ct", r.CompletionTokens);
            cmd.Parameters.AddWithValue("$q", r.Quota);
            cmd.Parameters.AddWithValue("$lat", r.LatencyMs);
            cmd.Parameters.AddWithValue("$st", r.Status);
            cmd.Parameters.AddWithValue("$ek", (object?)r.ErrorKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stream", r.Streamed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var up = conn.CreateCommand())
        {
            up.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            up.CommandText = """
                INSERT INTO daily_usage (day, member_id, quota, calls, prompt_tokens, completion_tokens)
                VALUES ($day, $mid, $q, 1, $pt, $ct)
                ON CONFLICT(day, member_id) DO UPDATE SET
                  quota = quota + $q,
                  calls = calls + 1,
                  prompt_tokens = prompt_tokens + $pt,
                  completion_tokens = completion_tokens + $ct
                """;
            up.Parameters.AddWithValue("$day", r.Day);
            up.Parameters.AddWithValue("$mid", r.MemberId);
            up.Parameters.AddWithValue("$q", r.Quota);
            up.Parameters.AddWithValue("$pt", r.PromptTokens);
            up.Parameters.AddWithValue("$ct", r.CompletionTokens);
            await up.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>概览：今天每个人用了多少。</summary>
    public async Task<List<Dictionary<string, object?>>> DashboardAsync(string day)
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.member_id, d.quota, d.calls, d.prompt_tokens, d.completion_tokens,
                   (SELECT member_name FROM calls c WHERE c.member_id = d.member_id
                     ORDER BY c.ts DESC LIMIT 1) AS member_name
            FROM daily_usage d WHERE d.day = $day ORDER BY d.quota DESC
            """;
        cmd.Parameters.AddWithValue("$day", day);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["memberId"] = reader.GetString(0),
                ["memberName"] = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ["quota"] = reader.GetInt64(1),
                ["calls"] = reader.GetInt64(2),
                ["promptTokens"] = reader.GetInt64(3),
                ["completionTokens"] = reader.GetInt64(4),
            });
        }
        return rows;
    }

    /// <summary>最近调用流水。</summary>
    public async Task<List<Dictionary<string, object?>>> RecentCallsAsync(int limit = 100)
    {
        await using var conn = OpenDb();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts, member_name, model, channel_id, prompt_tokens, completion_tokens,
                   quota, latency_ms, status, error_kind
            FROM calls ORDER BY ts DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", limit);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["time"] = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime().ToString("MM-dd HH:mm:ss"),
                ["member"] = reader.GetString(1),
                ["model"] = reader.GetString(2),
                ["channel"] = reader.GetString(3),
                ["promptTokens"] = reader.GetInt64(4),
                ["completionTokens"] = reader.GetInt64(5),
                ["quota"] = reader.GetInt64(6),
                ["latencyMs"] = reader.GetInt64(7),
                ["status"] = reader.GetInt64(8),
                ["errorKind"] = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return rows;
    }
}

public sealed class HubConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Hub 自己监听的地址。默认只绑回环 —— 对外由反代负责。</summary>
    public string Listen { get; set; } = "http://127.0.0.1:8890";

    /// <summary>管理接口的令牌。首次启动自动生成。</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>调用日志保留天数。</summary>
    public int LogRetentionDays { get; set; } = 90;

    public List<Channel> Channels { get; set; } = [];
    public List<Member> Members { get; set; } = [];
    public List<Group> Groups { get; set; } = [];
}

public sealed class CallRecord
{
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public required string Day { get; init; }
    public required string MemberId { get; init; }
    public required string MemberName { get; init; }
    public required string TokenPrefix { get; init; }
    public required string ChannelId { get; init; }
    public required string Model { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long Quota { get; init; }
    public long LatencyMs { get; init; }
    public int Status { get; init; }
    public string? ErrorKind { get; init; }
    public bool Streamed { get; init; }
}
