using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BuddyDeck.App.Controls;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Converters;

/// <summary>
/// 时间戳 → 「2026-10-06 18:39」。
///
/// **同时吃 Unix 毫秒和 ISO 字符串**（HANDOVER 坑 2）。
/// 逻辑全在 <see cref="TimeText"/> 里，这里只做「调它」。
///
/// ConverterParameter：
///   · "date"  → 只到日期
///   · "clock" → 只到秒（最近请求那一列用）
///   省略      → 日期 + 时分
/// </summary>
public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (parameter as string) switch
        {
            "date" => TimeText.FormatDate(value),
            "clock" => TimeText.FormatClock(value),
            _ => TimeText.FormatTimestamp(value),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 一个截止时刻 → 「还剩 12 天」/「已过期 2 天」。
///
/// ConverterParameter = "cooldown" 时改说冷却的措辞（「3 分钟后恢复」）。
/// </summary>
public sealed class RemainingTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var now = DateTimeOffset.UtcNow;

        return parameter as string == "cooldown"
            ? TimeText.FormatCooldown(value, now)
            : TimeText.FormatRemaining(value, now);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 余量比例（0–1）→ 「93.4%」。纯格式化，没有业务判断。
/// </summary>
public sealed class RatioTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? QuotaMath.FormatRatio(d) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 账号状态 → Brush。资源 key 由 <see cref="StatusAppearance"/> 决定。
///
/// ConverterParameter：
///   · "rail"  → 左侧竖条色
///   · "badge" → 徽章底色
///   省略      → 文字/图标色
/// </summary>
public sealed class AccountStateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AccountState state) return Fallback();

        var key = (parameter as string) switch
        {
            "rail" => StatusAppearance.RailKey(state),
            "badge" => StatusAppearance.BadgeBackgroundKey(state),
            _ => StatusAppearance.BrushKey(state),
        };

        return Lookup(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>找不到资源时的兜底：中性灰，不是透明 —— 透明会让人以为没渲染。</summary>
    private static Brush Fallback() => new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0x72));

    private static Brush Lookup(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Fallback();
}

/// <summary>用量条状态 → 条的填充色。</summary>
public sealed class QuotaStateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not QuotaState state) return Fallback();

        var key = parameter as string == "text"
            ? StatusAppearance.QuotaTextKey(state)
            : StatusAppearance.QuotaBrushKey(state);

        return Application.Current?.TryFindResource(key) as Brush ?? Fallback();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Fallback() => new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0x72));
}

/// <summary>网关在线 → 状态点的色。</summary>
public sealed class GatewayDotBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var online = value is bool b && b;
        var key = StatusAppearance.GatewayDotKey(online);
        return Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0x72));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// bool → Visibility。
///
/// WPF-UI 自带一个 BooleanToVisibilityConverter，但这里自己写：
/// 统一走 InvariantCulture 可预测的行为，且支持反相。
///
/// ## 反相怎么做（**这里踩过一次，改过一版**）
///
/// 反相有两条路，**必须用第二条**：
///
/// ❌ 旧做法（已废弃）：声明第二个实例然后用 ConverterParameter 区分
///     <conv:BoolToVisibilityConverter x:Key="InverseBoolToVisibility" />
///     —— 这个 key 名字叫 Inverse，但**行为跟正相完全一样**，
///        因为 ConverterParameter 是绑定时才传的，和 key 无关。
///        ui 就被它坑过：仪表盘「离线时显示原因」那一块渲染成空白。
///
/// ✅ 正确做法：构造函数里把 invert 定死
///     <conv:BoolToVisibilityConverter x:Key="InverseBoolToVisibility" Invert="True" />
///     x:Key 和实例状态一一对应，不可能对不上。
///
/// 【为什么建一个属性而不是两个类】：两种行为的差异只有一个 bool，
/// 拆成两个类会让 App.xaml 里多一个类名要同步。属性 + 构造参数是最小面。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 反相开关。**由 XAML 在声明实例时设定**（<c>Invert="True"</c>），
    /// 这样 key 名和行为永远一致。
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value is bool b && b;

        // 参数级反相仍然保留 —— 但**不要**再靠它去实现「Inverse 那个 key」。
        // 它只是给「同一个 key 想临时反一下」这种少数场景留的口子。
        if (parameter as string is "invert" or "false" or "!") truthy = !truthy;

        if (Invert) truthy = !truthy;

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;

        if (parameter as string is "invert" or "false" or "!") visible = !visible;
        if (Invert) visible = !visible;

        return visible;
    }
}

/// <summary>
/// 字符串 → Visibility。
///
/// 默认：**非空 → Visible**（「有错误信息就显示错误块」）。
///
/// ## 反相靠 <see cref="Invert"/> 属性，不靠 ConverterParameter
///
/// 原因同 <see cref="BoolToVisibilityConverter"/> 的类注释：
/// 用 ConverterParameter 实现的「Inverse 那个 key」名不副实，
/// 声明出来的第二个实例行为和正相一模一样。**这是被 ui 实际踩到的坑。**
///
///     <conv:StringToVisibilityConverter x:Key="StringToVisibility" />
///     <conv:StringToVisibilityConverter x:Key="EmptyStringToVisibility" Invert="True" />
///
/// 用 IsNullOrWhiteSpace 而不是 IsNullOrEmpty —— 上游回一个空格串的时候，
/// 按空处理才对。这种情况真的会有。
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <summary>反相开关。XAML 声明实例时设定。</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);

        // 参数级反相保留给「同一个 key 临时反一下」的少数场景。
        if (parameter as string == "empty") hasText = !hasText;
        if (Invert) hasText = !hasText;

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 集合为空 → Visibility。空态那一块用（「还没有账号」）。
///
/// ## ⚠ 它只认 ICollection —— 传别的东西会**静默判为非空**
///
/// `ObservableCollection&lt;T&gt;` / `List&lt;T&gt;` / `T[]` / `Dictionary` 都实现
/// `ICollection`，工作正常。
///
/// 但下面两种**不实现 ICollection**，于是模式
/// `is ICollection { Count: 0 } or null` 两个分支都不匹配，
/// `isEmpty` 保持默认 false → **返回 Collapsed（判为非空）**：
///
///   · **`Lines.Count`（int）** —— 最常见的错法。日志页空态不显示就是这个原因。
///     ui 实际踩过。
///   · LINQ 的查询结果（`Lines.Where(...)`）—— 裸 `IEnumerable&lt;T&gt;`。
///
/// **失败方式是「静默」的**：不抛异常、不报警告，只是空态该出现时不出现。
/// 空结果集 + 无空态 = 界面看起来像「还在加载」。这是最坏的一类 bug。
///
/// 所以：**绑集合本身**（`{Binding Lines}`），要过滤就先物化成
/// `List` / `ObservableCollection` 再绑。
///
/// ## 反相同样靠属性，不靠参数
/// </summary>
public sealed class EmptyCollectionToVisibilityConverter : IValueConverter
{
    /// <summary>反相开关。XAML 声明实例时设定。</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is System.Collections.ICollection { Count: 0 } or null;

        if (parameter as string == "notempty") isEmpty = !isEmpty;
        if (Invert) isEmpty = !isEmpty;

        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
