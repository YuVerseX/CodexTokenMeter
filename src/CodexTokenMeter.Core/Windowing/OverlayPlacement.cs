namespace CodexTokenMeter.Core.Windowing;

/// <summary>
/// 浮层相对宿主窗口的锚点。
/// </summary>
/// <remarks>
/// <para>
/// 锚点决定浮层贴哪两条边，同时也决定**内容增长的方向**：
/// 贴右边缘时，胶囊变宽会向左延伸，右边界保持不动；
/// 贴左边缘时则向右延伸。
/// </para>
/// <para>
/// 这与「存一个屏幕绝对坐标」的做法有本质区别。浮层跟随 Codex 窗口，
/// 若只记绝对坐标，Codex 一移动浮层就停在原地掉队了。
/// 锚点 + 偏移量描述的是「相对宿主的位置」，因此宿主移动时浮层自然跟随。
/// </para>
/// </remarks>
public enum OverlayAnchor
{
    /// <summary>宿主右上角。胶囊向右边缘对齐，向下与向左增长。</summary>
    TopRight,

    /// <summary>宿主右下角。胶囊向右边缘对齐，向上与向左增长。</summary>
    BottomRight,

    /// <summary>宿主左上角。胶囊向左边缘对齐，向下与向右增长。</summary>
    TopLeft,

    /// <summary>宿主左下角。胶囊向左边缘对齐，向上与向右增长。</summary>
    BottomLeft,
}

/// <summary>浮层的定位结果。</summary>
public readonly record struct OverlayPlacement(IntRect Bounds, OverlayAnchor Anchor)
{
    public bool IsVisible => !Bounds.IsEmpty;
}

/// <summary>
/// 计算浮层相对宿主窗口的位置。
/// </summary>
/// <remarks>
/// <para>
/// 纯几何运算，不涉及 Win32 调用，因此可以完整测试。
/// </para>
/// <para>
/// <b>输入输出均为设备无关单位（DIP），不是物理像素。</b>
/// 调用方负责把 Win32 给出的物理矩形换算为 DIP 后再传入。
/// 早期版本的文档误写为物理像素，而调用方传的是 DIP——
/// 在 150% 缩放下 margin 实际被当作 18 物理像素使用。
/// 行为本身是对的（换算在调用方完成），但契约描述有误，已更正。
/// </para>
/// <para>
/// 关于顶部内缩：Codex Desktop 是全屏无边框窗口，标题栏由应用自绘，
/// 系统指标 <c>SM_CYSMCAPTION</c> 给出的高度（实测 23 像素）并不等于
/// 其视觉标题栏高度。因此这里不用系统指标推算，而是接受调用方传入的
/// <c>topInset</c>，由配置决定，避免浮层压住窗口自身的按钮。
/// </para>
/// </remarks>
public static class OverlayPlacementCalculator
{
    /// <summary>默认的浮层与窗口边缘间距（DIP）。</summary>
    public const int DefaultMargin = 12;

    /// <summary>
    /// 默认的顶部内缩量（DIP），用于避开 Codex 自绘的标题栏。
    /// </summary>
    public const int DefaultTopInset = 48;

    /// <summary>偏移量的允许范围（DIP）。</summary>
    /// <remarks>
    /// 限制这不是为了保护布局，而是为了拦住损坏或手误的设置文件：
    /// 一个几十万像素的偏移会把浮层放到屏幕之外，用户会以为程序坏了。
    /// </remarks>
    public const int MaxOffset = 20_000;

    /// <summary>锚点是否贴近宿主右边缘。</summary>
    public static bool IsRightAnchored(OverlayAnchor anchor) =>
        anchor is OverlayAnchor.TopRight or OverlayAnchor.BottomRight;

    /// <summary>锚点是否贴近宿主上边缘。</summary>
    public static bool IsTopAnchored(OverlayAnchor anchor) =>
        anchor is OverlayAnchor.TopRight or OverlayAnchor.TopLeft;

    /// <summary>
    /// 计算浮层位置。
    /// </summary>
    /// <param name="hostBounds">宿主窗口矩形（DIP）。</param>
    /// <param name="overlayWidth">浮层宽度（DIP）。</param>
    /// <param name="overlayHeight">浮层高度（DIP）。</param>
    /// <param name="anchor">锚点位置。</param>
    /// <param name="margin">与窗口边缘的间距（DIP）。</param>
    /// <param name="topInset">
    /// 顶部内缩量（DIP），仅在贴顶的锚点生效。
    /// 为负值时使用 <see cref="DefaultTopInset"/>。
    /// </param>
    /// <param name="offsetX">
    /// 水平偏移（DIP）。正值表示**向宿主内部**移动，
    /// 因此贴右边缘时是向左，贴左边缘时是向右。
    /// </param>
    /// <param name="offsetY">垂直偏移（DIP）。正值同样表示向宿主内部移动。</param>
    public static OverlayPlacement Calculate(
        IntRect hostBounds,
        int overlayWidth,
        int overlayHeight,
        OverlayAnchor anchor = OverlayAnchor.TopRight,
        int margin = DefaultMargin,
        int topInset = -1,
        int offsetX = 0,
        int offsetY = 0)
    {
        if (hostBounds.IsEmpty || overlayWidth <= 0 || overlayHeight <= 0)
        {
            return new OverlayPlacement(IntRect.Empty, anchor);
        }

        if (margin < 0)
        {
            margin = DefaultMargin;
        }

        if (topInset < 0)
        {
            topInset = DefaultTopInset;
        }

        offsetX = Math.Clamp(offsetX, -MaxOffset, MaxOffset);
        offsetY = Math.Clamp(offsetY, -MaxOffset, MaxOffset);

        // 水平：贴右边缘时用「右边缘减去宽度」，贴左边缘时用「左边缘加上间距」。
        // 偏移量一律解释为「向宿主内部」，因此贴在右边缘时是减。
        var left = IsRightAnchored(anchor)
            ? hostBounds.Right - overlayWidth - margin - offsetX
            : hostBounds.Left + margin + offsetX;

        var top = IsTopAnchored(anchor)
            ? hostBounds.Top + topInset + offsetY
            : hostBounds.Bottom - overlayHeight - margin - offsetY;

        return new OverlayPlacement(
            ClampToHost(hostBounds, overlayWidth, overlayHeight, left, top, margin),
            anchor);
    }

    /// <summary>
    /// 由目标位置反推偏移量。
    /// </summary>
    /// <remarks>
    /// 拖动结束后用这个把「鼠标放下时的屏幕位置」转换回偏移量。
    /// 与 <see cref="Calculate"/> 严格互逆——两者共用同一组符号约定，
    /// 因此拖动后浮层会停在与放下时完全相同的位置，不会出现回弹。
    /// </remarks>
    /// <param name="hostBounds">宿主窗口矩形（DIP）。</param>
    /// <param name="overlayWidth">浮层宽度（DIP）。</param>
    /// <param name="overlayHeight">浮层高度（DIP）。</param>
    /// <param name="positionX">目标位置左上角 X（DIP）。</param>
    /// <param name="positionY">目标位置左上角 Y（DIP）。</param>
    /// <param name="anchor">锚点位置。</param>
    /// <param name="margin">与窗口边缘的间距（DIP）。</param>
    /// <param name="topInset">顶部内缩量（DIP）。</param>
    public static (int X, int Y) OffsetFromPosition(
        IntRect hostBounds,
        int overlayWidth,
        int overlayHeight,
        int positionX,
        int positionY,
        OverlayAnchor anchor,
        int margin = DefaultMargin,
        int topInset = DefaultTopInset)
    {
        if (margin < 0)
        {
            margin = DefaultMargin;
        }

        if (topInset < 0)
        {
            topInset = DefaultTopInset;
        }

        var offsetX = IsRightAnchored(anchor)
            ? hostBounds.Right - overlayWidth - margin - positionX
            : positionX - hostBounds.Left - margin;

        var offsetY = IsTopAnchored(anchor)
            ? positionY - hostBounds.Top - topInset
            : hostBounds.Bottom - overlayHeight - margin - positionY;

        return (
            Math.Clamp(offsetX, -MaxOffset, MaxOffset),
            Math.Clamp(offsetY, -MaxOffset, MaxOffset));
    }

    /// <summary>
    /// 把浮层收敛回宿主窗口内。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用户可以把浮层拖到窗口任意位置，但不能拖到看不见的地方——
    /// 那样它既没用也无法再次抓到。
    /// </para>
    /// <para>
    /// 若浮层比宿主还高或还宽（宿主极小化时可能出现），则贴左上角，
    /// 溢出部分由窗口裁剪处理，而不是产生负偏移。
    /// </para>
    /// </remarks>
    private static IntRect ClampToHost(
        IntRect hostBounds,
        int overlayWidth,
        int overlayHeight,
        int left,
        int top,
        int margin)
    {
        // 水平：优先保持在宿主内。
        var maxLeft = hostBounds.Right - overlayWidth;

        if (overlayWidth >= hostBounds.Width)
        {
            // 浮层比宿主宽：贴左边缘，而不是把它推到负偏移。
            left = hostBounds.Left;
        }
        else
        {
            left = Math.Clamp(left, hostBounds.Left, maxLeft);
        }

        // 垂直：贴顶的锚点允许紧贴顶边（此时 topInset 已被应用），
        // 贴底的锚点同理，因此下限直接用宿主顶边。
        var maxTop = hostBounds.Bottom - overlayHeight;

        if (overlayHeight >= hostBounds.Height)
        {
            top = hostBounds.Top;
        }
        else
        {
            top = Math.Clamp(top, hostBounds.Top, maxTop);
        }

        // margin 只在两端都要留出时才有意义；浮层几乎占满时忽略它，
        // 否则会出现「上限小于下限」的夹取错误。
        _ = margin;

        return new IntRect(left, top, left + overlayWidth, top + overlayHeight);
    }

    /// <summary>
    /// 判断浮层是否需要重新定位。
    /// </summary>
    /// <remarks>
    /// 用于避免每帧无谓地重设窗口位置：仅当宿主矩形真的变化时才移动。
    /// </remarks>
    public static bool NeedsReposition(IntRect currentHost, IntRect? lastHost) =>
        lastHost is null || currentHost != lastHost.Value;
}
