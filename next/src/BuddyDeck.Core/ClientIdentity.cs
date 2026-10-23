using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuddyDeck.Core;

/// <summary>
/// 客户端身份探测 —— 拼出和官方客户端一模一样的 User-Agent。
///
/// ## 为什么这件事很重要
///
/// 上游（腾讯）会校验 UA 里的版本号。实测：
///
/// ```
/// 5.8.3 的 UA  → 通过
/// 5.5.6 的 UA  → 被拦（半年前的老版本，一看就是假的）
/// ```
///
/// 所以**不能写死一个版本号**，必须从本机装的 App 里读真实版本。
///
/// ## 官方 UA 的形状
///
/// ```
/// WorkBuddy/<应用版本> WorkBuddy/<应用版本> CLI/<CLI 版本>
/// ```
///
/// 例：`WorkBuddy/5.8.3 WorkBuddy/5.8.3 CLI/2.161.0`
///
/// 三段，第三段是内置 agent CLI 的版本。缺第三段目前还能通，
/// 但既然能读到就没有理由不带 —— 越像真客户端越安全。
///
/// ## ⚠ 一个从 Node 版继承来的 bug
///
/// Node 版的路径是写死的：
///
/// ```javascript
/// join(bundle, "Contents", "Resources", "app.asar.unpacked", "cli", "package.json")
/// ```
///
/// `Contents/Resources` 是 **macOS** 的 App bundle 结构。Windows 上是
/// `resources/app.asar.unpacked`（没有 `Contents`）。所以 Windows 上
/// **永远读不到 CLI 版本**，UA 一直是两段。
///
/// 这个类里两个平台的路径都处理。
///
/// @module BuddyDeck.Core/client-identity
/// </summary>
public sealed partial class ClientIdentity
{
    private readonly IProcessRunner _runner;
    private readonly IFileProbe _files;

    /// <summary>
    /// 读不到本机 App 版本时的兜底。
    ///
    /// ⚠ **这个值必须跟着上游更新。** 它是「最后一道保险」，不是常态 ——
    /// 正常路径是从注册表/App bundle 读真实版本。
    ///
    /// 如果用户反馈「网关突然不能用了」，第一个要查的就是他们机器上
    /// 是不是走到了这个兜底（说明读不到 App 版本）。
    /// </summary>
    public const string FallbackAppVersion = "5.8.3";

    /// <summary>国际版的兜底版本。开发机没装国际版，这个是估的。</summary>
    public const string FallbackInternationalAppVersion = "5.8.4";

    // 版本号必须是「1~4 段数字」，且不能有换行等能注入 header 的字符。
    [GeneratedRegex(@"^\d{1,6}(?:\.\d{1,6}){1,3}$")]
    private static partial Regex AppVersionPattern();

    /// <summary>CLI 版本允许带预发布后缀（如 2.161.0-rc.1）。</summary>
    [GeneratedRegex(@"^\d{1,6}(?:\.\d{1,6}){1,3}(?:-[0-9A-Za-z.\-]{1,32})?$")]
    private static partial Regex CliVersionPattern();

    public ClientIdentity(IProcessRunner runner, IFileProbe files)
    {
        _runner = runner;
        _files = files;
    }

    /// <summary>
    /// 解析一次身份。**调用方应该缓存结果** —— 这个操作要查注册表和读文件。
    /// </summary>
    public async Task<ChatIdentity> ResolveAsync(UpstreamVariant variant, CancellationToken ct)
    {
        var cached = _cache.GetOrAdd(variant.Id, _ => new Lazy<Task<ChatIdentity>>(
            () => ResolveCoreAsync(variant, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return await cached.Value.ConfigureAwait(false);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ChatIdentity>>> _cache = new();

    private async Task<ChatIdentity> ResolveCoreAsync(UpstreamVariant variant, CancellationToken ct)
    {
        var fallbackVersion = variant.IsInternational
            ? FallbackInternationalAppVersion
            : FallbackAppVersion;

        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = await ReadWindowsRegistryAsync(variant.AppDisplayName, ct).ConfigureAwait(false);
            if (fromRegistry is not null)
            {
                var cli = fromRegistry.InstallLocation is null
                    ? null
                    : await ReadCliVersionAsync(fromRegistry.InstallLocation, ct).ConfigureAwait(false);

                return new ChatIdentity
                {
                    ClientVersion = fromRegistry.Version,
                    CliVersion = cli,
                    Source = "registry",
                };
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var bundle = $"/Applications/{variant.AppDisplayName}.app";
            var version = await ReadMacBundleVersionAsync(bundle, ct).ConfigureAwait(false);
            if (version is not null)
            {
                var cli = await ReadCliVersionAsync(bundle, ct).ConfigureAwait(false);
                return new ChatIdentity { ClientVersion = version, CliVersion = cli, Source = "bundle" };
            }
        }

        return new ChatIdentity
        {
            ClientVersion = fallbackVersion,
            CliVersion = null,
            Source = "fallback",
        };
    }

    /// <summary>
    /// 从 Windows 注册表读 App 版本。
    ///
    /// 读的是卸载项里的 `DisplayName`（形如 `WorkBuddy 5.8.3`，取最后一段）
    /// 或 `DisplayVersion`。为什么信这个而不是安装目录：
    ///
    ///   · Electron 的安装树里没有可读的版本文件
    ///   · 打包目录名不是产品版本
    ///   · 卸载项里的 DisplayName 就是客户端自己会写进 UA 的那个值
    /// </summary>
    private async Task<RegistryApp?> ReadWindowsRegistryAsync(string appName, CancellationToken ct)
    {
        var roots = new[]
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var root in roots)
        {
            var output = await _runner.RunAsync("reg", ["query", root, "/s", "/f", appName], ct)
                .ConfigureAwait(false);
            if (output is null) continue;

            // reg query 的输出是 Key / Value 表格，用正则捞需要的字段
            var display = MatchValue(output, "DisplayName");
            var version = MatchValue(output, "DisplayVersion");
            var location = MatchValue(output, "InstallLocation");

            // DisplayName 形如 "WorkBuddy 5.8.3"，最后一段是版本
            var fromName = display is null ? null : display.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

            var candidate = IsValidAppVersion(version) ? version
                : IsValidAppVersion(fromName) ? fromName
                : null;

            if (candidate is null) continue;

            return new RegistryApp
            {
                Version = candidate,
                InstallLocation = string.IsNullOrWhiteSpace(location) ? null : location.Trim('"'),
            };
        }

        return null;
    }

    /// <summary>从 `reg query` 的输出里取一个 REG_SZ 值。</summary>
    private static string? MatchValue(string output, string name)
    {
        var m = Regex.Match(output, name + @"\s+REG_SZ\s+(.+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>从 macOS 的 Info.plist 读版本。开发机是 Windows，这段没实测过。</summary>
    private async Task<string?> ReadMacBundleVersionAsync(string bundle, CancellationToken ct)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (!_files.Exists(plist)) return null;

        var output = await _runner.RunAsync("defaults", ["read", plist.Replace(".plist", ""), "CFBundleShortVersionString"], ct)
            .ConfigureAwait(false);

        var version = output?.Trim();
        return IsValidAppVersion(version) ? version : null;
    }

    /// <summary>
    /// 读内置 agent CLI 的版本。
    ///
    /// ## 两个平台路径不同（这是 Node 版的 bug）
    ///
    /// ```
    /// macOS    <bundle>/Contents/Resources/app.asar.unpacked/cli/package.json
    /// Windows  <install>/resources/app.asar.unpacked/cli/package.json
    /// ```
    ///
    /// Node 版只写了 macOS 那条，所以 Windows 上永远读不到。
    ///
    /// ## 版本藏在哪
    ///
    /// `cli/package.json` 的 `version` 是占位的 `0.0.0`，真版本在
    /// `publishConfig.customPackage.version`（实测 2.161.0）。
    /// </summary>
    private Task<string?> ReadCliVersionAsync(string bundleRoot, CancellationToken ct)
    {
        // 两个候选路径都试 —— 不靠平台判断，谁存在用谁。
        // 这样万一目录结构变了（或者跑在 CrossOver 之类环境里）也能读到。
        var candidates = new[]
        {
            Path.Combine(bundleRoot, "resources", "app.asar.unpacked", "cli", "package.json"),
            Path.Combine(bundleRoot, "Contents", "Resources", "app.asar.unpacked", "cli", "package.json"),
        };

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!_files.Exists(path)) continue;

            try
            {
                var text = _files.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                // 优先 version（只要不是占位的 0.0.0）
                if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (IsValidCliVersion(s) && s != "0.0.0") return Task.FromResult<string?>(s);
                }

                // 其次 publishConfig.customPackage.version
                if (root.TryGetProperty("publishConfig", out var pc) && pc.ValueKind == JsonValueKind.Object
                    && pc.TryGetProperty("customPackage", out var cp) && cp.ValueKind == JsonValueKind.Object
                    && cp.TryGetProperty("version", out var cv) && cv.ValueKind == JsonValueKind.String)
                {
                    var s = cv.GetString();
                    if (IsValidCliVersion(s)) return Task.FromResult<string?>(s);
                }
            }
            catch (JsonException)
            {
                // 文件坏了 —— 试下一个候选
            }
            catch (IOException)
            {
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>版本号是否安全（能塞进 HTTP header，且形状合理）。</summary>
    public static bool IsValidAppVersion(string? value)
        => value is not null && AppVersionPattern().IsMatch(value);

    public static bool IsValidCliVersion(string? value)
        => value is not null && CliVersionPattern().IsMatch(value);

    private sealed record RegistryApp
    {
        public required string Version { get; init; }
        public string? InstallLocation { get; init; }
    }
}

/// <summary>一次解析出来的客户端身份。</summary>
public sealed record ChatIdentity
{
    /// <summary>App 版本。UA 里出现两次的那个。</summary>
    public required string ClientVersion { get; init; }

    /// <summary>内置 agent CLI 版本。读不到就是 null（UA 只有两段）。</summary>
    public string? CliVersion { get; init; }

    /// <summary>这个身份从哪来的：registry / bundle / fallback。排查时看这个。</summary>
    public required string Source { get; init; }

    /// <summary>
    /// 拼成官方形状的 UA。
    ///
    /// ```
    /// WorkBuddy/5.8.3 WorkBuddy/5.8.3 CLI/2.161.0
    /// ```
    /// </summary>
    public string UserAgent(UpstreamVariant variant)
    {
        var product = variant.IsInternational ? "WorkBuddy AI" : "WorkBuddy";
        var parts = new List<string>
        {
            $"WorkBuddy/{ClientVersion}",
            $"{product}/{ClientVersion}",
        };
        if (!string.IsNullOrEmpty(CliVersion))
        {
            parts.Add($"CLI/{CliVersion}");
        }
        return string.Join(' ', parts);
    }

    /// <summary>走的是兜底吗（说明读不到本机 App —— 该提示用户了）。</summary>
    public bool IsFallback => Source == "fallback";
}

/// <summary>跑外部命令。抽象出来是为了测试能塞假实现。</summary>
public interface IProcessRunner
{
    /// <summary>跑一个命令，返回 stdout；失败返回 null（不要抛）。</summary>
    Task<string?> RunAsync(string command, string[] args, CancellationToken ct);
}

/// <summary>文件探测。抽象出来是为了测试能塞假文件系统。</summary>
public interface IFileProbe
{
    bool Exists(string path);
    string ReadAllText(string path);
}
