using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Formatting;
using CodexTokenMeter.Core.Settings;

namespace CodexTokenMeter.App;

/// <summary>
/// 胶囊中的一项：图标 + 数值。
/// </summary>
/// <param name="Metric">对应的指标，用于生成提示文本与诊断。</param>
/// <param name="Text">显示文本。</param>
/// <param name="IconKey">图标键；为 null 时不画图标。</param>
/// <param name="IsCost">是否使用费用强调色。</param>
/// <param name="RingPercent">为该值时渲染上下文圆环，而不是普通图标。</param>
/// <param name="Tooltip">悬停提示。</param>
internal sealed record CapsuleItem(
    CapsuleMetric Metric,
    string Text,
    string? IconKey,
    bool IsCost = false,
    double? RingPercent = null,
    string? Tooltip = null);

/// <summary>
/// 胶囊内容的分组：一组指标，外加一个可选的分隔线。
/// </summary>
/// <param name="Items">该组的指标。</param>
/// <param name="PrecededByDivider">该组之前是否插入分隔线。</param>
internal sealed record CapsuleGroup(
    IReadOnlyList<CapsuleItem> Items,
    bool PrecededByDivider);
/// <summary>
/// 把数据与用户选中的指标渲染为胶囊内容。
/// </summary>
/// <remarks>
/// <para>
/// 与展示层分离的纯逻辑：输入数据与指标列表，输出描述性的项与分组。
/// 这样格式化、分组与降级规则都能单独测试，不必启动 WPF。
/// </para>
/// <para>
/// 用户可勾选任意指标组合，因此分组分隔线的位置无法写死——
/// 由相邻两项的分组是否相同推导得出。
/// </para>
/// </remarks>
internal static class CapsulePresenter
{
    /// <summary>无数据时的占位符。</summary>
    private const string Placeholder = "—";

    /// <summary>
    /// 构建胶囊内容。
    /// </summary>
    /// <param name="data">当前数据。</param>
    /// <param name="metrics">用户选中的指标，按规范顺序。</param>
    public static IReadOnlyList<CapsuleGroup> Build(
        OverlayData data,
        IReadOnlyList<CapsuleMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(metrics);

        // 先逐项构建，丢弃无法呈现的项（当前没有这种情况，
        // 但保留过滤逻辑以便新增指标时不必改分组代码）。
        var items = new Dictionary<CapsuleMetric, CapsuleItem>();

        foreach (var metric in metrics)
        {
            if (CreateItem(data, metric) is { } item)
            {
                items[metric] = item;
            }
        }

        // 分组规则在 Core 中实现，以便单独测试；
        // 这里只负责把分组结果映射回展示项。
        var present = metrics.Where(items.ContainsKey).ToList();

        return
        [
            .. CapsuleMetricGrouper.Group(present).Select(group => new CapsuleGroup(
                [.. group.Items.Select(metric => items[metric])],
                group.PrecededByDivider)),
        ];
    }

    /// <summary>
    /// 构建单个指标。
    /// </summary>
    /// <remarks>
    /// 费用类指标在未定价时返回 null 以外的项并显示占位符，
    /// 而不是整个隐藏：指标位置固定，忽隐忽现会让胶囊宽度跳动得更厉害。
    /// </remarks>
    private static CapsuleItem? CreateItem(OverlayData data, CapsuleMetric metric) => metric switch
    {
        CapsuleMetric.TurnInput => new(metric, NumberFormatter.Compact(data.CurrentTurn.Input),
            "arrowUp", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.CurrentTurn.Input))),

        CapsuleMetric.TurnOutput => new(metric, NumberFormatter.Compact(data.CurrentTurn.Output),
            "arrowDown", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.CurrentTurn.Output))),

        CapsuleMetric.TurnTotal => new(metric, NumberFormatter.Compact(data.CurrentTurn.Total),
            "bars", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.CurrentTurn.Total))),

        CapsuleMetric.TurnReasoning => new(metric, NumberFormatter.Compact(data.CurrentTurn.ReasoningOutput),
            "sparkle", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.CurrentTurn.ReasoningOutput))),

        CapsuleMetric.TurnCost => new(metric, FormatCost(data.CurrentTurnCost),
            "dollar", IsCost: true,
            Tooltip: TooltipLine(metric, data.CurrentTurnCost is { } tc ? $"${NumberFormatter.Cost(tc)}" : "未定价")),

        CapsuleMetric.TotalInput => new(metric, NumberFormatter.Compact(data.Cumulative.Input),
            "arrowUp", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.Cumulative.Input))),

        CapsuleMetric.TotalOutput => new(metric, NumberFormatter.Compact(data.Cumulative.Output),
            "arrowDown", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.Cumulative.Output))),

        CapsuleMetric.TotalTokens => new(metric, NumberFormatter.Compact(data.Cumulative.Total),
            "bars", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.Cumulative.Total))),

        CapsuleMetric.CachedInput => new(metric, NumberFormatter.Compact(data.Cumulative.CachedInput),
            "refresh", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.Cumulative.CachedInput))),

        CapsuleMetric.CacheHitRate => new(metric, $"{data.CacheHitRate:F1}%",
            "percent", Tooltip: TooltipLine(metric, $"{data.CacheHitRate:F2}%")),

        CapsuleMetric.TotalCost => new(metric, FormatCost(data.TotalCost),
            "dollar", IsCost: true,
            Tooltip: TooltipLine(metric, data.TotalCost is { } c ? $"${NumberFormatter.Cost(c)}" : "未定价")),

        // 上下文占用用圆环呈现，比一个百分比数字更直观。
        CapsuleMetric.ContextPercent => new(metric, $"{data.ContextPercent:F0}%",
            IconKey: null,
            RingPercent: data.ContextPercent,
            Tooltip: TooltipLine(metric, $"{data.ContextPercent:F1}%")),

        CapsuleMetric.ContextTokens => new(metric, NumberFormatter.Compact(data.ContextUsedTokens),
            "gauge", Tooltip: TooltipLine(metric, NumberFormatter.Full(data.ContextUsedTokens))),

        CapsuleMetric.Messages => new(metric,
            NumberFormatter.Count(data.UserMessageCount + data.AssistantMessageCount),
            "chat", Tooltip: TooltipLine(metric,
                $"{NumberFormatter.Count(data.UserMessageCount)} 用户 / "
                + $"{NumberFormatter.Count(data.AssistantMessageCount)} 助手")),

        CapsuleMetric.ToolCalls => new(metric, NumberFormatter.Count(data.ToolCallCount),
            "terminal", Tooltip: TooltipLine(metric, NumberFormatter.Count(data.ToolCallCount))),

        CapsuleMetric.Project => new(metric,
            ProjectResolver.Resolve(data.WorkingDirectory).Name ?? Placeholder,
            "folder",
            Tooltip: TooltipLine(metric, data.WorkingDirectory ?? "未确定")),

        CapsuleMetric.Model => new(metric, data.Model ?? Placeholder,
            "chip", Tooltip: TooltipLine(metric, data.Model ?? "未确定")),

        CapsuleMetric.Duration => new(metric, NumberFormatter.Duration(data.ActiveDuration),
            "clock", Tooltip: TooltipLine(metric, NumberFormatter.DurationLong(data.ActiveDuration))),

        _ => null,
    };

    /// <summary>费用占位：未定价时不留空，避免看起来像 0。</summary>
    private static string FormatCost(double? value) =>
        value is { } cost ? $"${NumberFormatter.Cost(cost)}" : Placeholder;

    private static string TooltipLine(CapsuleMetric metric, string value) =>
        $"{CapsuleMetricCatalog.GetDisplayName(metric)}\t{value}";

    /// <summary>
    /// 构建胶囊的整体悬停提示。
    /// </summary>
    /// <remarks>
    /// 图标在指标较多时会有歧义，而胶囊本身不能加宽太多。
    /// 因此把「完整名称 + 精确数值」放到悬停提示里按需呈现，
    /// 既保住胶囊的紧凑，也不牺牲可读性。
    /// </remarks>
    public static string BuildTooltip(OverlayData data, IReadOnlyList<CapsuleMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(metrics);

        var lines = new List<string>
        {
            data.IsEmpty ? "无活动会话" : ProjectResolver.Resolve(data.WorkingDirectory).Name ?? "会话",
        };

        if (!data.IsEmpty)
        {
            var headline = metrics
                .Select(metric => (Metric: metric, Item: CreateItem(data, metric)))
                .Where(pair => pair.Item is not null)
                .Select(pair => pair.Item!.Tooltip)
                .Where(line => !string.IsNullOrWhiteSpace(line));

            lines.AddRange(headline!);
        }

        var notice = data switch
        {
            { IsUnpriced: true } => "该模型未收录价格",
            { IsPartial: true } => "日志尚未读完，数值可能偏低",
            { IsConnected: false } => "未连接 Codex",
            _ => null,
        };

        if (notice is not null)
        {
            lines.Add(string.Empty);
            lines.Add(notice);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 指标图标的矢量几何。
/// </summary>
/// <remarks>
/// <para>
/// 所有图标都在同一个 16×16 视口内绘制，并保持相近的视觉重量：
/// 描边端点据满约 11–12 单位，留出均匀的边距。
/// 若各图标的实际覆盖范围差异过大，等比缩放后会显得参差不齐。
/// </para>
/// <para>
/// 只用描边不用填充：与胶囊里细线分隔的风格一致，
/// 且在深色背景下不会显得比文字更重。
/// </para>
/// </remarks>
internal static class CapsuleIcons
{
    /// <summary>取值：SVG 风格的路径数据。</summary>
    public static string? Get(string? key) => key switch
    {
        // 上下箭头：竖线 2.5→13.5，箭头展开 4.2。
        "arrowUp" => "M8,13.5 L8,2.5 M4.2,6.3 L8,2.5 L11.8,6.3",
        "arrowDown" => "M8,2.5 L8,13.5 M4.2,9.7 L8,13.5 L11.8,9.7",

        // 柱状图：三根柱高度递进，基线对齐。
        "bars" => "M4,13.5 L4,9 M8,13.5 L8,2.5 M12,13.5 L12,5",

        // 刷新：开口圆环 + 箭头。
        "refresh" => "M12.6,8 A4.6,4.6 0 1 1 8,3.4 M10.2,1.2 L10.2,3.8 L12.8,3.8",

        // 百分比：斜线加两个圆环。
        "percent" => "M4.2,11.8 L11.8,4.2 M5.4,5.4 A1.1,1.1 0 1 1 3.2,5.4 A1.1,1.1 0 1 1 5.4,5.4 M12.8,10.6 A1.1,1.1 0 1 1 10.6,10.6 A1.1,1.1 0 1 1 12.8,10.6",

        // 美元：S 形加贯穿竖线，高度接近满格以与其他图标等重。
        "dollar" => "M8,2.2 L8,13.8 M11,4.9 C11,3.6 9.7,2.9 8,2.9 C6.3,2.9 5,3.6 5,4.9 C5,6.5 6.5,7 8,7.5 C9.5,8 11,8.5 11,10.1 C11,11.4 9.7,12.1 8,12.1 C6.3,12.1 5,11.4 5,10.1",

        // 仪表：开口圆环加指针。
        "gauge" => "M2.8,11.2 A5.6,5.6 0 1 1 13.2,11.2 M8,11.2 L11.2,6.6",

        // 对话气泡。
        "chat" => "M2.6,5 A2.2,2.2 0 0 1 4.8,2.8 L11.2,2.8 A2.2,2.2 0 0 1 13.4,5 L13.4,9 A2.2,2.2 0 0 1 11.2,11.2 L6.6,11.2 L3,13.6 L3.4,11.2 A2.2,2.2 0 0 1 2.6,9.4 Z",

        // 终端提示符：尖括号加下划线。
        "terminal" => "M2.8,4.6 L6,8 L2.8,11.4 M8.4,11.6 L13.2,11.6",

        // 文件夹。
        "folder" => "M2.4,5.2 A1.7,1.7 0 0 1 4.1,3.5 L6.2,3.5 L7.8,5.4 L11.9,5.4 A1.7,1.7 0 0 1 13.6,7.1 L13.6,11 A1.7,1.7 0 0 1 11.9,12.7 L4.1,12.7 A1.7,1.7 0 0 1 2.4,11 Z",

        // 芯片：方框加四边引脚。
        "chip" => "M5.4,5.4 L10.6,5.4 L10.6,10.6 L5.4,10.6 Z M8,2.4 L8,5.4 M8,10.6 L8,13.6 M2.4,8 L5.4,8 M10.6,8 L13.6,8",

        // 时钟：圆加指针。
        "clock" => "M13.4,8 A5.4,5.4 0 1 1 2.6,8 A5.4,5.4 0 1 1 13.4,8 M8,4.8 L8,8.4 L10.6,10",

        // 火花：推理输出的标记。
        "sparkle" => "M8,2.4 L9.3,6.7 L13.6,8 L9.3,9.3 L8,13.6 L6.7,9.3 L2.4,8 L6.7,6.7 Z",

        _ => null,
    };
}
