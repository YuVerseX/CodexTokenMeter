namespace CodexTokenMeter.Core.Settings;

/// <summary>
/// 胶囊态可显示的指标。
/// </summary>
/// <remarks>
/// 展开面板展示全部指标，此处只控制**收起时**胶囊里显示哪些——
/// 胶囊是常驻的，横向空间有限，用户关心的指标因人而异：
/// 有人盯上下文占用，有人盯缓存命中率，有人只想知道花了多少钱。
/// </remarks>
public enum CapsuleMetric
{
    /// <summary>本轮输入 token。</summary>
    TurnInput,

    /// <summary>本轮输出 token。</summary>
    TurnOutput,

    /// <summary>本轮输入与输出合计。</summary>
    TurnTotal,

    /// <summary>本轮输出中的推理部分。</summary>
    TurnReasoning,

    /// <summary>本轮花费。</summary>
    TurnCost,

    /// <summary>会话累计输入。</summary>
    TotalInput,

    /// <summary>会话累计输出。</summary>
    TotalOutput,

    /// <summary>会话累计总量。</summary>
    TotalTokens,

    /// <summary>会话累计中命中缓存的输入。</summary>
    CachedInput,

    /// <summary>缓存命中率。</summary>
    CacheHitRate,

    /// <summary>会话累计花费。</summary>
    TotalCost,

    /// <summary>上下文占用百分比。</summary>
    ContextPercent,

    /// <summary>上下文已用 token 数。</summary>
    ContextTokens,

    /// <summary>用户与助手消息数。</summary>
    Messages,

    /// <summary>工具调用次数。</summary>
    ToolCalls,

    /// <summary>项目名。</summary>
    Project,

    /// <summary>模型名。</summary>
    Model,

    /// <summary>会话活跃时长。</summary>
    Duration,
}

/// <summary>
/// 指标的分组。
/// </summary>
/// <remarks>
/// 胶囊在组之间插入一道细分隔线。分组让指标之间的关系无需文字说明即可读出，
/// 而用户的勾选组合是任意的，因此分隔位置必须由数据推导而不是写死。
/// </remarks>
public enum CapsuleMetricGroup
{
    /// <summary>本轮用量。</summary>
    Turn,

    /// <summary>会话累计用量。</summary>
    Total,

    /// <summary>上下文窗口占用。</summary>
    Context,

    /// <summary>费用。</summary>
    Cost,

    /// <summary>计数类。</summary>
    Counters,

    /// <summary>会话标识类。</summary>
    Session,
}

/// <summary>
/// 胶囊指标的元数据。
/// </summary>
/// <remarks>
/// 只描述「有哪些指标、叫什么、属于哪一组」，
/// 不含图标与格式化——那些是展示层的事，属于 App 项目。
/// </remarks>
public static class CapsuleMetricCatalog
{
    /// <summary>
    /// 全部指标的规范顺序。
    /// </summary>
    /// <remarks>
    /// 胶囊按此顺序渲染，而不是按用户在设置文件里的书写顺序。
    /// 这样无论怎么勾选，视觉节奏都保持一致（本轮 → 累计 → 上下文 → 费用）。
    /// </remarks>
    public static IReadOnlyList<CapsuleMetric> Ordered { get; } =
    [
        CapsuleMetric.TurnInput,
        CapsuleMetric.TurnOutput,
        CapsuleMetric.TurnTotal,
        CapsuleMetric.TurnReasoning,
        CapsuleMetric.TotalInput,
        CapsuleMetric.TotalOutput,
        CapsuleMetric.TotalTokens,
        CapsuleMetric.CachedInput,
        CapsuleMetric.CacheHitRate,
        CapsuleMetric.ContextPercent,
        CapsuleMetric.ContextTokens,
        CapsuleMetric.TurnCost,
        CapsuleMetric.TotalCost,
        CapsuleMetric.Messages,
        CapsuleMetric.ToolCalls,
        CapsuleMetric.Project,
        CapsuleMetric.Model,
        CapsuleMetric.Duration,
    ];

    /// <summary>
    /// 默认显示在胶囊里的指标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「这一轮用了多少、花了多少、上下文还剩多少」是最普遍的需求，
    /// 因此默认选中这五项。
    /// </para>
    /// <para>
    /// 必须与 <see cref="Ordered"/> 的顺序一致：
    /// <see cref="Sanitize"/> 会把任意输入重排为规范顺序，
    /// 若此处不合规，首次运行（未配置）与保存后（已配置）会得到
    /// 两种不同的排列，胶囊会自己重排一次。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CapsuleMetric> Default { get; } =
    [
        CapsuleMetric.TurnInput,
        CapsuleMetric.TurnOutput,
        CapsuleMetric.CachedInput,
        CapsuleMetric.ContextPercent,
        CapsuleMetric.TurnCost,
    ];

    /// <summary>默认指标的持久化键。</summary>
    public static IReadOnlyList<string> DefaultKeys { get; } = [.. Default.Select(ToKey)];

    /// <summary>设置文件中使用的键名。</summary>
    public static string ToKey(CapsuleMetric metric) => metric switch
    {
        CapsuleMetric.TurnInput => "turnInput",
        CapsuleMetric.TurnOutput => "turnOutput",
        CapsuleMetric.TurnTotal => "turnTotal",
        CapsuleMetric.TurnReasoning => "turnReasoning",
        CapsuleMetric.TurnCost => "turnCost",
        CapsuleMetric.TotalInput => "totalInput",
        CapsuleMetric.TotalOutput => "totalOutput",
        CapsuleMetric.TotalTokens => "totalTokens",
        CapsuleMetric.CachedInput => "cachedInput",
        CapsuleMetric.CacheHitRate => "cacheHitRate",
        CapsuleMetric.TotalCost => "totalCost",
        CapsuleMetric.ContextPercent => "contextPercent",
        CapsuleMetric.ContextTokens => "contextTokens",
        CapsuleMetric.Messages => "messages",
        CapsuleMetric.ToolCalls => "toolCalls",
        CapsuleMetric.Project => "project",
        CapsuleMetric.Model => "model",
        CapsuleMetric.Duration => "duration",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    /// <summary>菜单中显示的名称。</summary>
    public static string GetDisplayName(CapsuleMetric metric) => metric switch
    {
        CapsuleMetric.TurnInput => "本轮输入",
        CapsuleMetric.TurnOutput => "本轮输出",
        CapsuleMetric.TurnTotal => "本轮总量",
        CapsuleMetric.TurnReasoning => "推理输出",
        CapsuleMetric.TurnCost => "本轮花费",
        CapsuleMetric.TotalInput => "累计输入",
        CapsuleMetric.TotalOutput => "累计输出",
        CapsuleMetric.TotalTokens => "累计总量",
        CapsuleMetric.CachedInput => "缓存读取",
        CapsuleMetric.CacheHitRate => "缓存命中率",
        CapsuleMetric.TotalCost => "累计花费",
        CapsuleMetric.ContextPercent => "上下文占用",
        CapsuleMetric.ContextTokens => "上下文用量",
        CapsuleMetric.Messages => "消息数",
        CapsuleMetric.ToolCalls => "工具调用",
        CapsuleMetric.Project => "项目名",
        CapsuleMetric.Model => "模型",
        CapsuleMetric.Duration => "活跃时长",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    /// <summary>菜单中的补充说明。</summary>
    public static string GetDescription(CapsuleMetric metric) => metric switch
    {
        CapsuleMetric.TurnTotal => "输入与输出合计",
        CapsuleMetric.TurnReasoning => "本轮输出中属于推理的部分，已计入输出",
        CapsuleMetric.CacheHitRate => "命中缓存的输入占总输入的比例",
        CapsuleMetric.TotalCost => "该分组的单价受上下文长度影响",
        CapsuleMetric.ContextTokens => "取自最近一次 token 统计",
        CapsuleMetric.Project => "取自会话的工作目录",
        CapsuleMetric.Model => "会话主要使用的模型",
        CapsuleMetric.Duration => "首个到最后一个事件的时间跨度",
        _ => string.Empty,
    };

    /// <summary>指标所属的分组。</summary>
    public static CapsuleMetricGroup GetGroup(CapsuleMetric metric) => metric switch
    {
        CapsuleMetric.TurnInput
            or CapsuleMetric.TurnOutput
            or CapsuleMetric.TurnTotal
            or CapsuleMetric.TurnReasoning => CapsuleMetricGroup.Turn,

        CapsuleMetric.TotalInput
            or CapsuleMetric.TotalOutput
            or CapsuleMetric.TotalTokens
            or CapsuleMetric.CachedInput
            or CapsuleMetric.CacheHitRate => CapsuleMetricGroup.Total,

        CapsuleMetric.ContextPercent
            or CapsuleMetric.ContextTokens => CapsuleMetricGroup.Context,

        CapsuleMetric.TurnCost
            or CapsuleMetric.TotalCost => CapsuleMetricGroup.Cost,

        CapsuleMetric.Messages
            or CapsuleMetric.ToolCalls => CapsuleMetricGroup.Counters,

        CapsuleMetric.Project
            or CapsuleMetric.Model
            or CapsuleMetric.Duration => CapsuleMetricGroup.Session,

        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    /// <summary>
    /// 解析设置文件中的键名。
    /// </summary>
    /// <remarks>
    /// 大小写不敏感：用户手工编辑设置文件时不该因为大小写而静默失效。
    /// </remarks>
    public static bool TryParse(string? key, out CapsuleMetric metric)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var trimmed = key.Trim();

            foreach (var candidate in Ordered)
            {
                if (ToKey(candidate).Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    metric = candidate;
                    return true;
                }
            }
        }

        metric = default;
        return false;
    }

    /// <summary>
    /// 把一组键名整理为有效的指标列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 去重、丢弃无法识别的键、并按规范顺序重排。
    /// 未知键直接忽略而不是报错：设置文件可能来自更新的版本，
    /// 不认识的项不应让整个配置失效。
    /// </para>
    /// <para>
    /// 结果为空时回退到默认集合——空胶囊没有任何意义，
    /// 而用户很可能只是把设置文件删坏了。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CapsuleMetric> Sanitize(IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return Default;
        }

        var selected = new HashSet<CapsuleMetric>();

        foreach (var key in keys)
        {
            if (TryParse(key, out var metric))
            {
                selected.Add(metric);
            }
        }

        return selected.Count == 0
            ? Default
            : [.. Ordered.Where(selected.Contains)];
    }

    /// <summary>把指标列表转为持久化键。</summary>
    public static IReadOnlyList<string> ToKeys(IEnumerable<CapsuleMetric> metrics) =>
        [.. metrics.Select(ToKey)];
}
