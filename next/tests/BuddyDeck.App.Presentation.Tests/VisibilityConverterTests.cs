using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using BuddyDeck.App.Converters;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// <see cref="BoolToVisibilityConverter"/> / <see cref="StringToVisibilityConverter"/> /
/// <see cref="EmptyCollectionToVisibilityConverter"/> 的测试。
///
/// ## 这一组是「回归测试」，不是「新功能测试」
///
/// 三个转换器是照 ui 实际踩到的坑重写的。每一条 <c>坑</c> 命名的测试
/// 都对应一次真实的运行时故障，**锁住的是未来的回归**：
///
/// 坑 1  `InverseBoolToVisibility` 这个 key 行为不反相 —— 仪表盘
///       「离线时显示原因」那一块渲染成空白（因为 ConverterParameter
///       没传，实例行为和正相一模一样）。
/// 坑 2  `EmptyCollectionToVisibility` 绑 <c>Lines.Count</c>（int）不工作
///       —— 日志页空态不显示。
///
/// 修法是给转换器加 <c>Invert</c> 属性（由 XAML 在声明实例时设定），
/// 让 **key 名和实例行为一一对应**，而不是靠绑定时才传的参数。
/// </summary>
public class VisibilityConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ========================================================================
    //  坑 1：反相必须由实例状态决定，不能只靠 ConverterParameter
    // ========================================================================

    [Fact]
    public void 坑1_反相实例的行为确实反相()
    {
        // 这是那条真实故障的直接回归测试。
        var forward = new BoolToVisibilityConverter();
        var inverse = new BoolToVisibilityConverter { Invert = true };

        // false 时：正相隐藏，反相显示。
        Assert.Equal(Visibility.Collapsed, forward.Convert(false, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Visible, inverse.Convert(false, typeof(Visibility), null, Invariant));

        // true 时反过来。
        Assert.Equal(Visibility.Visible, forward.Convert(true, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, inverse.Convert(true, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 坑1_反相实例不依赖ConverterParameter()
    {
        // 故障的根因就是「不传参数时反相实例不反相」。
        // 这条断言把「Invert 属性足以反相」钉死。
        var inverse = new BoolToVisibilityConverter { Invert = true };

        var withNullParam = inverse.Convert(false, typeof(Visibility), null, Invariant);
        var withEmptyParam = inverse.Convert(false, typeof(Visibility), string.Empty, Invariant);

        Assert.Equal(Visibility.Visible, withNullParam);
        Assert.Equal(Visibility.Visible, withEmptyParam);
    }

    [Fact]
    public void 参数反相仍然可用()
    {
        // 参数级反相是留给「同一个 key 临时反一下」的少数场景，别删。
        var forward = new BoolToVisibilityConverter();

        Assert.Equal(Visibility.Visible, forward.Convert(false, typeof(Visibility), "invert", Invariant));
        Assert.Equal(Visibility.Visible, forward.Convert(false, typeof(Visibility), "!", Invariant));
        Assert.Equal(Visibility.Visible, forward.Convert(false, typeof(Visibility), "false", Invariant));
    }

    [Fact]
    public void 属性和参数同时反相等于不反相()
    {
        // 两次取反。虽然没人会故意这么写，但要保证不出现意外行为。
        var inverse = new BoolToVisibilityConverter { Invert = true };

        Assert.Equal(Visibility.Collapsed, inverse.Convert(false, typeof(Visibility), "invert", Invariant));
    }

    // ========================================================================
    //  BoolToVisibility 的基础行为与边界
    // ========================================================================

    [Fact]
    public void 非布尔值按假处理不崩()
    {
        var c = new BoolToVisibilityConverter();

        // null / 字符串 / int 都不是「真」，折叠而不是抛异常。
        Assert.Equal(Visibility.Collapsed, c.Convert(null, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, c.Convert("true", typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, c.Convert(1, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 反相转换支持ConvertBack()
    {
        var forward = new BoolToVisibilityConverter();
        var inverse = new BoolToVisibilityConverter { Invert = true };

        Assert.True((bool)forward.ConvertBack(Visibility.Visible, typeof(bool), null, Invariant));
        Assert.False((bool)forward.ConvertBack(Visibility.Collapsed, typeof(bool), null, Invariant));

        Assert.False((bool)inverse.ConvertBack(Visibility.Visible, typeof(bool), null, Invariant));
        Assert.True((bool)inverse.ConvertBack(Visibility.Collapsed, typeof(bool), null, Invariant));

        // Hidden 不是 Visible，一律当「不可见」。
        Assert.False((bool)forward.ConvertBack(Visibility.Hidden, typeof(bool), null, Invariant));
    }

    // ========================================================================
    //  StringToVisibility
    // ========================================================================

    [Theory]
    [InlineData("有内容", Visibility.Visible)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("   ", Visibility.Collapsed)]   // 空白串按空处理
    [InlineData("\t\n", Visibility.Collapsed)]
    public void 字符串非空才显示(string value, Visibility expected)
    {
        var c = new StringToVisibilityConverter();
        Assert.Equal(expected, c.Convert(value, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 字符串null当空处理()
    {
        var c = new StringToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, c.Convert(null, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 字符串反相实例行为反相()
    {
        var inverse = new StringToVisibilityConverter { Invert = true };

        Assert.Equal(Visibility.Visible, inverse.Convert(null, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Visible, inverse.Convert("  ", typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, inverse.Convert("有内容", typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 超长字符串仍然算有内容()
    {
        // 不影响可见性判断；只是确认没有长度上限之类的意外行为。
        var c = new StringToVisibilityConverter();
        var huge = new string('x', 100_000);

        Assert.Equal(Visibility.Visible, c.Convert(huge, typeof(Visibility), null, Invariant));
    }

    // ========================================================================
    //  坑 2：集合 —— 必须绑集合本身，不能绑 Count
    // ========================================================================

    [Fact]
    public void 坑2_绑集合本身才工作()
    {
        var c = new EmptyCollectionToVisibilityConverter();

        var populated = new ObservableCollection<string> { "a" };
        var empty = new ObservableCollection<string>();

        // 空集合 → 显示空态
        Assert.Equal(Visibility.Visible, c.Convert(empty, typeof(Visibility), null, Invariant));
        // 非空集合 → 隐藏空态
        Assert.Equal(Visibility.Collapsed, c.Convert(populated, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 坑2_绑Count时行为是静默判为非空()
    {
        // ⚠ 这条测试**记录的是一个陷阱**，不是期望功能。行为已实测确认。
        //
        // 绑 {Binding Lines.Count} 传进来的是 int。int 不实现 ICollection，
        // 于是模式 `is ICollection { Count: 0 } or null` **两个分支都不匹配**，
        // isEmpty 保持默认的 false → 结果 Collapsed（判为非空）。
        //
        // 后果正是 ui 遇到的：**日志有 0 行的时候，空态不显示**（被折叠了）。
        // 注意它不会抛异常、也不会日志告警，就是静静地不显示 —— 这是最坏的一类 bug。
        //
        // 正确做法：绑集合本身（{Binding Lines}），让它命中 ICollection 分支。
        var c = new EmptyCollectionToVisibilityConverter();

        Assert.Equal(Visibility.Collapsed, c.Convert(5, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, c.Convert(0, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 裸IEnumerable同样被静默判为非空()
    {
        // 同上：LINQ 查询结果是裸 IEnumerable<T>（WhereArrayIterator 之类），
        // 不实现 ICollection → 判为非空 → 空态不显示。
        // 所以 ViewModel 里要过滤，必须先物化成 List / ObservableCollection 再绑。
        var c = new EmptyCollectionToVisibilityConverter();

        var query = new List<int> { 1, 2, 3 }.Where(x => x > 0);
        Assert.Equal(Visibility.Collapsed, c.Convert(query, typeof(Visibility), null, Invariant));

        // 空结果集的查询也不认 —— 这才是真正危险的那一种：
        // 一条记录都没有，界面却因为没有空态而看起来像「还在加载」。
        var emptyQuery = new List<int>().Where(x => x > 0);
        Assert.Equal(Visibility.Collapsed, c.Convert(emptyQuery, typeof(Visibility), null, Invariant));

        // 物化之后才对。
        var materialized = query.ToList();
        Assert.Equal(Visibility.Collapsed, c.Convert(materialized, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Visible, c.Convert(new List<int>().Where(x => x > 0).ToList(), typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 集合null当空处理()
    {
        var c = new EmptyCollectionToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert(null, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 集合反相实例行为反相()
    {
        var inverse = new EmptyCollectionToVisibilityConverter { Invert = true };

        var populated = new List<string> { "a" };
        var empty = new List<string>();

        Assert.Equal(Visibility.Collapsed, inverse.Convert(empty, typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Visible, inverse.Convert(populated, typeof(Visibility), null, Invariant));
    }

    [Fact]
    public void 数组和字典都认()
    {
        var c = new EmptyCollectionToVisibilityConverter();

        Assert.Equal(Visibility.Visible, c.Convert(Array.Empty<int>(), typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, c.Convert(new[] { 1 }, typeof(Visibility), null, Invariant));

        Assert.Equal(Visibility.Visible, c.Convert(new Dictionary<string, int>(), typeof(Visibility), null, Invariant));
        Assert.Equal(Visibility.Collapsed, c.Convert(new Dictionary<string, int> { ["a"] = 1 }, typeof(Visibility), null, Invariant));
    }
}
