using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 保险库路径解析的测试（t47）。
///
/// ## 这一批测的是什么
///
/// 一个**真实发生过的静默降级**：用户给相对路径 `--vault .local\vault.json` 启动，
/// 启动日志看着正常、`/healthz` 与 `/v1/models` 都 200，
/// 但**所有聊天请求回 401 not_signed_in「保险库里没有账号」**。
///
/// 根因：旧代码把相对路径**原样返回**，而进程 cwd（`dotnet run --project` 会切到
/// 项目目录）不是用户的 shell cwd → 读了一个不存在的文件。
///
/// **它为什么最阴**：配置看起来被接受了、启动看起来正常，
/// 只有真正用到时才降级成「没账号」—— 用户会以为是自己账号坏了。
/// </summary>
public class VaultPathTests
{
    /// <summary>造一个 options（带显式 VaultPath）。</summary>
    private static GatewayOptions WithVault(string vaultPath) => new()
    {
        Port = 0,
        Variant = UpstreamVariant.China.Id,
        VaultPath = vaultPath,
    };

    // ══ ① 相对路径 → 按「启动时所在目录」解析 ═════════════════

    [Fact]
    public void 相对路径按启动目录解析成绝对路径()
    {
        // ★ 这条是 t47 的核心。
        var resolved = WithVault(Path.Combine(".local", "vault.json")).ResolveVaultPath();

        Assert.True(Path.IsPathRooted(resolved),
            $"解析结果必须是绝对路径（否则读的是进程 cwd，不是用户所在目录）：{resolved}");

        // 必须落在**启动目录**下，而不是当前进程 cwd 下
        var expected = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local", "vault.json"));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void 相对路径解析_不受后续改_currentDirectory_影响()
    {
        // ★ 这条验证的是**实现方式**：cwd 必须在进程启动那一刻取。
        //
        //   如果实现写成「每次调用 Directory.GetCurrentDirectory()」，
        //   那么 `dotnet run` / 别处改了目录之后就会解析错 ——
        //   而 `dotnet run --project` **确实会改**（切到项目目录），
        //   那正是这个 bug 的成因。
        var options = WithVault(Path.Combine("some", "vault.json"));

        var before = options.ResolveVaultPath();

        var original = Directory.GetCurrentDirectory();
        try
        {
            // 模拟「进程运行中被改了目录」
            Directory.SetCurrentDirectory(Path.GetTempPath());
            var after = options.ResolveVaultPath();

            Assert.Equal(before, after);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    // ══ ② 绝对路径 → 原样（不改既有语义）═══════════════════════

    [Fact]
    public void 绝对路径原样返回()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vault.json"));

        Assert.Equal(absolute, WithVault(absolute).ResolveVaultPath());
    }

    // ══ ③ 没给路径 → 程序运行目录（t47 要求「不改默认语义」）═══

    [Fact]
    public void 没给路径时_仍用程序运行目录_默认语义未变()
    {
        // ★ 契约明确要求：不修改 ResolveVaultPath 的「程序运行目录」默认语义。
        var options = new GatewayOptions { Port = 0, Variant = UpstreamVariant.China.Id };

        var resolved = options.ResolveVaultPath();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, UpstreamVariant.China.VaultFilename),
            resolved);
    }

    [Fact]
    public void 没给路径时_用变体自带的文件名()
    {
        var china = new GatewayOptions { Port = 0, Variant = UpstreamVariant.China.Id };
        var global = new GatewayOptions { Port = 0, Variant = UpstreamVariant.Global.Id };

        Assert.EndsWith("auth.cn.json", china.ResolveVaultPath(), StringComparison.Ordinal);
        Assert.EndsWith("auth.ai.json", global.ResolveVaultPath(), StringComparison.Ordinal);
    }

    // ══ ④ 日志目录同款处理 ════════════════════════════════════

    [Fact]
    public void 日志目录的相对路径也按启动目录解析()
    {
        // 与保险库同款的问题：相对路径不能落到「进程 cwd」上。
        var options = new GatewayOptions { Port = 0, Variant = UpstreamVariant.China.Id, LogDirectory = "mylogs" };

        var resolved = options.ResolveLogDirectory();

        Assert.True(Path.IsPathRooted(resolved), $"日志目录也必须是绝对路径：{resolved}");
        Assert.EndsWith("mylogs", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void 日志目录没给时_仍是程序运行目录下的_logs()
    {
        var options = new GatewayOptions { Port = 0, Variant = UpstreamVariant.China.Id };

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), options.ResolveLogDirectory());
    }

    // ══ ⑤ 命令行解析：--vault 的值要能被取到 ═══════════════════

    [Fact]
    public void 命令行_vault_参数被解析()
    {
        var options = GatewayOptions.Parse(["--vault", @".local\vault.json"]);

        Assert.Equal(@".local\vault.json", options.VaultPath);
    }

    [Fact]
    public void 命令行_vault_等号写法也被解析()
    {
        var options = GatewayOptions.Parse(["--vault=.local/vault.json"]);

        Assert.Equal(".local/vault.json", options.VaultPath);
    }
}
