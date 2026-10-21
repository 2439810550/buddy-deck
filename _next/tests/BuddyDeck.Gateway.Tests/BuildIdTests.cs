using BuddyDeck.Gateway;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 代码指纹的测试。
///
/// 这些用例是**回归测试** —— 两条都是实测踩出来的真 bug：
///
/// 1. 指纹跨重启不稳定（`EnumerateFiles` 不保证顺序）
///    → 表现：同一份源码两次启动算出不同指纹，值就没意义了
/// 2. bin/obj 里的生成文件跟着 build 变，把指纹带跑
///    → 表现：`dotnet build` 之后 `/healthz` 永远报 stale=true，
///      用户以为自己的改动没生效，排查方向完全跑偏
/// </summary>
public class BuildIdTests
{
    [Fact]
    public void 同一棵树两次计算得到相同指纹()
    {
        // ★ 回归 1：不排序的话，文件系统给的顺序变化会让指纹抖动。
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "// a");
            File.WriteAllText(Path.Combine(dir, "B.cs"), "// b");
            File.WriteAllText(Path.Combine(dir, "C.cs"), "// c");

            var first = BuildId.Compute(dir);
            var second = BuildId.Compute(dir);

            Assert.Equal(first, second);
            Assert.Equal(12, first.Length);
            Assert.Matches("^[0-9a-f]{12}$", first);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 新增文件会改变指纹()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "// a");
            var before = BuildId.Compute(dir);

            File.WriteAllText(Path.Combine(dir, "B.cs"), "// b");
            var after = BuildId.Compute(dir);

            Assert.NotEqual(before, after);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 改文件内容会改变指纹()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, "// 短");
            var before = BuildId.Compute(dir);

            File.WriteAllText(file, "// 长很多的内容，确保 size 不同");
            var after = BuildId.Compute(dir);

            Assert.NotEqual(before, after);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void bin_和_obj_里的文件不影响指纹()
    {
        // ★ 回归 2：编译产物（bin/obj）每次 build 都变。
        // 不排除它们的话，`dotnet build` 之后指纹就变，
        // `/healthz` 会永远报 stale=true —— 那是**假警报**，
        // 会把用户引向「我的代码没生效」这个错误结论。
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "// 真源码");
            var before = BuildId.Compute(dir);

            // 造出编译产物的目录结构
            var binDir = Path.Combine(dir, "bin", "Debug", "net8.0");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "Generated.AssemblyInfo.cs"), "// 生成的");

            var objDir = Path.Combine(dir, "obj", "Debug");
            Directory.CreateDirectory(objDir);
            File.WriteAllText(Path.Combine(objDir, "GlobalUsings.g.cs"), "// 生成的");

            var after = BuildId.Compute(dir);

            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 子目录里的同名文件不会算成同一个()
    {
        // 键里必须带相对路径 —— 两个 sub/Program.cs 是两份代码。
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            var a = Path.Combine(dir, "one");
            var b = Path.Combine(dir, "two");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);

            File.WriteAllText(Path.Combine(a, "Program.cs"), "// 一");
            var one = BuildId.Compute(dir);

            // 再在另一个目录放一个**同名同大小**的文件
            File.WriteAllText(Path.Combine(b, "Program.cs"), "// 一");
            var two = BuildId.Compute(dir);

            Assert.NotEqual(one, two);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 目录不存在或没有_cs_文件时返回_unknown()
    {
        Assert.Equal(BuildId.Unknown, BuildId.Compute(Path.Combine(Path.GetTempPath(), "根本不存在-" + Guid.NewGuid())));
        Assert.Equal(BuildId.Unknown, BuildId.Compute(null));

        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "没有 .cs");
            Assert.Equal(BuildId.Unknown, BuildId.Compute(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 源码没动时不报_stale_改了才报()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-buildid-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "// 第一版");

            var info = new BuildInfo(dir);

            // 刚算完，磁盘和加载的是同一份
            Assert.False(info.IsStale, "刚启动就报 stale 是假警报");

            File.WriteAllText(Path.Combine(dir, "A.cs"), "// 第二版，更长一些的内容");

            Assert.True(info.IsStale, "源码改了应该报 stale");

            // ★ Loaded 是**启动时的快照**，不能跟着磁盘变 ——
            // 它代表「这个进程加载的是哪份代码」
            Assert.Equal(info.Loaded, info.Loaded);
            Assert.NotEqual(info.Loaded, info.Current);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 指纹算不出来时不报_stale()
    {
        // 不知道的事不能报成「不一致」—— 那会让用户以为改动没生效。
        var info = new BuildInfo(Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid()));

        Assert.Equal(BuildId.Unknown, info.Loaded);
        Assert.False(info.IsStale);
    }
}
