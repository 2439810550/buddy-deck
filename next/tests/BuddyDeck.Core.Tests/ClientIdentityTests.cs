using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 客户端身份探测的测试。
///
/// ## 为什么这块必须测
///
/// UA 里的版本号是**能不能用**的关键：
///
/// ```
/// 5.8.3 的真实版本  → 上游通过
/// 5.5.6 的旧兜底    → 上游拦掉
/// ```
///
/// 而探测逻辑依赖「读注册表」「读 App 目录里的 package.json」——
/// 这两件事在别人机器上很容易失败（没装 App、装了但注册表结构不同）。
/// 一旦静默掉到兜底版本，用户看到的就是「网关突然不能用了」。
///
/// 所以测试要覆盖：注册表解析、路径变体、兜底、以及**兜底必须能被识别**。
/// </summary>
public class ClientIdentityTests
{
    // ── 假的 runner / probe ───────────────────────────────────

    private sealed class FakeRunner : IProcessRunner
    {
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

        public Task<string?> RunAsync(string command, string[] args, CancellationToken ct)
        {
            // 用「命令 + 关键参数」拼一个键
            var key = command + " " + string.Join(' ', args);
            foreach (var (k, v) in Outputs)
            {
                if (key.Contains(k, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<string?>(v);
            }
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeProbe : IFileProbe
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string path) => Files.ContainsKey(path.Replace('/', '\\'));
        public string ReadAllText(string path) => Files[path.Replace('/', '\\')];
    }

    /// <summary>
    /// 造一段真实的 `reg query` 输出（格式照抄 Windows 实际输出）。
    /// </summary>
    private static string RegistryOutput(string displayName, string version, string? location = null)
    {
        var lines = new List<string>
        {
            "",
            "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{GUID}",
            "    DisplayName    REG_SZ    " + displayName,
            "    DisplayVersion    REG_SZ    " + version,
        };
        if (location is not null)
        {
            lines.Add("    InstallLocation    REG_SZ    " + location);
        }
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    // ── 注册表解析 ────────────────────────────────────────────

    [Fact]
    public async Task 能读到注册表里的真实版本()
    {
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3");

        var identity = new ClientIdentity(runner, new FakeProbe());
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("5.8.3", id.ClientVersion);
        Assert.Equal("registry", id.Source);
        Assert.False(id.IsFallback);
    }

    [Fact]
    public async Task 只看_DisplayVersion_不看别的()
    {
        // DisplayVersion 是权威。DisplayName 里的版本只在 DisplayVersion 无效时用。
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 9.9.9", "5.8.3");

        var identity = new ClientIdentity(runner, new FakeProbe());
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("5.8.3", id.ClientVersion);
    }

    [Fact]
    public async Task DisplayVersion_无效时从_DisplayName_里取()
    {
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "不是版本号");

        var identity = new ClientIdentity(runner, new FakeProbe());
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("5.8.3", id.ClientVersion);
    }

    [Fact]
    public async Task 找不到注册表时掉到兜底()
    {
        var identity = new ClientIdentity(new FakeRunner(), new FakeProbe());
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal(ClientIdentity.FallbackAppVersion, id.ClientVersion);
        Assert.Equal("fallback", id.Source);
        Assert.True(id.IsFallback, "必须能识别出「这是兜底」—— 否则没法提示用户");
    }

    // ── CLI 版本（Node 版在这里有 bug）────────────────────────

    [Fact]
    public async Task 能读到_CLI_版本_用_Windows_路径()
    {
        // ⚠ Node 版把路径写成了 macOS 的 Contents/Resources，
        // 所以 Windows 上永远读不到。这里两个路径都试。
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3", @"D:\WorkBuddy");

        var probe = new FakeProbe();
        probe.Files[@"D:\WorkBuddy\resources\app.asar.unpacked\cli\package.json"] =
            """{"name":"@genie/agent-cli","version":"0.0.0","publishConfig":{"customPackage":{"version":"2.161.0"}}}""";

        var identity = new ClientIdentity(runner, probe);
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("5.8.3", id.ClientVersion);
        Assert.Equal("2.161.0", id.CliVersion);
    }

    [Fact]
    public async Task 也能读_macOS_的_Contents_路径()
    {
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3", @"/Applications/WorkBuddy.app");

        var probe = new FakeProbe();
        probe.Files[@"\Applications\WorkBuddy.app\Contents\Resources\app.asar.unpacked\cli\package.json"] =
            """{"version":"2.161.0"}""";

        var identity = new ClientIdentity(runner, probe);
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("2.161.0", id.CliVersion);
    }

    [Fact]
    public async Task version_是占位_0_0_0_时用_publishConfig()
    {
        // 实测 cli/package.json 里 version 就是 "0.0.0"，真版本在 publishConfig
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3", @"D:\WorkBuddy");

        var probe = new FakeProbe();
        probe.Files[@"D:\WorkBuddy\resources\app.asar.unpacked\cli\package.json"] =
            """{"version":"0.0.0","publishConfig":{"customPackage":{"version":"2.161.0"}}}""";

        var identity = new ClientIdentity(runner, probe);
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("2.161.0", id.CliVersion);
    }

    [Fact]
    public async Task 没有_CLI_文件时只有两段_UA()
    {
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3", @"D:\WorkBuddy");

        var identity = new ClientIdentity(runner, new FakeProbe());
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Null(id.CliVersion);
        Assert.Equal("WorkBuddy/5.8.3 WorkBuddy/5.8.3", id.UserAgent(UpstreamVariant.China));
    }

    [Fact]
    public async Task CLI_文件是坏_JSON_时不崩()
    {
        var runner = new FakeRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3", @"D:\WorkBuddy");

        var probe = new FakeProbe();
        probe.Files[@"D:\WorkBuddy\resources\app.asar.unpacked\cli\package.json"] = "{ 坏掉的";

        var identity = new ClientIdentity(runner, probe);
        var id = await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        Assert.Equal("5.8.3", id.ClientVersion);
        Assert.Null(id.CliVersion);
    }

    // ── UA 拼装 ───────────────────────────────────────────────

    [Fact]
    public void UA_是官方三段式()
    {
        var id = new ChatIdentity { ClientVersion = "5.8.3", CliVersion = "2.161.0", Source = "registry" };

        Assert.Equal("WorkBuddy/5.8.3 WorkBuddy/5.8.3 CLI/2.161.0", id.UserAgent(UpstreamVariant.China));
    }

    [Fact]
    public void 国际版用_WorkBuddy_AI_作为产品名()
    {
        var id = new ChatIdentity { ClientVersion = "5.4.2", Source = "registry" };

        Assert.Equal("WorkBuddy/5.4.2 WorkBuddy AI/5.4.2", id.UserAgent(UpstreamVariant.Global));
    }

    // ── 版本号校验 ────────────────────────────────────────────

    [Theory]
    [InlineData("5.8.3", true)]
    [InlineData("5.8", true)]
    [InlineData("5", false)]              // 至少两段
    [InlineData("5.8.3.1", true)]
    [InlineData("5.8.3\r\nX-Bad: 1", false)]   // header 注入
    [InlineData("v5.8.3", false)]         // 不能带前缀
    [InlineData("", false)]
    [InlineData(null, false)]
    public void App_版本校验(string? value, bool expected)
    {
        Assert.Equal(expected, ClientIdentity.IsValidAppVersion(value));
    }

    [Theory]
    [InlineData("2.161.0", true)]
    [InlineData("2.161.0-rc.1", true)]    // CLI 允许预发布后缀
    [InlineData("2.161", true)]
    [InlineData("2", false)]
    [InlineData("2.161.0\r\n", false)]
    public void CLI_版本校验(string value, bool expected)
    {
        Assert.Equal(expected, ClientIdentity.IsValidCliVersion(value));
    }

    // ── 缓存 ──────────────────────────────────────────────────

    [Fact]
    public async Task 解析结果会缓存()
    {
        var runner = new CountingRunner();
        runner.Outputs["HKCU"] = RegistryOutput("WorkBuddy 5.8.3", "5.8.3");

        var identity = new ClientIdentity(runner, new FakeProbe());
        await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);
        await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);
        await identity.ResolveAsync(UpstreamVariant.China, CancellationToken.None);

        // 查三次只该跑一次 reg（缓存生效）—— 这个操作要起子进程，不能每条消息都跑
        Assert.Equal(1, runner.Calls);
    }

    private sealed class CountingRunner : IProcessRunner
    {
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }

        public Task<string?> RunAsync(string command, string[] args, CancellationToken ct)
        {
            Calls++;
            var key = command + " " + string.Join(' ', args);
            foreach (var (k, v) in Outputs)
            {
                if (key.Contains(k, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<string?>(v);
            }
            return Task.FromResult<string?>(null);
        }
    }
}
