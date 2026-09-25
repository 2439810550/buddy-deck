using System.IO;
using System.Windows;

namespace LicenseTool;

/// <summary>
/// 往剪贴板写文本。**带重试**。
///
/// ## 为什么必须重试
///
/// Windows 的剪贴板是**全局独占**的：同一时刻只有一个进程能打开它。
/// 输入法、微信、QQ、截图工具、浏览器都在抢 —— 而且抢的窗口很短
/// （几十毫秒）。撞上就抛 `COMException`：
///
///     OpenClipboard 失败 (0x800401D0)
///
/// 直接 `Clipboard.SetText()` 的话，用户会看到"复制失败"，
/// 但他明明什么都没干 —— 体验很莫名。
///
/// ## 做法
///
/// 撞了等 60ms 再试，最多 5 次（共约 300ms）。实际观测到的情况是
/// 第 2 次就成功 —— 那点占用时间不到一次重试的间隔。
///
/// ## 为什么不用 Clipboard.SetDataObject 的 copy 参数
///
/// `SetDataObject(data, copy: true)` 会阻塞到剪贴板内容被取走，
/// 在某些情况下会让 UI 卡住。这里用最朴素的 SetText + 重试。
/// </summary>
public static class ClipboardHelper
{
    private const int Attempts = 5;
    private const int DelayMs = 60;

    /// <summary>
    /// 写文本。成功返回 true；全试完还是失败返回 false（不抛异常）。
    /// </summary>
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                // 注意：Clipboard.SetText 内部会先 OpenClipboard 再 SetDataObject，
                // 两个动作之间别的进程可能插进来 —— 所以异常可能在任何一步抛出。
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                if (attempt == Attempts - 1) return false;

                // Thread.Sleep 会卡 UI 线程，但这几十毫秒是必要的等待，
                // 而且比让用户看到"复制失败"要好。
                System.Threading.Thread.Sleep(DelayMs);
            }
        }
        return false;
    }

    /// <summary>
    /// 读文本。读不到返回 null。
    /// </summary>
    public static string? TryGetText()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception)
            {
                if (attempt == Attempts - 1) return null;
                System.Threading.Thread.Sleep(DelayMs);
            }
        }
        return null;
    }
}
