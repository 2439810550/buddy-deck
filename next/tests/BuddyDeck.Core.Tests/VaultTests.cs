using System.Text.Json;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 保险库读写的测试。
///
/// 这里有一组特别的测试：**读真实存在的保险库文件**（开发机上的
/// `~/.workbuddy-api/accounts.cn.json`）。这比造数据强得多 ——
/// 那个文件是旧版网关写的，能读它就证明格式兼容。
/// </summary>
public class VaultTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid():N}.json");

    // ── 基本读写 ──────────────────────────────────────────────

    [Fact]
    public void 写进去再读出来内容一致()
    {
        var path = TempPath();
        try
        {
            var doc = new VaultDocument
            {
                ActiveId = "abc-123:ent-9",
                Accounts =
                [
                    new VaultEntry
                    {
                        Id = "abc-123:ent-9",
                        AccessToken = "tok-1",
                        RefreshToken = "ref-1",
                        Uid = "u1",
                        EnterpriseId = "ent-9",
                        Domain = "workbuddy.cn",
                        Label = "主号",
                        ExpiresAtMs = 1790000000000,
                    },
                    new VaultEntry
                    {
                        Id = "def-456:",
                        AccessToken = "tok-2",
                        Uid = "u2",
                        Label = "朋友的号",
                    },
                ],
            };

            Vault.Write(path, doc);
            var back = Vault.Read(path);

            Assert.NotNull(back);
            Assert.Equal("abc-123:ent-9", back.ActiveId);
            Assert.Equal(2, back.Accounts.Count);
            Assert.Equal("主号", back.Accounts[0].Label);
            Assert.Equal("ent-9", back.Accounts[0].EnterpriseId);
            Assert.Equal("朋友的号", back.Accounts[1].Label);
            Assert.Null(back.Accounts[1].EnterpriseId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 文件不存在返回_null_而不是空文档()
    {
        // 这个区分很重要：调用方要能分辨「还没建库」和「库是空的」——
        // 前者该提示「去导入账号」，后者该显示「列表为空」。
        Assert.Null(Vault.Read(TempPath()));
    }

    [Fact]
    public void 空文件返回_null()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "");
            Assert.Null(Vault.Read(path));
            File.WriteAllText(path, "   \n  ");
            Assert.Null(Vault.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 原子写 ────────────────────────────────────────────────

    [Fact]
    public void 写完后不留临时文件()
    {
        var path = TempPath();
        try
        {
            Vault.Write(path, new VaultDocument());
            Assert.False(File.Exists(path + ".tmp"), "临时文件应该已经被 Move 掉了");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 覆盖写不会损坏原文件()
    {
        var path = TempPath();
        try
        {
            Vault.Write(path, new VaultDocument { ActiveId = "first" });
            Vault.Write(path, new VaultDocument { ActiveId = "second" });

            var back = Vault.Read(path);
            Assert.NotNull(back);
            Assert.Equal("second", back.ActiveId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 不存在的目录会自动建()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vault-dir-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(dir, "vault.json");
        try
        {
            Vault.Write(path, new VaultDocument { ActiveId = "x" });
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(dir)!))
            {
                Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
            }
        }
    }

    // ── Unix 权限 ─────────────────────────────────────────────

    [Fact]
    public void Unix_下权限是_0600()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows 上跳过

        var path = TempPath();
        try
        {
            Vault.Write(path, new VaultDocument());
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 中文不转义 ────────────────────────────────────────────

    [Fact]
    public void 中文标签按原样写不转义()
    {
        // 转义成 \u4e3b\u53f7 也能读，但用 cat 看的时候没法读 ——
        // 用户排查问题时经常要直接看这个文件。
        var path = TempPath();
        try
        {
            Vault.Write(path, new VaultDocument
            {
                Accounts = [new VaultEntry { Id = "a", AccessToken = "t", Label = "张三的号" }],
            });

            var text = File.ReadAllText(path);
            Assert.Contains("张三的号", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 兼容旧版写的文件 ──────────────────────────────────────

    [Fact]
    public void 能读旧版写的文件_含未知字段()
    {
        // 旧版可能有我们还不认识的字段。忽略它们，不能报错 ——
        // 否则新版本第一次运行就起不来。
        const string oldFormat = """
        {
          "version": 1,
          "activeId": "old-id:",
          "accounts": [
            {
              "id": "old-id:",
              "uid": "u-old",
              "accessToken": "tok-old",
              "label": "旧账号",
              "addedAt": "2026-09-24T09:35:12.642Z",
              "lastSeenAt": "2026-10-06T18:39:24.680Z",
              "lastCheckinAt": "2026-10-06T18:39:26.706Z",
              "未来才有的字段": { "随便": "什么" }
            }
          ],
          "也是新字段": 123
        }
        """;

        var path = TempPath();
        try
        {
            File.WriteAllText(path, oldFormat);
            var doc = Vault.Read(path);

            Assert.NotNull(doc);
            Assert.Equal("old-id:", doc.ActiveId);
            Assert.Single(doc.Accounts);
            Assert.Equal("旧账号", doc.Accounts[0].Label);
            Assert.Equal("2026-09-24T09:35:12.642Z", doc.Accounts[0].AddedAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_允许注释和尾逗号()
    {
        // 手改过的文件经常有这些。JSON 标准不允许，但配置文件放过它们
        // 能省掉用户一次「文件坏了」的困惑。
        const string json = """
        {
          // 这是个注释
          "version": 1,
          "accounts": [],
        }
        """;

        var path = TempPath();
        try
        {
            File.WriteAllText(path, json);
            Assert.NotNull(Vault.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── VaultEntry → Credential 的转换 ────────────────────────

    [Fact]
    public void 转成凭证时字段都对得上()
    {
        var entry = new VaultEntry
        {
            Id = "x:",
            AccessToken = "tok",
            RefreshToken = "ref",
            Uid = "u1",
            EnterpriseId = "e1",
            Domain = "d.cn",
            Label = "标签",
            ExpiresAtMs = 123,
        };

        var c = entry.ToCredential();

        Assert.Equal("tok", c.AccessToken);
        Assert.Equal("ref", c.RefreshToken);
        Assert.Equal("u1", c.Uid);
        Assert.Equal("e1", c.EnterpriseId);
        Assert.Equal("d.cn", c.Domain);
        Assert.Equal("标签", c.Label);
        Assert.Equal(123, c.ExpiresAtMs);
        Assert.Equal("u1:e1", c.AccountTag);
    }

    // ══════════════════════════════════════════════════════════
    //  读真实的保险库文件 —— 开发机上那份（如果存在）
    // ══════════════════════════════════════════════════════════
    //
    // 这个测试比造数据强得多：那个文件是旧版 Node 网关写的，
    // 能读它就证明格式真的兼容，而不是「我以为兼容」。

    [Fact]
    public void 能读真实的旧版保险库()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return;

        var realPath = Path.Combine(home, ".workbuddy-api", "accounts.cn.json");
        if (!File.Exists(realPath)) return;   // 别的机器上没有，跳过

        var doc = Vault.Read(realPath);

        Assert.NotNull(doc);
        Assert.NotEmpty(doc.Accounts);

        // 每个账号都该有可用的 token
        foreach (var a in doc.Accounts)
        {
            Assert.False(string.IsNullOrEmpty(a.Id), "id 不能为空");
            Assert.False(string.IsNullOrEmpty(a.AccessToken), $"账号 {a.Label} 没有 accessToken");
        }

        // activeId 要在账号列表里找得到（或者为空 —— 用户没选定过）
        if (!string.IsNullOrEmpty(doc.ActiveId))
        {
            Assert.Contains(doc.Accounts, a => a.Id == doc.ActiveId);
        }
    }
}
