using System.Runtime.CompilerServices;
using System.Text;

namespace BuddyDeck.Gateway;

/// <summary>
/// 把上游的 SSE 响应体读成一条条 data 载荷。
///
/// ## 为什么不用 ReadAsStringAsync
///
/// 那会把整个响应体缓冲下来 —— 流式的意义就没了：用户要等到最后一个
/// token 生成完才看到第一个字。这里用 <see cref="StreamReader.ReadAsync(Memory{char}, CancellationToken)"/>
/// 加上**增量扫描行边界**，读到一行就吐一行，一行都不多缓冲。
///
/// ## 为什么按行而不是按块
///
/// 网络回来的块边界和 SSE 的事件边界没有关系：一次 read 可能拿到半行，
/// 也可能拿到三行半。所以必须自己维护一个「还没成行的尾巴」缓冲区。
/// </summary>
public static class SseReader
{
    /// <summary>单行上限。防御上游吐一个超长行把内存吃光。</summary>
    private const int MaxLineLength = 4 * 1024 * 1024;

    /// <summary>
    /// 逐条读 SSE 事件。
    ///
    /// @param stream 上游响应流（调用方负责释放）。
    /// @returns 每条事件的 data 载荷（已去掉 `data: ` 前缀、已 trim）。
    ///          多行 data 会按 SSE 规范用 `\n` 连接。
    /// </summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // leaveOpen: true —— 流的生命周期归调用方（UpstreamResult.Response）
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192, leaveOpen: true);

        var pending = new StringBuilder();   // 还没成行的尾巴
        var data = new StringBuilder();      // 当前事件累积的 data:
        var hasData = false;

        var buffer = new char[8192];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;            // 上游关流

            var offset = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;

                pending.Append(buffer, offset, i - offset);
                offset = i + 1;

                var line = TrimLineEnding(pending);
                pending.Clear();

                if (line.Length == 0)
                {
                    // 空行 = 事件结束
                    if (hasData)
                    {
                        yield return data.ToString();
                        data.Clear();
                        hasData = false;
                    }
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // 按 SSE 规范去掉冒号后那**一个**空格
                    var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                }
                // 其他字段（event: / id: / retry: / 以 : 开头的注释）忽略：
                // 上游用的是最朴素的 data-only 形态，多认反而容易出错。

                if (pending.Length > MaxLineLength)
                {
                    throw new IOException($"SSE 单行超过 {MaxLineLength} 字符");
                }
            }

            pending.Append(buffer, offset, read - offset);
        }

        // 上游没给末尾空行就断流 —— 把攒着的那条吐出来，别丢。
        if (hasData) yield return data.ToString();
        else if (pending.Length > 0)
        {
            var tail = TrimLineEnding(pending);
            if (tail.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return tail.Length > 5 && tail[5] == ' ' ? tail[6..] : tail[5..];
            }
        }
    }

    /// <summary>去掉行尾的 `\r`（HTTP 的行结束是 `\r\n`）。</summary>
    private static string TrimLineEnding(StringBuilder sb)
    {
        var end = sb.Length;
        if (end > 0 && sb[end - 1] == '\r') end--;
        return sb.ToString(0, end);
    }
}
