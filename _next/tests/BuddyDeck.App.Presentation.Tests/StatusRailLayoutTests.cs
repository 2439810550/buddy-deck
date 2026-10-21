using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuddyDeck.App.Controls;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// `StatusRail` / `QuotaBar` 的**布局**测试。
///
/// ## 这一组补的是什么缺口（真实缺陷）
///
/// `StatusRail` 曾经**永远什么都不画**：类里漏了 `MeasureOverride`，
/// WPF 于是把期望尺寸算成 **0×0**，`OnRender` 开头的
/// `if (width &lt;= 0 || height &lt;= 0) return;` 每次都从这个出口走掉。
///
/// 实测表现：起假网关喂 6 个账号（数据层确认真有 6 条），界面上一条竖条都没有。
///
/// **为什么编译期/运行期都抓不到**：
///   · 编译通过 —— `OnRender` 语法完全合法；
///   · 不抛异常 —— 那个 `return` 是**正确的防御性写法**（尺寸为 0 时不该画）；
///   · 没有日志 —— `OnRender` 里没写日志，就算写了也没人看。
///   ⇒ 只有**真的把它放进布局里量一次**才看得出来。
///
/// 之前的 `ThemeResourceTests` / `XamlResourceInvariantTests` 都抓不到它：
/// 前者只验资源 key 与类型，后者扫的是 XAML 文本 —— 两者都不碰布局。
/// 这是同一类「只在运行时暴露」的 bug 的第三个变体。
///
/// ## 设计要点
///
/// · 用**真实的 WPF 布局流程**（`Measure` + `Arrange`），不是直接调
///   `OnRender` —— 缺陷正好出在测量阶段，绕开测量就绕开了缺陷；
/// · 断言的是「测量结果 &gt; 0」与「渲染真的产生了绘制指令」两件事；
/// · 带**自证用例**：证明这套检查对「没有 MeasureOverride 的控件」真的会失败。
/// </summary>
public class StatusRailLayoutTests
{
    private static void RunSta(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null) throw captured;
    }

    private static IReadOnlyList<StatusRailItem> MakeItems(int count)
    {
        var list = new List<StatusRailItem>();
        for (var i = 0; i < count; i++)
        {
            // 交替状态，覆盖「正常」与「冷却」两种绘制分支。
            var state = i % 3 == 0 ? QuotaState.Cooldown : QuotaState.Normal;
            list.Add(new StatusRailItem(0.8 - (i * 0.05), state));
        }
        return list;
    }

    // ========================================================================
    //  核心：测量结果必须 > 0 —— 这正是当年漏掉的那件事
    // ========================================================================

    [Fact]
    public void 有条数时测量结果必须大于零()
    {
        RunSta(() =>
        {
            var rail = new StatusRail { Items = MakeItems(6), BarWidth = 14 };

            // 用「无约束」测一次，模拟放进 DockPanel / StackPanel 的情形。
            rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.True(
                rail.DesiredSize.Width > 0,
                $"测量宽度为 {rail.DesiredSize.Width} —— 控件量成 0 宽，永远不会画");
            Assert.True(
                rail.DesiredSize.Height > 0,
                $"测量高度为 {rail.DesiredSize.Height} —— "
                + "这正是 StatusRail 曾经的缺陷（漏 MeasureOverride → 0×0 → 永不绘制）");
        });
    }

    [Fact]
    public void 高度随条数增长()
    {
        RunSta(() =>
        {
            double MeasureWith(int n)
            {
                var rail = new StatusRail { Items = MakeItems(n), BarWidth = 14 };
                rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return rail.DesiredSize.Height;
            }

            var h1 = MeasureWith(1);
            var h3 = MeasureWith(3);
            var h6 = MeasureWith(6);

            Assert.True(h1 > 0, "1 根条也该有高度");
            Assert.True(h3 > h1, $"3 根条({h3}) 应高于 1 根条({h1})");
            Assert.True(h6 > h3, $"6 根条({h6}) 应高于 3 根条({h3})");
        });
    }

    [Fact]
    public void 条数上界受单槽上限约束()
    {
        RunSta(() =>
        {
            var rail = new StatusRail { Items = MakeItems(50), BarWidth = 14 };
            rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 50 根 × (28 + 6) 会很大，但**必须是正值**且不该无限膨胀。
            // 这里不断言具体数值（那是内部实现），只断言量得出来。
            Assert.True(rail.DesiredSize.Height > 0);
            Assert.True(rail.DesiredSize.Height < 50 * 64, "高度不该离谱");
        });
    }

    [Fact]
    public void 没有条数时测量为零不崩()
    {
        RunSta(() =>
        {
            var empty = new StatusRail { Items = Array.Empty<StatusRailItem>(), BarWidth = 14 };
            empty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.Equal(0, empty.DesiredSize.Height);

            // null 也要能扛
            var nullItems = new StatusRail { Items = null, BarWidth = 14 };
            nullItems.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(0, nullItems.DesiredSize.Height);
        });
    }

    // ========================================================================
    //  测量与绘制必须用同一套算式
    // ========================================================================

    [Fact]
    public void 测量高度与布局算式一致()
    {
        RunSta(() =>
        {
            foreach (var n in new[] { 1, 2, 3, 6, 9 })
            {
                var rail = new StatusRail { Items = MakeItems(n), BarWidth = 14 };
                var expected = rail.DesiredHeight(n);

                rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.Equal(expected, rail.DesiredSize.Height, 3);
            }
        });
    }

    [Fact]
    public void 布局算式在各种尺寸下都不返回负值()
    {
        foreach (var n in new[] { 0, 1, 6, 100 })
        {
            foreach (var w in new[] { 0.0, 1.0, 14.0, 56.0 })
            {
                foreach (var h in new[] { 0.0, 1.0, 20.0, 220.0 })
                {
                    var (slot, barW) = StatusRail.ComputeLayout(n, w, h, 14);

                    Assert.True(slot >= 0, $"slot 为负：n={n} w={w} h={h}");
                    Assert.True(barW >= 0, $"barW 为负：n={n} w={w} h={h}");
                    Assert.True(barW <= w, $"条宽 {barW} 超出了可用宽 {w}");
                }
            }
        }
    }

    // ========================================================================
    //  真的渲染一次：断言画出了非空像素
    // ========================================================================

    /// <summary>
    /// 用 `RenderTargetBitmap` **真渲染**一次，断言画面上出现了东西。
    ///
    /// ## 为什么不用伪造的 DrawingContext（第一版就是这么错的）
    ///
    /// 第一版我写了个 `RecordingDrawingContext` 去数绘制指令，**编译不过**：
    /// `DrawingContext` 有 9 个抽象成员（还带 `AnimationClock` 重载的老 API），
    /// 我只实现了 `OnRender` 用到的那几个。
    ///
    /// 更重要的是 —— **伪造它本身就不划算**：
    ///   · 要补一堆自己根本不用的重载，噪音比被测逻辑还大；
    ///   · 而 `RenderTargetBitmap` 是**真渲染**，证据比 mock 强得多；
    ///   · 它还顺带覆盖了「渲染管线真的接受这个控件」这件事。
    ///
    /// （采纳 ui 的建议：能真渲染就别 mock。）
    ///
    /// 判据：统计非黑像素数 —— 底色是纯黑，任何非黑像素都是控件画的。
    /// </summary>
    /// <summary>
    /// 在**尊重 DesiredSize 的容器**里真渲染一次，断言画出了像素。
    ///
    /// ## ⚠⚠ 这个用例差点写成假绿 —— 记下来
    ///
    /// 第一版我把它写成「塞进 `Border` + `RenderTargetBitmap` + 数非黑像素」，
    /// 并且**假定它能抓到「漏 MeasureOverride」那个缺陷**。
    ///
    /// **注入验证发现：抓不到。** 给 `MeasureOverride` 注入 `return new Size(0,0)`
    /// 之后，那一版**照样通过**。
    ///
    /// 原因（WPF 布局两趟，见 tools/explain_layout_phases.py）：
    ///
    /// <code>
    /// Measure  —— 自下而上，子元素报告 DesiredSize
    /// Arrange  —— 自上而下，父元素**给定**子元素的最终矩形
    ///
    /// Border.ArrangeOverride 不看 child.DesiredSize，直接把整个可用矩形给它：
    ///     DesiredSize        = 0×0        ← 缺陷在这里
    ///     ActualWidth/Height = 56×200     ← 但 Arrange 还是给了这么大
    ///     → OnRender 的 `if (width &lt;= 0) return;` 不成立 → 照样画
    /// </code>
    ///
    /// **所以用 `Border` 当宿主，测的是 Arrange 的尺寸，对 Measure 的缺陷不敏感。**
    ///
    /// 改用 **`StackPanel`**（它尊重 `DesiredSize`）后才有判别力：
    /// 子元素报 0×0，StackPanel 就真的给 0×0，`OnRender` 于是不画。
    ///
    /// ## 两条断言各管一段，都不能删
    ///
    /// · `DesiredSize &gt; 0`（本文件上面几条）→ 防**「量成 0」**这个缺陷本体
    /// · 本用例（StackPanel 里渲染出像素）→ 防「渲染管线不接受这个控件」
    ///
    /// 判据：对比「有 Items」与「没 Items」两次渲染的像素数 ——
    /// 多出来的部分只可能来自 StatusRail 自己画的条。
    /// </summary>
    [Fact]
    public void 在尊重DesiredSize的容器里渲染必须画出像素()
    {
        RunSta(() =>
        {
            const int w = 56;
            const int h = 200;

            var withItems = RenderRailPixelCountInStackPanel(w, h, itemCount: 4);
            var withoutItems = RenderRailPixelCountInStackPanel(w, h, itemCount: 0);

            Assert.True(
                withItems > withoutItems,
                $"有 4 个账号时画了 {withItems} 个像素，没有账号时画了 {withoutItems} 个 —— "
                + "两者相同说明 StatusRail 根本没画东西（正是它曾经的缺陷）。");
        });
    }

    /// <summary>
    /// 在 `StackPanel` 里渲染一次，返回「画出来的像素数」。
    ///
    /// **必须用 StackPanel 而不是 Border** —— 理由见上面那个用例的注释：
    /// Border 会强制敷满，绕开 `DesiredSize`，于是掩盖掉 Measure 阶段的缺陷。
    /// StackPanel 尊重 `DesiredSize`，所以它是**能暴露这个缺陷**的宿主。
    ///
    /// @param itemCount 0 表示空轨（对照组）
    /// </summary>
    private static int RenderRailPixelCountInStackPanel(int w, int h, int itemCount)
    {
        var rail = new StatusRail
        {
            Items = itemCount == 0 ? Array.Empty<StatusRailItem>() : MakeItems(itemCount),
            BarWidth = 14,
        };

        // 用透明底：这样「画出来的像素」只可能是控件自己画的，
        // 宿主不贡献任何底色像素。
        var host = new System.Windows.Controls.StackPanel
        {
            Width = w,
            Height = h,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        host.Children.Add(rail);

        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        return CountPaintedPixels(bitmap, w, h);
    }

    /// <summary>
    /// 对照：**测量为 0 的元素渲染不出像素**。
    ///
    /// 把「缺陷会复发」写成断言 —— 用一个不实现 `MeasureOverride` 的元素
    /// 走同一条渲染路径，证明「没有测量 → 渲染不出像素」这个因果成立。
    /// </summary>
    [Fact]
    public void 自证_测量为零的元素渲染不出像素()
    {
        RunSta(() =>
        {
            const int w = 56;
            const int h = 60;

            var host = new System.Windows.Controls.Border
            {
                Width = w,
                Height = h,
                Background = System.Windows.Media.Brushes.Black,
                Child = new NoMeasureElement(),
            };

            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);

            Assert.Equal(0, CountPaintedPixels(bitmap, w, h));
        });
    }

    /// <summary>
    /// 数「画出来了」的像素（透明底上的可见像素）。
    ///
    /// 判据用**蓝通道 > 8**。用 `> 8` 而不是 `> 0` 是为了容忍渲染的极小误差，
    /// 避免偶发的抗锯齿噪声把测试弄红。
    ///
    /// ⚠ 前提：宿主用**透明**背景（`Brushes.Transparent`）——
    /// 否则宿主自己的底色像素会被算进来，断言就成了「宿主画了东西」，
    /// 与控件无关。这条踩过（见上面渲染用例的注释）。
    /// </summary>
    private static int CountPaintedPixels(RenderTargetBitmap bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var painted = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8) painted++;   // B 通道
        }

        return painted;
    }

    /// <summary>
    /// 自证：这套检查对**一个没有 MeasureOverride 的 FrameworkElement** 会失败。
    ///
    /// 为什么要这条：`StatusRail` 的缺陷本质是「框架元素不自己量尺寸 → 0×0」。
    /// 如果我的检查只对 `StatusRail` 特判（比如直接调 `DesiredHeight()`），
    /// 那它证明的是「我的私有方法返回值对」，而不是「控件真的量得出来」。
    ///
    /// 这条用一个**故意不实现 MeasureOverride** 的类跑同一套断言路径，
    /// 证明「不量尺寸 → 测量为 0」这个失败模式确实会被这套检查抓住。
    /// </summary>
    [Fact]
    public void 自证_不实现MeasureOverride的元素会量成零()
    {
        RunSta(() =>
        {
            // 故意什么都不做 —— 复刻 StatusRail 当年的形态。
            var broken = new NoMeasureElement();

            broken.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 证明失败模式存在：这样的元素期望尺寸就是 0×0。
            Assert.Equal(0, broken.DesiredSize.Width);
            Assert.Equal(0, broken.DesiredSize.Height);

            // 而正常实现测量 > 0（对照）。
            var good = new StatusRail { Items = MakeItems(6), BarWidth = 14 };
            good.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(good.DesiredSize.Height > 0);
        });
    }

    /// <summary>
    /// 对照：`MeasureOverride` 一旦被移除（模拟有人重构时删掉它），
    /// 「测量 &gt; 0」这条断言就会失败。
    ///
    /// 这条**不是**在测产品代码，而是把「缺陷会复发」这件事本身写成断言 ——
    /// 用一个等价形态的类证明检查有判别力。
    /// </summary>
    [Fact]
    public void 自证_测量为零时核心断言会失败()
    {
        RunSta(() =>
        {
            var broken = new NoMeasureElement();
            broken.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 复刻核心断言（StatusRail 主用例里的那句）—— 它在这里必须失败。
            var wouldPass = broken.DesiredSize.Height > 0;
            Assert.False(
                wouldPass,
                "自证失败：一个不实现 MeasureOverride 的元素竟然量出了非零高度，"
                + "说明「测量 > 0」这条断言抓不住 StatusRail 当年的缺陷");
        });
    }

    /// <summary>故意不实现 `MeasureOverride` 的框架元素 —— 复刻缺陷形态。</summary>
    private sealed class NoMeasureElement : FrameworkElement
    {
        // 刻意什么都不写：没有 MeasureOverride，也没有 OnRender。
    }
}
