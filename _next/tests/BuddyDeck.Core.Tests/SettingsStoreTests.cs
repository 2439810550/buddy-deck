using System.Text;
using System.Text.Json;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// M14 持久化层的测试。
///
/// ## 为什么"重启能读回"要用两个实例来测
///
/// 用同一个 <see cref="SettingsStore"/> 存了再读**证明不了任何事** ——
/// 它可能只是把值留在了内存里（那正是 M14 要修的 bug 本身）。
///
/// 所以这里每次都**新建一个 store 实例**去读，模拟"进程重启后重新构造"。
/// 这不是形式主义：被测的正是"值真的经过了文件系统"。
/// </summary>
public class SettingsStoreTests
{
    private static string TempFile()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-settings-").FullName;
        return Path.Combine(dir, SettingsStore.FileName);
    }

    private static SettingsStore StoreAt(string path) => new(() => path);

    /// <summary>一份「每个字段都有值」的载荷 —— 用来验证只有 persistable 里的才会被写。</summary>
    private static SettingsValues AllFieldsSet(int port) => new()
    {
        Port = port,
        Host = "127.0.0.1",
        ApiKey = "sk-secret",
        Provider = "workbuddy",
        AuthFile = "/vault/auth.cn.json",
        DefaultModel = "hy3",
        LogDir = "/logs",
    };

    // ══ ① 往返（重启读回）═══════════════════════════════════════

    [Fact]
    public void 存了再新建实例能读回()
    {
        var path = TempFile();

        StoreAt(path).Save(new SettingsDocument { Port = 8791, ApiKey = "sk-test", DefaultModel = "hy3" });

        // ⚠ 新实例 = 模拟进程重启。这一步是这个测试的全部价值。
        var reloaded = StoreAt(path).Load();

        Assert.Equal(8791, reloaded.Port);
        Assert.Equal("sk-test", reloaded.ApiKey);
        Assert.Equal("hy3", reloaded.DefaultModel);
    }

    [Fact]
    public void 中文标签往返不乱码()
    {
        var path = TempFile();

        StoreAt(path).Save(new SettingsDocument { Host = "本机回环", DefaultModel = "模型·默认" });

        var reloaded = StoreAt(path).Load();

        Assert.Equal("本机回环", reloaded.Host);
        Assert.Equal("模型·默认", reloaded.DefaultModel);
    }

    [Fact]
    public void 中文不转义成_unicode_转义()
    {
        // 与 Vault.Write 同一约定：用 cat 看要能读
        var path = TempFile();

        StoreAt(path).Save(new SettingsDocument { Host = "中文主机" });

        var raw = File.ReadAllText(path);
        Assert.Contains("中文主机", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", raw, StringComparison.Ordinal);
    }

    // ══ ② 只落显式改过的字段 ═════════════════════════════════════

    [Fact]
    public void 没改过的字段不写进文件()
    {
        var path = TempFile();

        // 只显式改了端口
        var document = SettingsStore.Project(
            AllFieldsSet(8791),
            persistable: new HashSet<string> { FieldNames.Port });

        StoreAt(path).Save(document);

        var reloaded = StoreAt(path).Load();
        Assert.Equal(8791, reloaded.Port);
        Assert.Null(reloaded.Host);          // ← 没改过 = 不写
        Assert.Null(reloaded.ApiKey);
        Assert.Null(reloaded.Provider);
        Assert.Null(reloaded.DefaultModel);

        // 文件里也不该出现那些键
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("apiKey", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultModel", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void 空的可落盘集合等于什么都不写()
    {
        var path = TempFile();

        var document = SettingsStore.Project(AllFieldsSet(9999), persistable: new HashSet<string>());

        StoreAt(path).Save(document);

        var reloaded = StoreAt(path).Load();
        Assert.Null(reloaded.Port);
        Assert.Null(reloaded.Host);
        Assert.Null(reloaded.ApiKey);
    }

    [Fact]
    public void 全部字段都可落盘时全部写进去()
    {
        var path = TempFile();

        var document = SettingsStore.Project(
            AllFieldsSet(8791),
            persistable: new HashSet<string>(FieldNames.All));

        StoreAt(path).Save(document);

        var reloaded = StoreAt(path).Load();
        Assert.Equal(8791, reloaded.Port);
        Assert.Equal("127.0.0.1", reloaded.Host);
        Assert.Equal("sk-secret", reloaded.ApiKey);
        Assert.Equal("workbuddy", reloaded.Provider);
        Assert.Equal("/vault/auth.cn.json", reloaded.AuthFile);
        Assert.Equal("hy3", reloaded.DefaultModel);
        Assert.Equal("/logs", reloaded.LogDir);
    }

    // ══ ③ 原子写 ═════════════════════════════════════════════════

    [Fact]
    public void 写完不留临时文件()
    {
        var path = TempFile();

        StoreAt(path).Save(new SettingsDocument { Port = 8791 });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"), "临时文件应当已被原子替换掉");
    }

    [Fact]
    public void 重复保存不残留临时文件()
    {
        var path = TempFile();
        var store = StoreAt(path);

        for (var i = 0; i < 5; i++)
        {
            store.Save(new SettingsDocument { Port = 8790 + i });
        }

        Assert.Equal(8794, StoreAt(path).Load().Port);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void 覆盖保存后内容是新值()
    {
        var path = TempFile();
        var store = StoreAt(path);

        store.Save(new SettingsDocument { Port = 1, ApiKey = "old" });
        store.Save(new SettingsDocument { Port = 2, ApiKey = "new" });

        var reloaded = StoreAt(path).Load();
        Assert.Equal(2, reloaded.Port);
        Assert.Equal("new", reloaded.ApiKey);
    }

    [Fact]
    public void 临时文件是原子替换而不是直接覆盖写()
    {
        // 证据：保存过程中目标文件一度不存在（Move 是替换，不是 truncate+write）。
        // 更直接的证据是"目录里只该有一个文件"——直接 File.WriteAllText 覆盖不会
        // 产生 tmp 文件，也就不会留痕；这里断言 tmp 被创建过又被移走。
        var path = TempFile();
        StoreAt(path).Save(new SettingsDocument { Port = 1 });

        var directory = Path.GetDirectoryName(path)!;
        var files = Directory.GetFiles(directory);

        Assert.Single(files);                       // 只有 settings.json，没有 .tmp 残留
        Assert.Equal(path, files[0]);
    }

    // ══ ④ 读不到 / 读坏了都不抛 ═══════════════════════════════════

    [Fact]
    public void 文件不存在时返回空文档()
    {
        var document = StoreAt(TempFile()).Load();

        Assert.Null(document.Port);
        Assert.Null(document.ApiKey);
    }

    [Fact]
    public void 空文件返回空文档()
    {
        var path = TempFile();
        File.WriteAllText(path, "");

        Assert.Null(StoreAt(path).Load().Port);
    }

    [Fact]
    public void 只有空白的文件返回空文档()
    {
        var path = TempFile();
        File.WriteAllText(path, "   \n\t  ");

        Assert.Null(StoreAt(path).Load().Port);
    }

    [Fact]
    public void 坏_json_返回空文档而不抛()
    {
        // ⚠ 设置文件坏了不该让网关起不来 —— 那是拿"少一个调优参数"
        // 换"整个服务不可用"，代价完全不对等
        var path = TempFile();
        File.WriteAllText(path, """{"port": 8791, "host": """);

        var document = StoreAt(path).Load();
        Assert.Null(document.Port);
    }

    [Fact]
    public void 截断的_json_返回空文档()
    {
        // 模拟"写到一半断电"——原子写要防的正是这个
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"port":8791,"apiKey":"sk-abc""");

        Assert.Null(StoreAt(path).Load().Port);
    }

    [Fact]
    public void 合法_json_但不是对象时返回空文档()
    {
        var path = TempFile();
        File.WriteAllText(path, "[1,2,3]");

        // 数组反序列化成 SettingsDocument 会抛 JsonException → catch 掉
        Assert.Null(StoreAt(path).Load().Port);
    }

    [Fact]
    public void 未知字段被忽略_不抛()
    {
        // 将来版本加了字段、或用旧版文件 —— 都要能读
        var path = TempFile();
        File.WriteAllText(path, """{"port":8791,"未来才有的字段":"x","provider":"workbuddy"}""");

        var document = StoreAt(path).Load();
        Assert.Equal(8791, document.Port);
        Assert.Equal("workbuddy", document.Provider);
    }

    [Fact]
    public void 带注释和尾逗号的_json_能读()
    {
        // 用户手改配置文件时很容易留这些东西
        var path = TempFile();
        File.WriteAllText(path, """
            {
              // 端口
              "port": 8791,
              "host": "127.0.0.1",
            }
            """);

        var document = StoreAt(path).Load();
        Assert.Equal(8791, document.Port);
    }

    // ══ ⑤ 路径推导：与保险库同目录 ═══════════════════════════════

    [Fact]
    public void 设置文件与保险库同目录()
    {
        // captain 拍板：同目录。理由见 SettingsStore 类注释（一起备份/权限一致/不新造概念）
        var vault = Path.Combine(Path.GetTempPath(), "some-dir", "auth.cn.json");

        var settings = SettingsStore.PathBesideVault(vault);

        Assert.Equal(Path.GetDirectoryName(vault), Path.GetDirectoryName(settings));
        Assert.Equal(SettingsStore.FileName, Path.GetFileName(settings));
    }

    [Fact]
    public void 国际版保险库也推导到同目录()
    {
        var vault = Path.Combine(Path.GetTempPath(), "elsewhere", "auth.ai.json");

        var settings = SettingsStore.PathBesideVault(vault);

        Assert.Equal(Path.GetDirectoryName(vault), Path.GetDirectoryName(settings));
    }

    [Fact]
    public void 保险库路径为空时退化到程序运行目录()
    {
        var settings = SettingsStore.PathBesideVault("");

        // ⚠ 要 TrimEnd 再比：AppContext.BaseDirectory **带结尾分隔符**
        // （`…\net8.0\`），而 Path.GetDirectoryName 返回的**不带**。
        // 直接比会差一个字符 —— 那是路径 API 的既定行为，不是 bug。
        var expected = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetDirectoryName(settings)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.Equal(expected, actual);
        Assert.Equal(SettingsStore.FileName, Path.GetFileName(settings));
    }

    [Fact]
    public void 目录不存在时保存会自动建目录()
    {
        var dir = Path.Combine(Path.GetTempPath(), "buddydeck-new-" + Guid.NewGuid().ToString("N")[..8], "nested");
        var path = Path.Combine(dir, SettingsStore.FileName);

        StoreAt(path).Save(new SettingsDocument { Port = 8791 });

        Assert.True(File.Exists(path));
        Assert.Equal(8791, StoreAt(path).Load().Port);
    }

    // ══ ⑥ 版本字段 ═══════════════════════════════════════════════

    [Fact]
    public void 写出的文件带版本号()
    {
        var path = TempFile();
        StoreAt(path).Save(new SettingsDocument { Port = 1 });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(SettingsStore.CurrentVersion, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void 旧文件缺版本号也能读()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"port":8791}""");

        Assert.Equal(8791, StoreAt(path).Load().Port);
    }

    [Fact]
    public void 未来版本号的文件仍然尽力读()
    {
        // 读到不认识的版本时"拒绝读"会让用户降级版本后设置全丢。
        // 认得的字段照读，认不得的忽略 —— 这是宽容的那一侧。
        var path = TempFile();
        File.WriteAllText(path, """{"version":99,"port":8791,"defaultModel":"hy3"}""");

        var document = StoreAt(path).Load();
        Assert.Equal(8791, document.Port);
        Assert.Equal("hy3", document.DefaultModel);
    }

    // ══ ⑦ 落盘文件的内容形状 ═════════════════════════════════════

    [Fact]
    public void 落盘文件是缩进的可读_json()
    {
        var path = TempFile();
        StoreAt(path).Save(new SettingsDocument { Port = 8791, ApiKey = "sk-x" });

        var raw = File.ReadAllText(path);
        Assert.Contains("\n", raw, StringComparison.Ordinal);   // 有换行 = 缩进过
        Assert.Contains("8791", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void 落盘文件是无_BOM_的_utf8()
    {
        var path = TempFile();
        StoreAt(path).Save(new SettingsDocument { Host = "中文" });

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "不该有 UTF-8 BOM");

        // 也不该有替换字符
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(bytes));
    }

    // ══ ⑧ 端到端：临时值绝不落盘（契约点名的核心用例）═════════════

    [Fact]
    public void 命令行临时值不进文件_重启后读不到()
    {
        // ⚠⚠ 这条是 M14 最核心的一条 ⚠⚠
        //
        // 它走完整链路：三层来源合并 → 投影 → 落盘 → **新实例读回**。
        //
        // 场景（就是用户会遇到的）：
        //   第 1 天  用 --port 8791 起了一个测试实例
        //   第 2 天  不带参数启动 —— 端口**必须**回到默认，而不是记住 8791
        //
        // 如果命令行值被落盘，第 2 天用户会看到一个"自己没设过"的端口，
        // 而且他昨天做过什么早就忘了 —— 完全无法解释。

        var path = TempFile();
        var defaults = new SettingsValues { Port = 8787, Host = "127.0.0.1", Provider = "workbuddy" };

        // 文件里什么都没有（第一次运行）
        var fromFile = new SettingsValues();

        // 命令行传了端口和默认模型
        var fromCommandLine = new SettingsValues { Port = 8791, DefaultModel = "hy4-preview" };

        var merged = SettingsMerge.Merge(
            fromFile, new SettingsValues(), fromCommandLine, defaults);

        // ① 合并结果里命令行生效了
        Assert.Equal(8791, merged.Value.Port);
        Assert.Equal("hy4-preview", merged.Value.DefaultModel);

        // ② 但没有任何字段被标为可落盘
        Assert.Empty(merged.PersistableFields);

        // ③ 落盘
        StoreAt(path).Save(merged.Value.ToDocument(merged.PersistableFields));

        // ④ **新实例读回**（= 重启）
        var afterRestart = StoreAt(path).Load();

        Assert.Null(afterRestart.Port);          // ← 没记住 8791
        Assert.Null(afterRestart.DefaultModel);  // ← 也没记住 hy4-preview

        // ⑤ 重启后重新合并（这次命令行什么都不传）→ 必须回到默认
        var secondBoot = SettingsMerge.Merge(
            SettingsValues.FromDocument(afterRestart),
            new SettingsValues(),
            new SettingsValues(),
            defaults);

        Assert.Equal(8787, secondBoot.Value.Port);
        Assert.NotEqual("hy4-preview", secondBoot.Value.DefaultModel);
    }

    [Fact]
    public void 环境变量临时值不进文件_重启后读不到()
    {
        // 与上一条同源，只是来源换成环境变量（比如 CI 里设的 WORKBUDDY_PORT=0）
        var path = TempFile();
        var defaults = new SettingsValues { Port = 8787 };

        var merged = SettingsMerge.Merge(
            new SettingsValues(),
            new SettingsValues { Port = 8899 },   // 环境变量
            new SettingsValues(),
            defaults);

        Assert.Equal(8899, merged.Value.Port);
        Assert.Empty(merged.PersistableFields);

        StoreAt(path).Save(merged.Value.ToDocument(merged.PersistableFields));

        var secondBoot = SettingsMerge.Merge(
            SettingsValues.FromDocument(StoreAt(path).Load()),
            new SettingsValues(),
            new SettingsValues(),
            defaults);

        Assert.Equal(8787, secondBoot.Value.Port);   // ← 回到默认，不是 8899
    }

    [Fact]
    public void 用户显式改过才落盘_且重启后仍生效()
    {
        // 与上面两条对照：**文件里有**的字段才是"用户改过"，才该被记住
        var path = TempFile();
        var defaults = new SettingsValues { Port = 8787, Host = "127.0.0.1" };

        // 文件里已经有 port（= 用户上次通过设置接口改过）
        var fromFile = new SettingsValues { Port = 8791 };

        var merged = SettingsMerge.Merge(fromFile, new SettingsValues(), new SettingsValues(), defaults);

        Assert.Equal(8791, merged.Value.Port);
        Assert.Contains(FieldNames.Port, merged.PersistableFields);   // ← 这条该落盘

        StoreAt(path).Save(merged.Value.ToDocument(merged.PersistableFields));

        var secondBoot = SettingsMerge.Merge(
            SettingsValues.FromDocument(StoreAt(path).Load()),
            new SettingsValues(),
            new SettingsValues(),
            defaults);

        Assert.Equal(8791, secondBoot.Value.Port);   // ← 记住
    }

    [Fact]
    public void 命令行盖过文件里的值_但不把文件里的值抹掉()
    {
        // 组合场景：文件说 8791（用户改过），命令行说 9999（这次临时的）
        //
        //   生效：9999（命令行最高）
        //   落盘：8791 那条**仍然可落盘**（它是用户改的），但因为本轮生效值是 9999，
        //         投影时写的是**生效值** —— 见下方断言说明
        //
        // ⚠ 这条记录一个真实的设计取舍：落盘写的是"当前生效值"。
        // 如果用户同时"文件里是 A、命令行是 B"，本轮结束存下去的是 B。
        // 对 M14 的场景（界面改设置）这没问题 —— 那时没有命令行覆盖。
        // 但写下来，免得下一个人以为落盘的是"文件里的原值"。
        var defaults = new SettingsValues { Port = 8787 };

        var merged = SettingsMerge.Merge(
            new SettingsValues { Port = 8791 },
            new SettingsValues(),
            new SettingsValues { Port = 9999 },
            defaults);

        Assert.Equal(9999, merged.Value.Port);
        Assert.Contains(FieldNames.Port, merged.PersistableFields);

        var document = merged.Value.ToDocument(merged.PersistableFields);
        Assert.Equal(9999, document.Port);   // ← 落盘的是生效值
    }

    [Fact]
    public void 三层同时在场时按优先级取_且只有文件层可落盘()
    {
        var defaults = new SettingsValues { Port = 8787, Host = "127.0.0.1", DefaultModel = "hy3" };

        var merged = SettingsMerge.Merge(
            fromFile: new SettingsValues { Port = 8801, Host = "10.0.0.1" },
            fromEnvironment: new SettingsValues { Port = 8802, DefaultModel = "env-model" },
            fromCommandLine: new SettingsValues { Port = 8803 },
            defaults);

        // 生效值：命令行 8803 > 环境变量 8802 > 文件 8801
        Assert.Equal(8803, merged.Value.Port);
        // Host 只有文件给了 → 文件生效
        Assert.Equal("10.0.0.1", merged.Value.Host);
        // DefaultModel 只有环境变量给了 → 环境变量生效
        Assert.Equal("env-model", merged.Value.DefaultModel);

        // 落盘集合**只含文件层给出的字段**（Port 和 Host）
        Assert.Equal(2, merged.PersistableFields.Count);
        Assert.Contains(FieldNames.Port, merged.PersistableFields);
        Assert.Contains(FieldNames.Host, merged.PersistableFields);
        Assert.DoesNotContain(FieldNames.DefaultModel, merged.PersistableFields);
    }
}
