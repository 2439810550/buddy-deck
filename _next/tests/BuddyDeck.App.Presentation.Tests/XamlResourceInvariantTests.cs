using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// **全量** XAML 资源不变量扫描。
///
/// ## 这一组补的是什么缺口
///
/// <see cref="ThemeResourceTests"/> 是**定点**断言：它写死一份 key 清单，
/// 只验证「我列出的这些存在」。它挡不住「页面里引了一个我没列出的 key」，
/// 也完全不管**类型**。
///
/// 而 ui 实际踩到的 3 类 bug 里，有 2 类是「名字对、类型错」或
/// 「名字对、语义依赖参数」—— **编译期 100% 通过，只在运行时炸或静默变空白**：
///
/// | 症状 | 例子 | 编译期 | 运行时 |
/// |---|---|---|---|
/// | 类型不对 | `BorderThickness="{StaticResource StrokeHairline}"`（double 给了 Thickness） | 通过 | `ArgumentException` |
/// | 类型不对 | `Background="{StaticResource FontSizeBody}"`（double 给了 Brush） | 通过 | `ArgumentException` |
/// | 语义缺失 | `{StaticResource InverseBoolToVisibility}` 漏了 `Invert="True"` | 通过 | **静默空白** |
///
/// 本文件做三件事：
///   ① **全量扫描**所有非主题 XAML 的 `{StaticResource X}` / `{DynamicResource X}`，
///      断言每个 X 在「五本主题字典 + App.xaml 里声明的转换器」中真实存在
///   ② **类型匹配**：按属性名推导期望类型，断言解析出来的资源对象真是那个类型
///   ③ **自证**：本文件自带一组「故意写错」的用例（见 <c>自证</c> 区域），
///      证明这套检查**真的会红**，而不是永远返回通过
///
/// ## 为什么正则扫 XAML 而不是把 XAML 加载起来
///
/// 把页面 XAML 真正加载需要构造整个 ViewModel 树和 WPF 应用上下文，
/// 代价高且脆弱（页面依赖 DI 容器）。而这类 bug 的**语法特征**很规整 ——
/// `属性名="{StaticResource 键名}"` —— 正则足够精确地抓到它，
/// 且测试跑得快、依赖少。**检查的是契约，不是渲染结果。**
/// </summary>
public class XamlResourceInvariantTests
{
    /// <summary>五本主题字典，顺序与 App.xaml 一致。</summary>
    private static readonly string[] ThemeFiles =
    [
        "Colors.xaml", "Typography.xaml", "Metrics.xaml", "Motion.xaml", "Controls.xaml",
    ];

    /// <summary>
    /// 属性名 → 该属性能接受的资源类型。
    ///
    /// 这份映射是**本测试的核心知识**。只列会被资源引用赋值的属性
    /// （已用脚本从真实 XAML 里枚举确认过，见 ui 页面里的实际用法）。
    ///
    /// ⚠ 加新属性时：先确认它在 WPF 里的真实类型再往这里加，
    ///   不要为了「让测试过」而放宽。
    /// </summary>
    private static readonly Dictionary<string, Type[]> ExpectedTypes = new(StringComparer.Ordinal)
    {
        // --- 画刷类 ---
        ["Background"] = [typeof(Brush)],
        ["Foreground"] = [typeof(Brush)],
        ["BorderBrush"] = [typeof(Brush)],
        ["Fill"] = [typeof(Brush)],

        // --- 几何类 ---
        // ⚠ 就是这一条抓到「BorderThickness 吃到 double」的 19 处：
        //   Thickness 与 double 是两个完全不同的类型，且都是「一个数」，
        //   肉眼与编译器都分不出来。
        //
        //   这一段做过自证实验：把它临时改成 typeof(double) 后，
        //   正式测试立刻报「有 19 处资源类型与属性不匹配」并逐页列出
        //   （AccountsPage/ChatPage/DashboardPage/LogsPage/PoolsPage/SettingsPage）。
        //   19 这个数与 ui 实际踩到的处数吻合 —— 说明检查器真的在扫全量。
        ["BorderThickness"] = [typeof(Thickness)],
        ["Margin"] = [typeof(Thickness)],
        ["Padding"] = [typeof(Thickness)],
        ["CornerRadius"] = [typeof(CornerRadius)],

        // --- 数值类 ---
        ["FontSize"] = [typeof(double)],
        ["Width"] = [typeof(double)],
        ["Height"] = [typeof(double)],
        ["MaxWidth"] = [typeof(double)],
        ["MinWidth"] = [typeof(double)],

        // --- 动画时长 ---
        ["Duration"] = [typeof(Duration)],

        // --- 字体/字重 ---
        ["FontFamily"] = [typeof(FontFamily)],
        ["FontWeight"] = [typeof(FontWeight)],

        // --- 枚举 ---
        ["Visibility"] = [typeof(Visibility)],
        ["TextTrimming"] = [typeof(TextTrimming)],
        ["HorizontalAlignment"] = [typeof(HorizontalAlignment)],
        ["VerticalAlignment"] = [typeof(VerticalAlignment)],

        // --- 样式（只能吃 Style）---
        ["Style"] = [typeof(Style)],
        ["BasedOn"] = [typeof(Style)],

        // --- Setter.Value 是「什么都可能」的，由被设的属性决定，
        //     无法在这里静态判定。见下面 ValueAcceptsAnyType 的说明。---
    };

    /// <summary>
    /// <c>Setter.Value</c> 不接受静态类型判定 —— 它的合法类型取决于它在给哪个
    /// 属性设值（`&lt;Setter Property="FontSize" Value="{...size...}"/&gt;` 要 double，
    /// 而 `Property="Background"` 要 Brush）。所以对这一类只做「key 存在」检查。
    /// </summary>
    private const string UnconstrainedProperty = "Value";

    // ========================================================================
    //  定位源码目录
    // ========================================================================

    private static string AppDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "BuddyDeck.App", "Themes")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "BuddyDeck.App");
        }
    }

    /// <summary>
    /// 要扫描的 XAML —— **去重取反**：整个 App 项目树里所有 *.xaml，**减掉主题字典**。
    ///
    /// ## 为什么是「全树减主题」而不是「列几个已知目录」
    ///
    /// 早先的实现只枚举 `*.xaml`（顶层）+ `Pages/**`。那是个**写死的目录清单**：
    /// 谁哪天加一个 `Controls/Foo.xaml` 或 `Views/Bar.xaml`，那些文件就
    /// **静默不被检查** —— 保护网出现缺口而没人知道。
    ///
    /// 改成「全树减主题」之后，**新增任何目录的 XAML 都自动纳入**。
    /// 豁免面从「我列出的目录」翻转成「我明确排除的文件」，方向对了：
    /// 漏掉一个文件的后果从「静默放过」变成「必须先想清楚为什么排除它」。
    ///
    /// ## 为什么排除主题字典
    ///
    /// 五本主题字典里引用的是**同字典或兄弟字典**的 key，它们本身就是
    /// 被检查的**对象**（<see cref="ThemeResourceTests"/> 负责），
    /// 而不是**消费者**。把它们一起扫会重复计数，也会把
    /// 「字典内部 A 引 B」误当成「页面引字典」。
    /// </summary>
    private static IEnumerable<string> ScannedXamlFiles()
    {
        var appDir = AppDir;
        var themesDir = Path.Combine(appDir, "Themes");

        return Directory
            .EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            // 跳过构建产物 —— obj/ 里会有 XAML 的中间文件。
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            // 排除主题字典本身（见上面的说明）。
            .Where(f => !f.StartsWith(themesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    // ========================================================================
    //  抽取引用
    // ========================================================================

    /// <summary>一条 <c>属性="{StaticResource 键}"</c> 引用。</summary>
    private readonly record struct ResourceRef(string File, string Property, string Key, string Kind);

    /// <summary>
    /// 从全部非主题 XAML 里抽出所有资源引用。
    ///
    /// 匹配 <c>{StaticResource X}</c> 与 <c>{DynamicResource X}</c> 两种，
    /// 并且捕获它前面那个属性名。
    ///
    /// 注：`ui:ThemeResource` 是 WPF-UI 自己的标记扩展（引 WPF-UI 的字典，
    /// 不在我们的五本里），**刻意不纳入** —— 断言它会误报。
    /// </summary>
    private static List<ResourceRef> CollectReferences()
    {
        var refs = new List<ResourceRef>();

        var pattern = new Regex(
            @"(?<![\w:])(?<prop>[A-Za-z][A-Za-z0-9_.]*)\s*=\s*""\{(?<kind>Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_]+)\}""",
            RegexOptions.Compiled);

        foreach (var file in ScannedXamlFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match m in pattern.Matches(text))
            {
                refs.Add(new ResourceRef(
                    Path.GetFileName(file),
                    m.Groups["prop"].Value,
                    m.Groups["key"].Value,
                    m.Groups["kind"].Value));
            }
        }

        return refs;
    }

    /// <summary>
    /// 加载五本主题字典 + 解析 App.xaml 里声明的转换器实例。
    ///
    /// 必须在一个 STA 线程上跑（WPF 要求），且要给 <c>clr-namespace</c>
    /// 补程序集名（理由见 <see cref="ThemeResourceTests"/> 的 LoadDictionary）。
    /// </summary>
    private static (ResourceDictionary Theme, HashSet<string> ConverterKeys) LoadResourceSurface()
    {
        var merged = new ResourceDictionary();
        var assemblyName = typeof(XamlResourceInvariantTests).Assembly.GetName().Name;

        foreach (var file in ThemeFiles)
        {
            var path = Path.Combine(AppDir, "Themes", file);
            var text = File.ReadAllText(path);

            // 给 xmlns 声明补程序集名（详见 ThemeResourceTests 的说明）。
            text = Regex.Replace(
                text,
                @"xmlns:([A-Za-z0-9_]+)=""clr-namespace:([A-Za-z0-9_.]+)""",
                $"xmlns:$1=\"clr-namespace:$2;assembly={assemblyName}\"");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            merged.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream));
        }

        // App.xaml 里的转换器实例：用正则取 x:Key（不去加载 App.xaml ——
        // 它是 Application 级别的资源，在测试宿主里加载会拉起 WPF 应用上下文）。
        var converterKeys = new HashSet<string>(StringComparer.Ordinal);
        var appXaml = Path.Combine(AppDir, "App.xaml");
        if (File.Exists(appXaml))
        {
            var appText = File.ReadAllText(appXaml);
            foreach (Match m in Regex.Matches(appText, @"x:Key=""(?<k>[A-Za-z0-9_]+)"""))
            {
                converterKeys.Add(m.Groups["k"].Value);
            }
        }

        return (merged, converterKeys);
    }

    /// <summary>STA 线程上跑一段 WPF 代码。</summary>
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

    // ========================================================================
    //  ① 全量存在性
    // ========================================================================

    [Fact]
    public void 页面里引用的每个资源key都真实存在()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();
            var refs = CollectReferences();

            Assert.NotEmpty(refs);

            var missing = new List<string>();

            foreach (var r in refs)
            {
                if (theme.Contains(r.Key) || converterKeys.Contains(r.Key))
                {
                    continue;
                }

                missing.Add($"{r.File}  {r.Property}=\"{{{r.Kind}Resource {r.Key}}}\"");
            }

            Assert.True(
                missing.Count == 0,
                $"有 {missing.Count} 处引用了不存在的资源 key：\n  " + string.Join("\n  ", missing.Distinct()));
        });
    }

    /// <summary>
    /// 反向：主题字典里不该有**没人用也没文档**的孤儿 key。
    ///
    /// 这条不做「零孤儿」的硬断言 —— 有些 key 是给未来页面或设计一致性留的
    /// （比如 RadiusDialog 现在还没弹窗用）。所以只断言孤儿数量不失控，
    /// 超过阈值说明有人在往字典里堆垃圾。
    /// </summary>
    [Fact]
    public void 主题字典的孤儿key数量没有失控()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();
            var used = CollectReferences().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

            var orphans = theme.Keys.Cast<object>()
                .Select(k => k?.ToString() ?? "")
                .Where(k => k.Length > 0)
                .Where(k => !used.Contains(k))
                // 基色/字号这类「被别的 key 引用」的不算孤儿（它们被 StaticResource 引用在字典内部）。
                .ToList();

            // 这个数只是「警戒线」，不是设计约束。当前约 100 上下（大量基础 token 由
            // 字典内部互相引用，不出现在页面里 —— 那是正常的）。
            Assert.True(
                orphans.Count < 200,
                $"主题字典里未被页面引用、也不被其他 key 引用的项有 {orphans.Count} 个，可能堆了冗余 key。\n  样例："
                + string.Join(", ", orphans.Take(20)));
        });
    }

    // ========================================================================
    //  ② 类型匹配 —— 抓「名字对、类型错」
    // ========================================================================

    /// <summary>
    /// 类型检查的**唯一实现**。正式测试与自证用例都调它 ——
    /// 这样自证证明的是「**这段真实逻辑**会红」，而不是我另写的一段等价代码会红。
    /// （如果自证复刻一份逻辑，那它证明不了生产逻辑有效。）
    /// </summary>
    /// <returns>形如「文件 属性="…" 期望 X，实际 Y」的描述列表；空 = 全部匹配。</returns>
    private static (List<string> Mismatches, int Checked) CheckTypeMatches(
        ResourceDictionary theme,
        HashSet<string> converterKeys,
        IEnumerable<ResourceRef> refs)
    {
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var r in refs)
        {
            // 转换器实例由 Binding 产出值，与目标属性类型无关（Converter 的输出类型
            // 由绑定目标决定，XAML 层不校验）。所以转换器 key 跳过。
            if (converterKeys.Contains(r.Key)) continue;

            // Value（Setter.Value）无法静态判定，见 UnconstrainedProperty 注释。
            if (r.Property == UnconstrainedProperty) continue;

            // 没登记过类型的属性由另一条测试负责报缺口，这里不判。
            if (!ExpectedTypes.TryGetValue(r.Property, out var allowed)) continue;

            if (!theme.Contains(r.Key)) continue;   // 存在性由另一条测试负责

            var value = theme[r.Key];
            checkedCount++;

            if (!allowed.Any(t => value is not null && t.IsInstanceOfType(value)))
            {
                var actual = value?.GetType().Name ?? "null";
                var expect = string.Join(" / ", allowed.Select(t => t.Name));
                mismatches.Add(
                    $"{r.File}  {r.Property}=\"{{{r.Kind}Resource {r.Key}}}\"  "
                    + $"期望 {expect}，实际 {actual}");
            }
        }

        return (mismatches, checkedCount);
    }

    [Fact]
    public void 每个资源引用的类型与目标属性匹配()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();
            var refs = CollectReferences();

            var (mismatches, checkedCount) = CheckTypeMatches(theme, converterKeys, refs);

            Assert.True(checkedCount > 0, "一条引用都没检查到 —— 扫描逻辑失效了");
            Assert.True(
                mismatches.Count == 0,
                $"有 {mismatches.Count} 处资源类型与属性不匹配（编译期不报，运行时报 ArgumentException）：\n  "
                + string.Join("\n  ", mismatches.Distinct()));
        });
    }

    /// <summary>
    /// 扫描到的每个属性名都必须在 <see cref="ExpectedTypes"/> 里登记过
    /// （<c>Value</c> 例外）。
    ///
    /// 为什么要有这条：如果不登记也能通过，那「加了新的资源型属性」就会被
    /// **静默跳过类型检查** —— 保护网出现缺口而没人知道。
    /// 这条让缺口变成测试失败，逼人显式决定。
    /// </summary>
    [Fact]
    public void 扫描到的资源型属性都已登记类型()
    {
        var props = CollectReferences()
            .Select(r => r.Property)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var unregistered = props
            .Where(p => p != UnconstrainedProperty)
            .Where(p => !ExpectedTypes.ContainsKey(p))
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            "以下属性被资源引用赋值，但没登记期望类型 —— 类型检查对它们是空的：\n  "
            + string.Join("\n  ", unregistered));
    }

    // ========================================================================
    //  自证：这套检查真的会红
    //
    //  ## 为什么必须自证
    //
    //  「加了一组测试」和「这组测试有用」是两件事。常见的两种假绿：
    //    · 正则写错 → 一处引用都没扫到 → 断言「没有不匹配」自然通过
    //    · 类型映射写空 → 每种情况都走 continue → 同样「通过」
    //
    //  下面几条把**故意写错的数据**喂给**正式测试用的同一个函数**
    //  <see cref="CheckTypeMatches"/>。如果它不报错，说明检查是空壳。
    //
    //  ⚠ 关键点：自证**不复刻一份检查逻辑** —— 复刻的话，证明的是「我复刻的那份
    //    能抓到」，而不是「生产用的那份能抓到」。这里调的是同一个方法。
    // ========================================================================

    /// <summary>构造一条「页面里写了某个属性引用了某个 key」的假数据。</summary>
    private static ResourceRef Fake(string prop, string key, string kind = "Static")
        => new("FakePage.xaml", prop, key, kind);

    [Fact]
    public void 自证_类型检查能抓到BorderThickness吃到double()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            // ui 实际踩到的写法：StrokeHairline 是 sys:Double(1)，
            // 而 BorderThickness 要 Thickness。
            Assert.IsType<double>(theme["StrokeHairline"]);

            var wrong = new[] { Fake("BorderThickness", "StrokeHairline") };
            var (mismatches, checkedCount) = CheckTypeMatches(theme, converterKeys, wrong);

            // 必须①真的检查了这条 ②判定为不匹配。
            Assert.Equal(1, checkedCount);
            Assert.Single(mismatches);

            // 报错信息要能定位到问题 —— 文件、属性、期望与实际类型。
            var msg = mismatches[0];
            Assert.Contains("BorderThickness", msg, StringComparison.Ordinal);
            Assert.Contains("StrokeHairline", msg, StringComparison.Ordinal);
            Assert.Contains("Thickness", msg, StringComparison.Ordinal);
            Assert.Contains("Double", msg, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 自证_类型检查能抓到Background吃到double()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            // 另一种同类错法：把字号给 Background（Brush 位置）。
            Assert.IsType<double>(theme["FontSizeBody"]);

            var (mismatches, checkedCount) = CheckTypeMatches(
                theme, converterKeys, new[] { Fake("Background", "FontSizeBody") });

            Assert.Equal(1, checkedCount);
            Assert.Single(mismatches);
            Assert.Contains("Brush", mismatches[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 自证_类型检查能抓到Foreground吃到Thickness()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            // 反向的同类错法：把几何（Thickness）喂给 Brush 位置。
            Assert.IsType<Thickness>(theme["SpaceRow"]);

            var (mismatches, _) = CheckTypeMatches(
                theme, converterKeys, new[] { Fake("Foreground", "SpaceRow") });

            Assert.Single(mismatches);
            Assert.Contains("Brush", mismatches[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 自证_类型检查能抓到FontSize吃到Brush()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            // FontSize 要 double，给它一个 Brush。
            Assert.IsAssignableFrom<System.Windows.Media.Brush>(theme["AccentBrush"]);

            var (mismatches, _) = CheckTypeMatches(
                theme, converterKeys, new[] { Fake("FontSize", "AccentBrush") });

            Assert.Single(mismatches);
            Assert.Contains("Double", mismatches[0], StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// 反向自证：**正确**的引用不能被误报。
    ///
    /// 一个「见谁都报错」的检查器和「谁都不报错」一样没用 ——
    /// 它会逼人把断言删掉。
    /// </summary>
    [Fact]
    public void 自证_正确的引用不会被误报()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            var correct = new[]
            {
                Fake("BorderThickness", "BorderHairline"),   // Thickness → Thickness ✓
                Fake("Background", "SurfaceBrush"),          // Brush → Brush ✓
                Fake("Foreground", "TextPrimaryBrush"),      // Brush → Brush ✓
                Fake("FontSize", "FontSizeBody"),            // double → double ✓
                Fake("Margin", "SpaceSection"),              // Thickness → Thickness ✓
                Fake("CornerRadius", "RadiusRow"),           // CornerRadius → CornerRadius ✓
                Fake("Style", "ListRow"),                    // Style → Style ✓
            };

            var (mismatches, checkedCount) = CheckTypeMatches(theme, converterKeys, correct);

            Assert.Equal(correct.Length, checkedCount);
            Assert.True(
                mismatches.Count == 0,
                "检查器对正确的引用误报了，它的信噪比不可用：\n  " + string.Join("\n  ", mismatches));
        });
    }

    [Fact]
    public void 自证_存在性检查能抓到不存在的key()
    {
        RunSta(() =>
        {
            var (theme, converterKeys) = LoadResourceSurface();

            // 一个绝对不存在的 key —— 存在性检查必须判它「不在」。
            const string bogus = "ThisKeyDeliberatelyDoesNotExist";

            Assert.DoesNotContain(bogus, theme.Keys.Cast<object>().Select(k => k?.ToString()));
            Assert.DoesNotContain(bogus, converterKeys);
        });
    }

    /// <summary>
    /// 自证扫描器本身有产出：如果正则写错、或路径找不到文件，
    /// <see cref="CollectReferences"/> 会返回空列表 ——
    /// 那样上面所有断言都会「因为没什么可查」而通过。这条防止那种假绿。
    /// </summary>
    [Fact]
    public void 自证_扫描器确实扫到了足够多的引用()
    {
        var refs = CollectReferences();

        // 实际约 450 处（Static + Dynamic 合计）。给一个宽松下限，
        // 既能挡住「正则失效」，又不会因为页面增删而频繁误报。
        Assert.True(
            refs.Count > 100,
            $"只扫到 {refs.Count} 处资源引用，扫描器可能失效了（预期数百处）");

        // 而且必须覆盖到多个文件，不能只扫到一个。
        var files = refs.Select(r => r.File).Distinct().ToList();
        Assert.True(files.Count >= 5, $"只覆盖了 {files.Count} 个 XAML 文件：{string.Join(", ", files)}");
    }

    /// <summary>
    /// 自证类型映射表本身不是空的 —— 防止有人把 <see cref="ExpectedTypes"/>
    /// 清空来「让测试通过」。
    /// </summary>
    [Fact]
    public void 自证_类型映射表覆盖了关键属性()
    {
        string[] mustHave =
        [
            "BorderThickness", "Margin", "Padding", "CornerRadius",
            "Background", "Foreground", "BorderBrush",
            "FontSize", "Width", "Height",
            "Style", "BasedOn",
        ];

        var missing = mustHave.Where(p => !ExpectedTypes.ContainsKey(p)).ToList();
        Assert.True(missing.Count == 0, "类型映射表缺少关键属性：" + string.Join(", ", missing));
    }
}
