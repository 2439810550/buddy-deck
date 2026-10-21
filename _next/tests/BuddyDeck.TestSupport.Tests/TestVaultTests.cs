using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// 凭证工厂的测试。
///
/// 重点是**空身份账号** —— 它是 `X-No-User-Id` / `X-No-Enterprise-Id` /
/// `X-No-Department-Info` 三个头的唯一测试对象。造不出来就没法验证
/// 「缺头」和「显式声明没有」的区别（HANDOVER 第三节第 2 条）。
/// </summary>
public class TestVaultTests
{
    [Fact]
    public void 默认账号有身份有域且没过期()
    {
        var credential = new TestVault().Add().FirstCredential;

        Assert.Equal("user-1", credential.Uid);
        Assert.Equal("ent-1", credential.EnterpriseId);
        Assert.Equal("workbuddy.cn", credential.Domain);
        Assert.Equal("user-1:ent-1", credential.AccountTag);
        Assert.NotNull(credential.Remaining);
        Assert.True(credential.Remaining!.Value > TimeSpan.Zero, "默认账号不该是过期的");
    }

    // ── 空身份：这一组是重点 ──────────────────────────────────

    [Fact]
    public void 空身份账号没有_uid_没有企业_没有域()
    {
        var credential = new TestVault().EmptyIdentity().FirstCredential;

        Assert.Equal("", credential.Uid);
        Assert.Null(credential.EnterpriseId);
        Assert.Equal("", credential.Domain);
    }

    [Fact]
    public async Task 空身份账号发出去的请求带的是_X_No_族_而不是缺头()
    {
        // ★ 这就是「空身份」存在的全部理由
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);

        var credential = new TestVault().EmptyIdentity().FirstCredential;
        await client.ChatStreamAsync(credential, "{}", null, null, CancellationToken.None);

        var headers = fake.Last.Headers;

        Assert.Equal("1", fake.Last.Header("X-No-User-Id"));
        Assert.Equal("1", fake.Last.Header("X-No-Enterprise-Id"));
        Assert.Equal("1", fake.Last.Header("X-No-Department-Info"));

        // 反过来：**绝不能**出现带值的那三个头
        Assert.False(headers.ContainsKey("X-User-Id"));
        Assert.False(headers.ContainsKey("X-Enterprise-Id"));
        Assert.False(headers.ContainsKey("X-Domain"));
    }

    [Fact]
    public async Task 有身份的账号发出去的请求带的是带值的那三个头()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);

        await client.ChatStreamAsync(new TestVault().Add(uid: "u9", enterpriseId: "e9", domain: "d9.cn").FirstCredential,
            "{}", null, null, CancellationToken.None);

        Assert.Equal("u9", fake.Last.Header("X-User-Id"));
        Assert.Equal("e9", fake.Last.Header("X-Enterprise-Id"));
        Assert.Equal("d9.cn", fake.Last.Header("X-Domain"));

        Assert.False(fake.Last.HasHeader("X-No-User-Id"));
        Assert.False(fake.Last.HasHeader("X-No-Enterprise-Id"));
        Assert.False(fake.Last.HasHeader("X-No-Department-Info"));
    }

    // ── 过期时间 ──────────────────────────────────────────────

    [Fact]
    public void 可以指定各自的过期时间()
    {
        var now = DateTimeOffset.Parse("2026-10-07T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var vault = new TestVault(now)
            .Add(label: "还有两小时", expiresAtMs: now.AddHours(2).ToUnixTimeMilliseconds())
            .Add(label: "已经过期", expiresAtMs: now.AddHours(-2).ToUnixTimeMilliseconds())
            .NoExpiry("没有过期时间");

        Assert.Equal(now.AddHours(2).ToUnixTimeMilliseconds(), vault.Entries[0].ExpiresAtMs);
        Assert.Equal(now.AddHours(-2).ToUnixTimeMilliseconds(), vault.Entries[1].ExpiresAtMs);
        Assert.Equal(0, vault.Entries[2].ExpiresAtMs);
    }

    [Fact]
    public void Expired_和_NoExpiry_两个便捷方法造对了()
    {
        var vault = new TestVault().Expired().NoExpiry();

        Assert.True(vault.Entries[0].ExpiresAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, vault.Entries[1].ExpiresAtMs);
    }

    // ── id 格式 / 落盘往返 ────────────────────────────────────

    [Fact]
    public void id_是_uuid冒号enterpriseId_的格式_个人号以冒号结尾()
    {
        var vault = new TestVault().Add().EmptyIdentity();

        Assert.Equal("00000000-0000-0000-0000-000000000001:ent-1", vault.Entries[0].Id);
        Assert.Equal("00000000-0000-0000-0000-000000000002:", vault.Entries[1].Id);
    }

    [Fact]
    public void 标签可以指定()
    {
        var vault = new TestVault().Add(label: "我的主力号").Add(label: "备用号");
        Assert.Equal("我的主力号", vault.Entries[0].Label);
        Assert.Equal("备用号", vault.Entries[1].Label);
    }

    [Fact]
    public void 走一遍真实的保险库写读往返_格式对得上()
    {
        // 这一步很关键：造数据造不出「类型写错了」这种错
        //（HANDOVER 坑 2 就是被「读真实文件」那条测试抓到的）。
        // 所以夹具自己也要真的过一遍 Vault.Write / Vault.Read。
        var vault = new TestVault().Add(label: "中文标签").EmptyIdentity();

        var path = vault.WriteToTempFile();
        try
        {
            var read = Vault.Read(path);
            Assert.NotNull(read);
            Assert.Equal(2, read!.Accounts.Count);
            Assert.Equal("中文标签", read.Accounts[0].Label);
            Assert.Equal(vault.Entries[0].Id, read.Accounts[0].Id);
            Assert.Equal(vault.Entries[0].Uid, read.Accounts[0].Uid);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void 落盘之后的_activeId_以冒号结尾_不被_trim()
    {
        var vault = new TestVault().EmptyIdentity();
        var path = vault.WriteToTempFile();
        try
        {
            var read = Vault.Read(path);
            Assert.EndsWith(":", read!.ActiveId);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ── 时间字段类型（历史坑） ────────────────────────────────

    [Fact]
    public void 落盘的时间字段类型和真实文件一致_数值_ms_加_ISO_字符串()
    {
        var vault = new TestVault().Add();
        var json = vault.ToVaultJson();

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var account = document.RootElement.GetProperty("accounts")[0];

        Assert.Equal(System.Text.Json.JsonValueKind.Number, account.GetProperty("expiresAtMs").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, account.GetProperty("refreshExpiresAtMs").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.String, account.GetProperty("addedAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.String, account.GetProperty("lastSeenAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.String, account.GetProperty("lastCheckinAt").ValueKind);

        Assert.EndsWith("Z", account.GetProperty("addedAt").GetString());
    }

    [Fact]
    public void 可以手工造出时间字段全错类型的保险库()
    {
        var path = VaultJson.WriteTo(
            Path.Combine(Path.GetTempPath(), "buddydeck-testsupport-" + Guid.NewGuid().ToString("N")[..8], "auth.cn.json"),
            VaultJson.AllNumericTimestamps());

        try
        {
            Assert.Throws<System.Text.Json.JsonException>(() => Vault.Read(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void 普通账号和空身份可以混在一份保险库里()
    {
        var vault = new TestVault().Add().EmptyIdentity().Add(uid: "user-3");

        Assert.Equal(3, vault.Entries.Count);
        Assert.Equal("user-1", vault.Entries[0].Uid);
        Assert.Equal("", vault.Entries[1].Uid);
        Assert.Equal("user-3", vault.Entries[2].Uid);
        Assert.Equal(3, vault.Entries.Select(e => e.Id).Distinct().Count());
    }

    // ── 内存凭据源 ────────────────────────────────────────────

    [Fact]
    public void 内存凭据源可以随时加减账号()
    {
        var source = FakeCredentialSource.EmptyIdentity();
        Assert.Single(source.Load());

        source.Add(new Credential { AccessToken = "at-2", Uid = "u2", Label = "新号" });
        Assert.Equal(2, source.Load().Count);

        source.Clear();
        Assert.Empty(source.Load());
    }

    [Fact]
    public void 从保险库建凭据源()
    {
        var source = FakeCredentialSource.From(new TestVault().AddMany(3));
        Assert.Equal(3, source.Accounts.Count);
        Assert.Equal("user-1", source.Load()[0].Uid);
    }
}
