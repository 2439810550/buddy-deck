using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkBuddyPanel.Services;

/// <summary>要接入的客户端。</summary>
public enum ClientKind
{
    ClaudeCode,
    Codex,
    Pi,
    Dsh,
}

/// <summary>本机网关的接入点。</summary>
public sealed record GatewayEndpoint(string Origin, string BaseUrl, string ApiKey, string Model)
{
    public static GatewayEndpoint From(string baseUrl, string apiKey, string model)
    {
        var origin = baseUrl.TrimEnd('/');
        if (origin.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) origin = origin[..^3];
        return new GatewayEndpoint(origin.TrimEnd('/'), baseUrl.TrimEnd('/'), apiKey, model);
    }
}

/// <summary>某个客户端当前的状态。</summary>
public sealed record ClientState(
    ClientKind Kind,
    string Name,
    string ConfigPath,
    bool Detected,
    bool Configured,
    string Detail,
    string Hint);

/// <summary>pi 供应商需要的模型元数据（原始数值，不是格式化过的）。</summary>
public sealed record ModelSpec(
    string Id,
    string Name,
    long? ContextWindow,
    long? MaxTokens,
    bool SupportsImages,
    bool Reasoning,
    IReadOnlyList<string> Efforts);

/// <summary>
/// 把本机网关写进各个客户端的配置里。
///
/// 设计原则：
///   1. **改之前先备份**。每个文件只在「第一次被本程序修改」时存一份原始快照，
///      放在 %APPDATA%\WorkBuddyPanel\backups\ 下，随时能一键还原。
///   2. **只动自己那一块**。Claude Code 的 theme、Codex 的其它 provider、
///      pi 的 cc-switch-* 供应商，全都不碰。
///   3. **能识别就不再写**。Inspect() 先看清楚现状，避免重复覆盖。
/// </summary>
public static class ClientIntegrator
{
    private const string CodexProviderId = "workbuddy";
    private const string PiProviderId = "workbuddy-local";

    /// <summary>备份根目录。</summary>
    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkBuddyPanel",
        "backups");

    // ── 各客户端的位置 ────────────────────────────────────────

    public static string ClaudeSettingsPath => Path.Combine(Home, ".claude", "settings.json");

    public static string CodexConfigPath => Path.Combine(Home, ".codex", "config.toml");

    public static string CodexAuthPath => Path.Combine(Home, ".codex", "auth.json");

    public static string PiModelsPath => Path.Combine(Home, ".pi", "agent", "models.json");

    public static string DshHome => Path.Combine(Home, ".dsh");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>CC Switch 的安装位置（用来提供「打开 CC Switch」按钮）。</summary>
    public static string? FindCcSwitch()
    {
        foreach (var guess in new[]
                 {
                     @"D:\CCSwitch\cc-switch.exe",
                     @"C:\Program Files\CC Switch\cc-switch.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs", "CC Switch", "cc-switch.exe"),
                 })
        {
            if (File.Exists(guess)) return guess;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is not null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(sub);
                    if (item?.GetValue("DisplayName") as string != "CC Switch") continue;
                    var location = item.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(location)) continue;
                    var exe = Path.Combine(location.TrimEnd('\\'), "cc-switch.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch
        {
            // 注册表读不了就算了
        }

        return null;
    }

    // ── 现状检查 ──────────────────────────────────────────────

    public static IReadOnlyList<ClientState> Inspect(GatewayEndpoint endpoint)
    {
        return
        [
            InspectClaude(endpoint),
            InspectCodex(endpoint),
            InspectPi(endpoint),
            InspectDsh(),
        ];
    }

    private static ClientState InspectClaude(GatewayEndpoint endpoint)
    {
        var path = ClaudeSettingsPath;
        var detected = File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path));
        if (!detected)
        {
            return new ClientState(ClientKind.ClaudeCode, "Claude Code", path, false, false,
                "没找到 ~/.claude 目录", "装好 Claude Code 并至少跑一次后再点配置");
        }

        try
        {
            var node = ReadJson(path);
            var env = node?["env"] as JsonObject;
            var current = env?["ANTHROPIC_BASE_URL"]?.GetValue<string>();
            var configured = !string.IsNullOrEmpty(current) && current.Contains("127.0.0.1");
            var backup = HasBackup("claude", "settings.json");
            var detail = string.IsNullOrEmpty(current) ? "还没指向本网关" : $"当前指向 {current}";
            return new ClientState(ClientKind.ClaudeCode, "Claude Code", path, true, configured, detail,
                backup ? "已备份原配置" : "配置前会自动备份");
        }
        catch (Exception ex)
        {
            return new ClientState(ClientKind.ClaudeCode, "Claude Code", path, true, false,
                $"配置读不出来：{ex.Message}", "配置前会自动备份");
        }
    }

    private static ClientState InspectCodex(GatewayEndpoint endpoint)
    {
        var path = CodexConfigPath;
        if (!File.Exists(path))
        {
            return new ClientState(ClientKind.Codex, "Codex", path, false, false,
                "没找到 ~/.codex/config.toml", "装好 Codex 并至少跑一次后再点配置");
        }

        try
        {
            var text = File.ReadAllText(path);
            var hasProvider = text.Contains($"[model_providers.{CodexProviderId}]", StringComparison.Ordinal);
            var usesOurs = Regex.IsMatch(text, @"^\s*model_provider\s*=\s*""workbuddy""", RegexOptions.Multiline);
            var model = Regex.Match(text, @"^\s*model\s*=\s*""([^""]*)""", RegexOptions.Multiline);
            var detail = usesOurs
                ? $"已指向本网关（模型 {model.Groups[1].Value}）"
                : hasProvider ? "已有本网关的 provider，但当前没在用它" : "还没接入";
            return new ClientState(ClientKind.Codex, "Codex", path, true, usesOurs, detail,
                "会把 ~/.codex/auth.json 的 OPENAI_API_KEY 换成本网关密钥（原值已备份）");
        }
        catch (Exception ex)
        {
            return new ClientState(ClientKind.Codex, "Codex", path, true, false,
                $"配置读不出来：{ex.Message}", "配置前会自动备份");
        }
    }

    private static ClientState InspectPi(GatewayEndpoint endpoint)
    {
        var path = PiModelsPath;
        if (!File.Exists(path))
        {
            return new ClientState(ClientKind.Pi, "pi", path, false, false,
                "没找到 ~/.pi/agent/models.json", "装好 pi 并至少跑一次后再点配置");
        }

        try
        {
            var node = ReadJson(path);
            var provider = (node?["providers"] as JsonObject)?[PiProviderId];
            var configured = provider is not null;
            var count = (provider?["models"] as JsonArray)?.Count ?? 0;
            return new ClientState(ClientKind.Pi, "pi", path, true, configured,
                configured ? $"已接入（{count} 个模型）" : "还没接入",
                "会新增一个叫 WorkBuddy 的供应商，原有的都不动");
        }
        catch (Exception ex)
        {
            return new ClientState(ClientKind.Pi, "pi", path, true, false,
                $"配置读不出来：{ex.Message}", "配置前会自动备份");
        }
    }

    private static ClientState InspectDsh()
    {
        var detected = Directory.Exists(DshHome);
        return new ClientState(ClientKind.Dsh, "DeepSeek Harness", DshHome, detected, false,
            detected ? "检测到 DSH" : "没检测到 DSH",
            detected
                ? "会执行 dsh plugin --profile web add dsh-workbuddy-connect"
                : "DSH 不走本网关，装插件即可；先装好 DSH 再点");
    }

    // ── 配置 ──────────────────────────────────────────────────

    /// <summary>
    /// 配置一个客户端。返回给界面看的说明行。
    /// </summary>
    public static string Configure(ClientKind kind, GatewayEndpoint endpoint, IReadOnlyList<ModelSpec> models)
    {
        return kind switch
        {
            ClientKind.ClaudeCode => ConfigureClaude(endpoint),
            ClientKind.Codex => ConfigureCodex(endpoint),
            ClientKind.Pi => ConfigurePi(endpoint, models),
            ClientKind.Dsh => "DSH 不走本网关，用插件接入（见「一键接入」页的说明）",
            _ => "未知客户端",
        };
    }

    private static string ConfigureClaude(GatewayEndpoint endpoint)
    {
        var path = ClaudeSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Backup("claude", path);

        var node = ReadJson(path) ?? new JsonObject();
        var env = node["env"] as JsonObject ?? new JsonObject();

        env["ANTHROPIC_BASE_URL"] = endpoint.Origin;
        env["ANTHROPIC_AUTH_TOKEN"] = endpoint.ApiKey;
        env["ANTHROPIC_MODEL"] = endpoint.Model;
        env["ANTHROPIC_SMALL_FAST_MODEL"] = endpoint.Model;
        // Claude Code 不认识 WorkBuddy 的模型名，不给这个它会按 200k 去压缩上下文
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = "1000000";

        node["env"] = env;
        WriteJson(path, node);
        return $"Claude Code：已写入 {endpoint.Origin}（模型 {endpoint.Model}）";
    }

    private static string ConfigureCodex(GatewayEndpoint endpoint)
    {
        var configPath = CodexConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Backup("codex", configPath);

        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        WriteText(configPath, UpsertCodexToml(existing, endpoint));

        var authPath = CodexAuthPath;
        Backup("codex", authPath);
        var auth = ReadJson(authPath) ?? new JsonObject();
        auth["OPENAI_API_KEY"] = endpoint.ApiKey;
        WriteJson(authPath, auth);

        return $"Codex：已写入 provider \"{CodexProviderId}\"（模型 {endpoint.Model}），并更新了 auth.json";
    }

    private static string ConfigurePi(GatewayEndpoint endpoint, IReadOnlyList<ModelSpec> models)
    {
        var path = PiModelsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Backup("pi", path);

        var node = ReadJson(path) ?? new JsonObject();
        var providers = node["providers"] as JsonObject ?? new JsonObject();

        var list = new JsonArray();
        foreach (var model in models)
        {
            var input = new JsonArray { "text" };
            if (model.SupportsImages) input.Add("image");

            var entry = new JsonObject
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["reasoning"] = model.Reasoning,
                ["input"] = input,
            };
            if (model.ContextWindow is > 0) entry["contextWindow"] = model.ContextWindow;
            if (model.MaxTokens is > 0) entry["maxTokens"] = model.MaxTokens;
            if (model.Efforts.Count > 0)
            {
                var efforts = new JsonArray();
                foreach (var effort in model.Efforts) efforts.Add(effort);
                entry["supportedEfforts"] = efforts;
            }

            list.Add(entry);
        }

        providers[PiProviderId] = new JsonObject
        {
            ["name"] = "WorkBuddy",
            ["baseUrl"] = endpoint.BaseUrl,
            ["api"] = "openai-completions",
            ["apiKey"] = endpoint.ApiKey,
            ["models"] = list,
        };

        node["providers"] = providers;
        WriteJson(path, node);
        return $"pi：已写入供应商 {PiProviderId}（{models.Count} 个模型）";
    }

    // ── 还原 ──────────────────────────────────────────────────

    public static string Restore(ClientKind kind)
    {
        var restored = new List<string>();

        void PutBack(string group, string target, string label)
        {
            var snapshot = BackupPathFor(group, target);
            if (!File.Exists(snapshot)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(snapshot, target, overwrite: true);
            restored.Add(label);
        }

        switch (kind)
        {
            case ClientKind.ClaudeCode:
                PutBack("claude", ClaudeSettingsPath, "Claude Code settings.json");
                break;
            case ClientKind.Codex:
                PutBack("codex", CodexConfigPath, "Codex config.toml");
                PutBack("codex", CodexAuthPath, "Codex auth.json");
                break;
            case ClientKind.Pi:
                PutBack("pi", PiModelsPath, "pi models.json");
                break;
            default:
                return "DSH 不是本程序改的，无需还原";
        }

        return restored.Count == 0
            ? "没有找到备份，说明这个客户端还没被本程序改过"
            : $"已还原：{string.Join("、", restored)}";
    }

    public static bool HasAnyBackup()
        => Directory.Exists(BackupRoot) && Directory.EnumerateFiles(BackupRoot, "*", SearchOption.AllDirectories).Any();

    // ── TOML 处理 ─────────────────────────────────────────────

    /// <summary>
    /// 在 config.toml 里把 model / model_provider 设成本网关的，并补上 provider 表。
    ///
    /// 顶层键必须写在第一个 `[table]` 之前，所以不是简单追加：
    /// 先把头部区（第一个 `[` 之前）里的两行替换掉，再去掉旧的 provider 表，最后附到文件末尾。
    /// 其它 provider（比如用户原来的中转站）原样保留。
    /// </summary>
    internal static string UpsertCodexToml(string existing, GatewayEndpoint endpoint)
    {
        var normalized = existing.Replace("\r\n", "\n");
        var lines = normalized.Length == 0 ? new List<string>() : [.. normalized.Split('\n')];

        RemoveTable(lines, $"model_providers.{CodexProviderId}");

        var firstTable = lines.FindIndex(line => line.TrimStart().StartsWith('['));
        if (firstTable < 0) firstTable = lines.Count;

        var head = lines.Take(firstTable).ToList();
        var body = lines.Skip(firstTable).ToList();

        head.RemoveAll(line => Regex.IsMatch(line, @"^\s*(model|model_provider)\s*="));
        while (head.Count > 0 && head[^1].Trim().Length == 0) head.RemoveAt(head.Count - 1);

        var output = new List<string>
        {
            $"model = \"{endpoint.Model}\"",
            $"model_provider = \"{CodexProviderId}\"",
        };
        if (head.Count > 0) output.Add("");
        output.AddRange(head);

        while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
        output.Add("");

        var tail = new List<string>(body);
        while (tail.Count > 0 && tail[^1].Trim().Length == 0) tail.RemoveAt(tail.Count - 1);
        if (tail.Count > 0)
        {
            output.AddRange(tail);
            output.Add("");
        }

        output.Add($"# 由 WorkBuddy 控制台写入。官方账号或其它中转站不受影响，切回去改 model_provider 即可。");
        output.Add($"[model_providers.{CodexProviderId}]");
        output.Add("name = \"WorkBuddy\"");
        output.Add("wire_api = \"responses\"");
        // 抄用户现有 custom provider 的写法：Codex 会从 auth.json 的 OPENAI_API_KEY 取密钥
        output.Add("requires_openai_auth = true");
        output.Add($"base_url = \"{endpoint.BaseUrl}\"");
        output.Add("");

        return string.Join("\n", output);
    }

    /// <summary>整段删掉一个 TOML 表（含 `[[x]]` 数组表写法）。</summary>
    private static void RemoveTable(List<string> lines, string name)
    {
        var header = new Regex(
            $@"^\s*\[\[?\s*{Regex.Escape(name)}\s*\]?\]\s*$",
            RegexOptions.IgnoreCase);

        var start = lines.FindIndex(line => header.IsMatch(line));
        if (start < 0) return;

        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end += 1;

        // 连带把表前面多余的空行也带走，避免越改越空
        while (start > 0 && lines[start - 1].Trim().Length == 0)
        {
            start -= 1;
        }

        lines.RemoveRange(start, end - start);
    }

    // ── 备份与文件读写 ────────────────────────────────────────

    /// <summary>路径 → 备份文件名，保持稳定，还原时按同一个规则找回。</summary>
    private static string BackupPathFor(string group, string originalPath)
    {
        var name = Path.GetFileName(originalPath);
        return Path.Combine(BackupRoot, group, name);
    }

    /// <summary>第一次改某个文件时存一份原始快照；已经有了就不再覆盖（那才是「原样」）。</summary>
    private static void Backup(string group, string originalPath)
    {
        if (!File.Exists(originalPath)) return;

        var snapshot = BackupPathFor(group, originalPath);
        if (File.Exists(snapshot)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        File.Copy(originalPath, snapshot, overwrite: false);
    }

    private static bool HasBackup(string group, string fileName)
        => File.Exists(Path.Combine(BackupRoot, group, fileName));

    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Trim().Length == 0) return null;
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject;
    }

    private static void WriteJson(string path, JsonObject node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文不转义，配置文件人能看懂
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        WriteText(path, json + "\n");
    }

    private static void WriteText(string path, string text)
        => File.WriteAllText(path, text, new UTF8Encoding(false));
}
