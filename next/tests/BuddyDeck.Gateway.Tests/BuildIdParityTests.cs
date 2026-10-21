using BuddyDeck.Gateway;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 指纹算法的**独立复算**校验。
///
/// 上面那些用例验的是「稳定、可区分、排除编译产物」；
/// 这条验的是「值本身算得对」—— 用一份手写的参考实现对照，
/// 而不是拿被测代码自己跟自己比（那是同义反复，抓不到系统性错误）。
/// </summary>
public class BuildIdParityTests
{
    [Fact]
    public void 和独立参考实现算出的值一致()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-parity-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Alpha.cs"), "// alpha\n");
            File.WriteAllText(Path.Combine(dir, "Beta.cs"), "// beta\n");

            var sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "Alpha.cs"), "// alpha in sub\n");

            // 编译产物：不该被计入
            var obj = Path.Combine(dir, "obj");
            Directory.CreateDirectory(obj);
            File.WriteAllText(Path.Combine(obj, "Generated.cs"), "// 生成的，不计\n");

            var actual = BuildId.Compute(dir);
            var expected = ReferenceCompute(dir);

            Assert.Equal(expected, actual);
            Assert.Equal(12, actual.Length);
            Assert.Matches("^[0-9a-f]{12}$", actual);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 参考实现：独立走一遍定义（相对路径:size:floor(mtimeMs)，排序后 sha256 前 12）。
    ///
    /// 用 SHA256.HashData + HEX 手工拼，不依赖被测代码的任何辅助方法。
    /// </summary>
    private static string ReferenceCompute(string root)
    {
        var parts = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var mtimeMs = (long)Math.Floor((info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds);
            parts.Add($"{relative}:{info.Length}:{mtimeMs}");
        }

        parts.Sort(StringComparer.Ordinal);

        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts));
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return hex[..12];
    }
}
