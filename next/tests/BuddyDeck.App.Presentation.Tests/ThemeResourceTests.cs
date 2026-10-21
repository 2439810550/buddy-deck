using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 资源字典的**加载测试** —— 证明五本字典真的能被 WPF 解析，
/// 而且每个具名 key 都真的存在。
///
/// ## 为什么必须做这个（而不是只看编译过不过）
///
/// XAML 是**运行时**才解析的：一个拼错的 `StaticResource` key、
/// 一个引用了不存在颜色的 Brush、一本字典里写了重复的 x:Key ——
/// **编译期全部不会报错**，只有界面跑起来那一刻才炸。
///
/// 这种坑对 ui 代价特别大：他在页面里写 `{StaticResource WarnBrush}`，
/// 如果我的字典里其实叫 `WarningBrush`，他要等自己把六个页面都写完、
/// 跑起来、点进那一页才发现。
///
/// 所以这里在测试里把字典**真加载一遍**，并且断言 ui 会用的 key 全都在。
///
/// ## WPF 测试怎么跑起来
///
/// 需要一个 STA 线程 + Application 实例（有些资源类型要求有 Application.Current）。
/// xunit 默认是 MTA，所以这里自己起一个 STA 线程。
/// </summary>
public class ThemeResourceTests
{
    /// <summary>五本字典的相对路径（相对 App 项目根）。</summary>
    private static readonly string[] DictionaryFiles =
    [
        "Colors.xaml",
        "Typography.xaml",
        "Metrics.xaml",
        "Motion.xaml",
        "Controls.xaml",
    ];

    private static string ThemesDir
    {
        get
        {
            // 测试程序集在 tests/BuddyDeck.App.Presentation.Tests/bin/Debug/net8.0/
            // 源文件在 src/BuddyDeck.App/Themes/
            // 沿目录往上找到 _next，再拼过去。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "BuddyDeck.App", "Themes")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "BuddyDeck.App", "Themes");
        }
    }

    /// <summary>
    /// 加载一本字典。
    ///
    /// ## clr-namespace 怎么解析（这是实现里最容易踩的一处）
    ///
    /// 字典里有 <c>xmlns:bd="clr-namespace:BuddyDeck.App.Controls"</c> 这种
    /// 类型引用（自绘控件的 Style TargetType）。XamlReader.Load 解析
    /// clr-namespace 时默认只认**入口程序集**，而测试宿主的入口是
    /// testhost.exe —— 里面当然没有 QuotaBar，于是抛：
    ///
    ///     类型引用无法找到名为「{clr-namespace:BuddyDeck.App.Controls}QuotaBar」的类型
    ///
    /// 被链接进来的 QuotaBar 就在**本测试程序集**里。解法是把字典文本里的
    /// clr-namespace 引用补上程序集名：
    ///
    ///     clr-namespace:BuddyDeck.App.Controls;assembly=BuddyDeck.App.Presentation.Tests
    ///
    /// 这样解析器不用猜，直接去本程序集拿类型。
    ///
    /// ## 这不是在掩盖产品问题
    ///
    /// 真项目里 BuddyDeck.App 就是入口程序集，无后缀的 clr-namespace
    /// 天生解析得到 —— ui 在 App.xaml 里 merge 这五本字典不会遇到这个问题。
    /// 这里补程序集名纯粹是测试宿主的需要。
    /// </summary>
    private static ResourceDictionary LoadDictionary(string file)
    {
        var path = Path.Combine(ThemesDir, file);
        var text = File.ReadAllText(path);

        var testAssemblyName = typeof(ThemeResourceTests).Assembly.GetName().Name;

        // 只改**类型引用**（形如 TargetType="bd:QuotaBar" 里经 xmlns 展开后的部分），
        // 不能碰 xmlns:bd="clr-namespace:..." 那条声明本身 ——
        // 在声明上加 ;assembly= 会让 XmlnsDefinition 的展开逻辑炸掉
        // （AssemblyName 解析失败），字典反而完全加载不了。
        //
        // 正确做法：在 xmlns 声明里就地补程序集名。
        // 也就是把
        //     xmlns:bd="clr-namespace:BuddyDeck.App.Controls"
        // 改成
        //     xmlns:bd="clr-namespace:BuddyDeck.App.Controls;assembly=<本测试程序集>"
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"xmlns:([A-Za-z0-9_]+)=""clr-namespace:([A-Za-z0-9_.]+)""",
            $"xmlns:$1=\"clr-namespace:$2;assembly={testAssemblyName}\"");

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        using var stream = new MemoryStream(bytes);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>
    /// 在一个 STA 线程上跑一段需要 WPF 的代码。
    ///
    /// 为什么要 STA：WPF 的 <see cref="Application"/> 和大部分
    /// <c>DependencyObject</c> 都要求 STA 线程。xunit 默认跑在 MTA，
    /// 直接 new Application() 会抛 InvalidOperationException。
    /// </summary>
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

    /// <summary>
    /// 加载全部五本字典（按 App.xaml 里的顺序）到一个 ResourceDictionary。
    ///
    /// 顺序有讲究：Controls.xaml 里的 Style 会引 Colors/Typography/Metrics 的 key，
    /// 所以那几本必须先合进来。
    /// </summary>
    private static ResourceDictionary LoadAll()
    {
        var merged = new ResourceDictionary();

        foreach (var file in DictionaryFiles)
        {
            merged.MergedDictionaries.Add(LoadDictionary(file));
        }

        return merged;
    }

    // ========================================================================
    //  加载
    // ========================================================================

    [Fact]
    public void 五本字典都能被WPF解析()
    {
        RunSta(() =>
        {
            var merged = LoadAll();
            Assert.Equal(DictionaryFiles.Length, merged.MergedDictionaries.Count);
        });
    }

    [Theory]
    [InlineData("Colors.xaml")]
    [InlineData("Typography.xaml")]
    [InlineData("Metrics.xaml")]
    [InlineData("Motion.xaml")]
    [InlineData("Controls.xaml")]
    public void 单本字典能独立加载(string file)
    {
        RunSta(() =>
        {
            var dict = LoadDictionary(file);
            Assert.NotNull(dict);
            Assert.True(dict.Count > 0, $"{file} 里一个资源都没有");
        });
    }

    /// <summary>
    /// 五本字典能同时加载且**没有重复 key**（重复会在引用时行为不确定）。
    /// </summary>
    [Fact]
    public void 字典之间没有重复的key()
    {
        RunSta(() =>
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new List<string>();

            foreach (var file in DictionaryFiles)
            {
                var dict = LoadDictionary(file);

                foreach (var key in dict.Keys)
                {
                    var name = key?.ToString() ?? "";
                    if (name.Length == 0) continue;

                    if (seen.TryGetValue(name, out var other))
                    {
                        duplicates.Add($"{name}（{other} 和 {file}）");
                    }
                    else
                    {
                        seen[name] = file;
                    }
                }
            }

            Assert.Empty(duplicates);
        });
    }

    // ========================================================================
    //  ui 会用的 key 必须在
    //
    //  这些清单是 ui 在 App.xaml / 页面里真的要引的。少一个他就要自己猜。
    // ========================================================================

    [Fact]
    public void 颜色key全部存在()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            string[] keys =
            [
                // 基色
                "InkBrush", "SurfaceBrush", "RailBrush", "RailSubtleBrush",
                "HairlineBrush", "WindowBackgroundBrush",
                // 文字
                "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush",
                // 语义
                "AccentBrush", "OkBrush", "WarnBrush", "StopBrush",
                "FaultBrush", "IdleBrush", "CoolBrush",
                // 焦点
                "FocusBrush",
                // 竖条
                "OkRailBrush", "WarnRailBrush", "StopRailBrush",
                "FaultRailBrush", "IdleRailBrush", "CoolRailBrush",
                // 条槽与徽章底
                "BarTrackBrush",
                "OkBadgeBackgroundBrush", "WarnBadgeBackgroundBrush",
                "StopBadgeBackgroundBrush", "FaultBadgeBackgroundBrush",
                "IdleBadgeBackgroundBrush", "CoolBadgeBackgroundBrush",
            ];

            var missing = keys.Where(k => !merged.Contains(k)).ToList();
            Assert.Empty(missing);
        });
    }

    [Fact]
    public void 排版key全部存在()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            string[] keys =
            [
                "FontUi", "FontNumeric",
                "FontSizeMicro", "FontSizeSmall", "FontSizeBody",
                "FontSizeSection", "FontSizeTitle", "FontSizeBadge",
                "FontWeightNormal", "FontWeightEmphasis",
                "TextPageTitle", "TextSectionTitle", "TextBody",
                "TextSmall", "TextMicro", "TextNumber",
                "TextNumberStrong", "TextParagraph",
                // 复合样式（BasedOn 引 Typography 自己的样式，所以必须住在这里）
                "ListRowTitle", "ListRowMeta",
                "FieldLabel", "FieldValue", "FieldValueMono",
            ];

            var missing = keys.Where(k => !merged.Contains(k)).ToList();
            Assert.Empty(missing);
        });
    }

    /// <summary>
    /// <c>BasedOn</c> 只能吃 StaticResource（它不是 DependencyProperty），
    /// 所以引用了别的字典里样式的 Style 会**加载失败**。
    ///
    /// 这条测试防的就是这个：如果哪天有人把 ListRowTitle 挪回 Controls.xaml，
    /// 或者在 Controls.xaml 里写一个新的 BasedOn 跨字典样式，这里会红。
    /// </summary>
    [Fact]
    public void Controls里没有任何跨字典的BasedOn()
    {
        RunSta(() =>
        {
            var path = Path.Combine(ThemesDir, "Controls.xaml");
            var text = File.ReadAllText(path);

            // Controls.xaml 里出现的 BasedOn 只能指向本文件定义的样式。
            // 目前设计上它是一个都不该有（复合样式都在 Typography.xaml）。
            var matches = System.Text.RegularExpressions.Regex.Matches(
                text,
                @"BasedOn=""\{StaticResource ([A-Za-z0-9_]+)\}""");

            Assert.Empty(matches);
        });
    }

    [Fact]
    public void 间距圆角尺寸key全部存在()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            string[] keys =
            [
                "SpaceInline", "SpaceTight", "SpaceRow", "SpaceControl",
                "SpaceSection", "SpacePage",
                "RadiusBar", "RadiusRow", "RadiusPanel", "RadiusDialog",
                "StrokeHairline", "StrokeFocus",
                "SizeStatusRail", "SizeRowRail", "SizeTopBar",
                "SizeQuotaBarHeight", "SizeStatusDot", "SizeLabelColumn",
                "BorderHairline", "BorderLeftRail",
            ];

            var missing = keys.Where(k => !merged.Contains(k)).ToList();
            Assert.Empty(missing);
        });
    }

    [Fact]
    public void 动效key全部存在()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            string[] keys =
            [
                "MotionInstant", "MotionFeedback", "MotionExpand",
                "MotionEnter", "MotionCooldownMs",
            ];

            var missing = keys.Where(k => !merged.Contains(k)).ToList();
            Assert.Empty(missing);
        });
    }

    [Fact]
    public void 按钮和列表行样式存在()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            string[] keys =
            [
                "ListRow", "ListRowContent", "ListRowRail",
                "PrimaryButton", "QuietButton", "FieldInput",
                "SectionDivider",
            ];

            var missing = keys.Where(k => !merged.Contains(k)).ToList();
            Assert.Empty(missing);
        });
    }

    // ========================================================================
    //  跨字典引用完整性
    //
    //  Colors.xaml 里的 Brush 用 StaticResource 引同文件里的 Color ——
    //  文件名写错的话编译期不报，加载时才炸。这里验证类型正确。
    // ========================================================================

    [Fact]
    public void 基色是Brush而且值非空()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[] { "InkBrush", "SurfaceBrush", "RailBrush", "AccentBrush", "OkBrush", "StopBrush" })
            {
                var brush = merged[key];
                Assert.IsAssignableFrom<System.Windows.Media.SolidColorBrush>(brush);

                var scb = (System.Windows.Media.SolidColorBrush)brush;
                Assert.Equal(1.0, scb.Color.A / 255.0 * 255 / 255);   // 不透明
            }
        });
    }

    [Fact]
    public void 字号是Double()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[] { "FontSizeMicro", "FontSizeBody", "FontSizeTitle" })
            {
                Assert.IsType<double>(merged[key]);
            }
        });
    }

    [Fact]
    public void 圆角是CornerRadius()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[] { "RadiusBar", "RadiusRow", "RadiusPanel", "RadiusDialog" })
            {
                Assert.IsType<CornerRadius>(merged[key]);
            }
        });
    }

    [Fact]
    public void 动效时长是Duration()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[] { "MotionInstant", "MotionFeedback", "MotionExpand", "MotionEnter" })
            {
                Assert.IsType<Duration>(merged[key]);
            }
        });
    }

    /// <summary>
    /// 设计计划的硬要求：**圆角不是一刀切**。
    /// 如果四个圆角 key 有相同的值，就退化成了 SaaS 卡片套装（识别特征 4）。
    /// </summary>
    [Fact]
    public void 圆角按尺寸分级不是一刀切()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            var bar = ((CornerRadius)merged["RadiusBar"]).TopLeft;
            var row = ((CornerRadius)merged["RadiusRow"]).TopLeft;
            var panel = ((CornerRadius)merged["RadiusPanel"]).TopLeft;
            var dialog = ((CornerRadius)merged["RadiusDialog"]).TopLeft;

            Assert.True(bar < row, "RadiusBar 应小于 RadiusRow");
            Assert.True(row < panel, "RadiusRow 应小于 RadiusPanel");
            Assert.True(panel < dialog, "RadiusPanel 应小于 RadiusDialog");
        });
    }

    /// <summary>
    /// 「没有阴影」不是口头约定 —— 这里断言字典里确实没有任何 DropShadowEffect。
    /// </summary>
    [Fact]
    public void 字典里没有任何阴影效果()
    {
        RunSta(() =>
        {
            foreach (var file in DictionaryFiles)
            {
                var text = File.ReadAllText(Path.Combine(ThemesDir, file));

                Assert.DoesNotContain("DropShadowEffect", text, StringComparison.Ordinal);
                Assert.DoesNotContain("DropShadow", text, StringComparison.Ordinal);
            }
        });
    }

    // ========================================================================
    //  数量型 key 的类型正确性
    //
    //  这一组来自 ui 实际踩到的运行时故障：他把 {StaticResource StrokeHairline}
    //  用在了 BorderThickness 上。StrokeHairline 是 sys:Double(1)，
    //  而 BorderThickness 要的是 Thickness ——
    //  **编译期完全通过，运行时报 ArgumentException：对 1 个参数，"BorderThickness" 是无效值。**
    //  6 个页面共 19 处。
    //
    //  所以这里把「哪个 key 是什么类型」写成断言，让混用变成测试失败而不是运行时崩溃。
    // ========================================================================

    [Fact]
    public void 描边粗细两组key类型不同()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            // sys:Double —— 给 Rectangle.StrokeThickness / Pen 用
            Assert.IsType<double>(merged["StrokeHairline"]);
            Assert.IsType<double>(merged["StrokeFocus"]);

            // Thickness —— 给 Border.BorderThickness 用
            Assert.IsType<Thickness>(merged["BorderHairline"]);
            Assert.IsType<Thickness>(merged["BorderLeftRail"]);
        });
    }

    [Fact]
    public void 两组描边key不能互相替代()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            // 这两组的值都是「1」，所以**特别容易混**：
            // 长得一样、数值一样，只有类型不同。
            // 这条断言就是防「随手拿错了那一个」。
            Assert.IsNotType<Thickness>(merged["StrokeHairline"]);
            Assert.IsNotType<double>(merged["BorderHairline"]);
        });
    }

    [Fact]
    public void 尺寸key都是Double()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[]
                     {
                         "SizeStatusRail", "SizeRowRail", "SizeTopBar",
                         "SizeQuotaBarHeight", "SizeStatusDot",
                         "SizeLabelColumn", "SizeMaxReadingWidth",
                     })
            {
                Assert.IsType<double>(merged[key]);
            }
        });
    }

    [Fact]
    public void 间距key都是Thickness()
    {
        RunSta(() =>
        {
            var merged = LoadAll();

            foreach (var key in new[]
                     {
                         "SpaceInline", "SpaceTight", "SpaceRow",
                         "SpaceControl", "SpaceSection", "SpacePage",
                     })
            {
                Assert.IsType<Thickness>(merged[key]);
            }
        });
    }

    /// <summary>
    /// 反相语义必须是「两个行为不同的实例」，不能只是两个名字。
    ///
    /// 这条防的是已发生过的故障：App.xaml 里声明了
    /// <c>x:Key="InverseBoolToVisibility"</c> 却**没设 <c>Invert="True"</c>**，
    /// 于是那个 key 名不副实（行为和正相一样），
    /// 导致仪表盘「离线时显示原因」渲染成空白。
    ///
    /// 转换器实例由 ui 在 App.xaml 里声明（不在资源字典里），
    /// 所以这里验证「<c>Invert</c> 属性确实能改变行为」——
    /// 只要属性有效，XAML 那句 <c>Invert="True"</c> 就够；
    /// 漏写那句话则由页面渲染暴露（ui 已经实测过六个页面）。
    /// </summary>
    [Fact]
    public void 反相转换器靠属性生效而不是靠key名()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        var forward = new BuddyDeck.App.Converters.BoolToVisibilityConverter();
        var inverse = new BuddyDeck.App.Converters.BoolToVisibilityConverter { Invert = true };

        // false → 正相折叠、反相显示（这就是 Inverse* 那个 key 该有的行为）
        Assert.Equal(Visibility.Collapsed, forward.Convert(false, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, inverse.Convert(false, typeof(Visibility), null, culture));

        // 字符串版同样
        var stringForward = new BuddyDeck.App.Converters.StringToVisibilityConverter();
        var stringInverse = new BuddyDeck.App.Converters.StringToVisibilityConverter { Invert = true };

        Assert.Equal(Visibility.Collapsed, stringForward.Convert(null, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, stringInverse.Convert(null, typeof(Visibility), null, culture));

        // 集合版同样
        var collForward = new BuddyDeck.App.Converters.EmptyCollectionToVisibilityConverter();
        var collInverse = new BuddyDeck.App.Converters.EmptyCollectionToVisibilityConverter { Invert = true };

        var populated = new List<int> { 1 };

        Assert.Equal(Visibility.Collapsed, collForward.Convert(populated, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, collInverse.Convert(populated, typeof(Visibility), null, culture));
    }
}
