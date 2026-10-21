using System.Globalization;

namespace BuddyDeck.Core;

/// <summary>
/// 一个设置值的**来源**。
///
/// ## 为什么需要"来源"这个概念
///
/// M14 的核心需求是：**只把「用户显式改过的字段」写回配置文件**。
///
/// 要做到这一点，光有「当前值」是不够的 —— 还必须知道**这个值是从哪来的**：
///
/// ```
/// --port 8791 起了一个测试实例     ← 命令行来的，**不能写回文件**
/// 面板上把端口改成 8791            ← 用户改的，**必须写回文件**
/// ```
///
/// 两者的**最终值一模一样**，但一个是临时的、一个是持久的。只看值分不出来。
///
/// ## 为什么这个区分重要（不是洁癖）
///
/// 如果命令行传入的值被写回文件，会发生这件事：
///
/// ```
/// 第 1 天  dotnet run -- --port 8791        → 文件里记下 8791
/// 第 2 天  dotnet run                        → 没传端口，却从文件读到 8791
/// ```
///
/// 用户第 2 天**没有传端口**，网关却跑到 8791 上 —— 他完全无法解释为什么。
/// 更糟的是：他可能正是因为 8791 撞了别的东西才改回默认的。
///
/// ## 枚举的顺序就是优先级（低 → 高）
///
/// 数值越大优先级越高，合并时直接取大的。这样「优先级链」不是一段
/// if/else 代码，而是**类型自身的一个属性** —— 想插一层新来源时
/// 只要给它一个合适的数值，合并逻辑一行都不用改。
/// </summary>
public enum OptionOrigin
{
    /// <summary>编译内置的默认值。**永不落盘**（它本来就是默认）。</summary>
    Default = 0,

    /// <summary>配置文件里读到的。**这是唯一会落盘的来源**（用户之前改过的）。</summary>
    File = 1,

    /// <summary>环境变量。**永不落盘**（临时值，见类型注释）。</summary>
    Environment = 2,

    /// <summary>命令行参数。**永不落盘**（临时值，优先级最高）。</summary>
    CommandLine = 3,
}

/// <summary>
/// 一个带来源标记的值。
/// </summary>
/// <typeparam name="T">值的类型。</typeparam>
public sealed record Sourced<T>
{
    /// <summary>当前生效的值。</summary>
    public required T Value { get; init; }

    /// <summary>这个值从哪来。</summary>
    public required OptionOrigin Origin { get; init; }

    /// <summary>
    /// 这个值该不该写回配置文件。
    ///
    /// **只有 <see cref="OptionOrigin.File"/> 该写** —— 但那不是因为「文件来的要存回去」，
    /// 而是因为 <see cref="OptionSources"/> 在合并时，只有「用户通过设置接口显式改过」
    /// 的字段才被标成 <see cref="OptionOrigin.File"/>（见 <see cref="OptionSources{TSettings}.WithUserEdit"/>）。
    ///
    /// 换句话说：**这一个属性就是"只落显式改过的字段"的全部实现。**
    /// </summary>
    public bool ShouldPersist => Origin == OptionOrigin.File;

    public override string ToString() => $"{Value} ({Origin})";
}

/// <summary>
/// 设置的**来源与优先级**层（M14 的一半）。
///
/// ## 优先级链（对齐旧版 `bin.js` + `config.js` 的实际行为）
///
/// ```
/// 命令行  >  环境变量  >  配置文件  >  默认值
///   ↑最高                              ↑最低
/// ```
///
/// ## 为什么是这个顺序
///
/// **① 命令行最高 —— 因为它最"当下"。**
/// 用户敲下 `--port 8791` 的那一刻，他的意图是最新、最具体的。
/// 如果文件能盖过命令行，那"临时起一个测试实例"就永远做不到 ——
/// 每次都得先去改配置文件，用完再改回来。旧版 `config.js` 的注释也这么说：
/// every value can be overridden by a flag。
///
/// **② 环境变量次之 —— 因为它是"这个进程"的上下文，比文件里的长期偏好更贴近现场。**
/// 典型场景：CI 里用 `WORKBUDDY_PORT=0` 跑测试，而开发机文件里存着 8787。
/// 环境变量是这次运行的条件，文件是这台机器的偏好 —— 现场条件优先。
///
/// **③ 配置文件在默认值之上 —— 因为"用户上次的选择"好过"我们编的默认"。**
/// 这是整个文件存在的意义：让面板上改过的设置活过重启。
///
/// **④ 默认值垫底 —— 兜底，永不落盘。**
///
/// ## 一个容易搞反的点
///
/// 优先级是**关于"哪个值生效"**的，和**"哪个值会被存"**是两件独立的事：
///
/// ```
/// 生效顺序：  命令行 > 环境变量 > 文件 > 默认
/// 落盘条件：  只有「用户显式改过的」才落 —— 见 Sourced{T}.ShouldPersist
/// ```
///
/// 所以一个命令行传进来的值会**生效**（它优先级最高）但**不被存储**
/// （它是临时的）。这两条规则不冲突，反而互相配合：
/// **临时值影响这一次运行，持久值影响以后每一次运行。**
/// </summary>
public static class OptionSources
{
    /// <summary>把一个「用户显式改过」的值标成可落盘。</summary>
    public static Sourced<T> UserEdit<T>(T value) => new() { Value = value, Origin = OptionOrigin.File };

    /// <summary>命令行来源。</summary>
    public static Sourced<T> FromCommandLine<T>(T value) => new() { Value = value, Origin = OptionOrigin.CommandLine };

    /// <summary>环境变量来源。</summary>
    public static Sourced<T> FromEnvironment<T>(T value) => new() { Value = value, Origin = OptionOrigin.Environment };

    /// <summary>配置文件来源。</summary>
    public static Sourced<T> FromFile<T>(T value) => new() { Value = value, Origin = OptionOrigin.File };

    /// <summary>默认值来源。</summary>
    public static Sourced<T> Default<T>(T value) => new() { Value = value, Origin = OptionOrigin.Default };

    /// <summary>
    /// 按优先级挑一个胜出者。
    ///
    /// @returns 优先级最高的那个；输入全为 null 时返回 null。
    ///          **同优先级取第一个** —— 调用方按「更具体 → 更宽泛」传即可。
    /// </summary>
    public static Sourced<T>? Winner<T>(params Sourced<T>?[] candidates)
    {
        Sourced<T>? best = null;

        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (best is null || candidate.Origin > best.Origin) best = candidate;
        }

        return best;
    }

    /// <summary>
    /// 解析一个可空字符串：空串视为「没给」。
    ///
    /// 为什么要这一步：命令行的 `--api-key ""`、环境变量设成空串，语义上都是
    /// 「我没提供值」，而不是「我要把值清空」。**这两件事必须分开** ——
    /// 后者应该走设置接口（那是用户显式操作），而不是靠一个空命令行参数。
    /// </summary>
    public static Sourced<string>? StringOrNull(string? raw, OptionOrigin origin)
        => string.IsNullOrEmpty(raw)
            ? null
            : new Sourced<string> { Value = raw, Origin = origin };

    /// <summary>
    /// 解析一个可空整数。
    ///
    /// **解析失败返回 null（视同没给），不抛** —— 但这对端口是例外，
    /// 见 <see cref="PortOrNull"/>。
    /// </summary>
    public static Sourced<int>? IntOrNull(string? raw, OptionOrigin origin)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? new Sourced<int> { Value = value, Origin = origin }
            : null;

    /// <summary>
    /// 解析端口。
    ///
    /// ## 为什么端口非法要抛，而别的字段非法就忽略
    ///
    /// 与 <c>GatewayOptions.ParsePort</c> 同一条理由（那是本项目的既有约定）：
    ///
    /// > 非法值**不静默回退** —— 悄悄用 8787 可能导致撞上用户正在跑的网关，
    /// > 那比启动失败更糟（AGENTS.md 的铁律）。
    ///
    /// 端口填错是**危及正在运行的服务**的事故，必须让用户当场知道。
    /// 而 `--metrics-recent abc` 顶多是少一个调优参数，忽略掉继续跑更合适。
    /// **代价不对称，处置就该不对称。**
    /// </summary>
    public static Sourced<int>? PortOrNull(string? raw, OptionOrigin origin)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535)
        {
            throw new ArgumentException($"端口必须是 1-65535 之间的整数，收到：{raw}");
        }
        return new Sourced<int> { Value = port, Origin = origin };
    }
}

/// <summary>
/// 一份「带来源的」设置快照，以及它的持久化投影。
///
/// ## 为什么用一个泛型包装而不是让 SettingsStore 自己做
///
/// 这样 <c>OptionSources</c> 的合并/优先级逻辑可以**独立于具体有哪些字段**来测
/// —— 加一个设置字段不需要动这一层的任何一行。
/// </summary>
/// <typeparam name="TSettings">具体的设置载荷。</typeparam>
public sealed record SourcedSettings<TSettings>
    where TSettings : class
{
    /// <summary>生效的设置（合并后的结果）。</summary>
    public required TSettings Value { get; init; }

    /// <summary>
    /// 哪些字段该落盘。
    ///
    /// 由合并方（<see cref="SettingsMerge"/>）按每个字段的
    /// <see cref="Sourced{T}.ShouldPersist"/> 填。
    /// </summary>
    public required IReadOnlySet<string> PersistableFields { get; init; }
}

/// <summary>
/// 把「命令行 / 环境变量 / 配置文件」三层来源合并成一份生效设置，
/// 并算出哪些字段该落盘。
///
/// ## 优先级链（本类型的全部意义）
///
/// ```
/// 命令行  >  环境变量  >  配置文件  >  默认值
/// ```
///
/// 完整理由写在 <see cref="OptionSources"/> 的类注释里。这里只重复最关键的
/// 一句，因为它决定了本类型的每个设计细节：
///
/// > **优先级是关于「哪个值生效」的，落盘条件是「哪个值该被记住」——
/// > 这是两件独立的事。**
///
/// 所以一个命令行传进来的值会**生效**（它优先级最高）但**不被存储**
/// （它是临时的）。两条规则不冲突，反而互相配合：
/// **临时值影响这一次运行，持久值影响以后每一次运行。**
///
/// ## 这个类型不做的事
///
/// - **不解析命令行** —— 那是 `GatewayOptions.Parse` 的活（在另一个程序集）。
///   这里只接收"已经解析好的来源值"。
/// - **不写文件** —— 那是 <see cref="SettingsStore"/> 的活。
///
/// 它只回答一个问题：**给定三层来源，每个字段取哪个值、该不该落盘。**
/// </summary>
public static class SettingsMerge
{
    /// <summary>
    /// 合并三层来源。
    ///
    /// @param fromFile        配置文件里读到的（null 字段 = 文件里没这个键）
    /// @param fromEnvironment 环境变量（null 字段 = 没设）
    /// @param fromCommandLine 命令行（null 字段 = 没传）
    /// @param defaults        编译内置默认值（**应当全有值** —— 它是兜底）
    ///
    /// @returns 生效值 + 该落盘的字段名集合。
    ///
    /// ## 落盘集合只来自文件层，这是刻意的
    ///
    /// `PersistableFields` 取的是 **`fromFile` 里非空的字段**，而不是
    /// 命令行/环境变量的。理由：
    ///
    /// ```
    /// 文件里有这个键  ⇒ 用户以前通过设置接口显式改过它  ⇒ 该继续存
    /// 命令行/环境变量有 ⇒ 这次运行是临时的               ⇒ 绝不存
    /// ```
    ///
    /// ⚠ 一个反直觉但正确的推论：**用户清了某个值（改成空）之后，该键会
    /// 从文件里消失，于是下次它不再是"可持久化字段"。** 这是对的 ——
    /// "用户把它改回未设置"就是一个应当被记住的决定。
    /// </summary>
    public static SourcedSettings<TSettings> Merge<TSettings>(
        TSettings fromFile,
        TSettings fromEnvironment,
        TSettings fromCommandLine,
        TSettings defaults)
        where TSettings : class
    {
        // 逐层往上叠：命令行 > 环境变量 > 文件 > 默认。
        // Overlay 用"相对默认值的差异"来判断哪几个字段这一层真的给了值。
        var merged = defaults;

        var fileFields = ChangedFields(defaults, fromFile);
        merged = Overlay(merged, fromFile, fileFields);

        var envFields = ChangedFields(defaults, fromEnvironment);
        merged = Overlay(merged, fromEnvironment, envFields);

        var cliFields = ChangedFields(defaults, fromCommandLine);
        merged = Overlay(merged, fromCommandLine, cliFields);

        return new SourcedSettings<TSettings>
        {
            Value = merged,
            // ⚠ 只用 fileFields —— 命令行/环境变量绝不进这个集合
            PersistableFields = fileFields,
        };
    }

    /// <summary>
    /// 找出 <paramref name="candidate"/> 里**相对默认值非空**的字段名。
    ///
    /// ## 返回的是**落盘键名**，不是属性名
    ///
    /// 这是个容易搞错的地方，所以显式说明：本方法返回
    /// `FieldNames.Port`（`"port"`）而不是 `SettingsValues.Port`（`"Port"`）。
    ///
    /// 理由：这个集合最终的用途是给 <see cref="SettingsValues.ToDocument"/>
    /// 判断"哪些键该写进 JSON"。整条链上**只有一个词汇表**——
    /// 落盘键名 —— 就不会出现"合并层说 Port、落盘层找 port"这种静默失配
    /// （症状是字段永远存不下去，而且不报错）。
    ///
    /// 映射由 `[JsonPropertyName]` 之外的**约定**给出：本项目的设置类型
    /// 属性名与键名只差首字母大小写（`Port` ↔ `port`、`ApiKey` ↔ `apiKey`），
    /// 所以这里用 `char.ToLowerInvariant` 转换，并对 `FieldNames.All` 做校验
    /// —— 对不上的属性不会被算进集合（宁可漏存，也不写一个不认识的键）。
    ///
    /// ## 为什么要跟默认值比，而不是直接看 null
    ///
    /// 因为这一层的输入是**任意 TSettings 实例**，调用方未必只填它知道的值
    /// —— 也可以传一个"全默认 + 覆盖了两项"的实例。跟默认值比能同时覆盖
    /// 两种调用方式，且语义清楚：**"这一层把哪些字段变成了别的值"**。
    ///
    /// ## 为什么用反射
    ///
    /// 手写版加一个设置字段要改 4 处（三个来源 + 合并），漏一处就是
    /// **静默不生效** —— 值看起来是对的，只是不覆盖。这类 bug 极难发现。
    /// 反射版加字段零改动。
    ///
    /// 代价是慢一点，但**这是启动路径上跑一次的代码**，不是热路径。
    /// </summary>
    public static IReadOnlySet<string> ChangedFields<TSettings>(TSettings defaults, TSettings candidate)
        where TSettings : class
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in SettingsProperties<TSettings>())
        {
            var candidateValue = property.GetValue(candidate);
            if (IsUnset(candidateValue)) continue;                    // 没给值

            var defaultValue = property.GetValue(defaults);
            if (Equals(candidateValue, defaultValue)) continue;        // 与默认相同 = 这一层没改它

            var key = FieldNames.Match(property.Name);
            if (key is null) continue;    // 对不上任何落盘键 → 不算（宁可漏存，不写未知键）

            result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// 把 <paramref name="source"/> 里 <paramref name="fields"/> 指定的字段
    /// 盖到 <paramref name="target"/> 上。
    ///
    /// ## 为什么用 MemberwiseClone 而不是逐字段构造
    ///
    /// 本项目的设置类型都是 record（不可变、init-only）。逐字段 `with` 需要
    /// 把每个字段名写一遍 —— 那正是上面想避免的"加字段要改多处"。
    /// `MemberwiseClone` 起步再覆盖差异字段，字段增删都不用改这里。
    /// </summary>
    private static TSettings Overlay<TSettings>(TSettings target, TSettings source, IReadOnlySet<string> fields)
        where TSettings : class
    {
        if (fields.Count == 0) return target;

        var copy = (TSettings)target.GetType()
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(target, null)!;

        foreach (var property in SettingsProperties<TSettings>())
        {
            if (!property.CanWrite) continue;

            // ⚠ fields 里装的是**落盘键名**（"port"），不是属性名（"Port"）
            // —— 与 ChangedFields 的返回值保持同一个词汇表。
            var key = FieldNames.Match(property.Name);
            if (key is null || !fields.Contains(key)) continue;

            var value = property.GetValue(source);
            if (IsUnset(value)) continue;

            property.SetValue(copy, value);
        }

        return copy;
    }

    /// <summary>
    /// 这个值算不算"没给"。
    ///
    /// 空串算没给 —— 理由与 <see cref="OptionSources.StringOrNull"/> 一致：
    /// `--api-key ""` 的语义是"我没提供"，不是"把它清空"。
    /// </summary>
    private static bool IsUnset(object? value)
        => value is null || (value is string s && s.Length == 0);

    /// <summary>
    /// 取一个设置类型上**可参与合并**的属性。
    ///
    /// 跳过只用来看的派生属性（比如 `ResolveVaultPath()` 那种方法不算属性，
    /// 但将来若有 `IsConfigured` 之类，也不该参与合并）—— 判据是
    /// "可写" 且 "不是索引器"。
    /// </summary>
    private static System.Reflection.PropertyInfo[] SettingsProperties<TSettings>()
    {
        var properties = typeof(TSettings).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        return [.. properties.Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)];
    }
}
