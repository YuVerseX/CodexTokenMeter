using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Formatting;
using CodexTokenMeter.Core.Settings;
using CodexTokenMeter.Core.Windowing;

// System.Windows.Shapes.Path（图标几何）与 System.IO.Path（路径操作）同名，
// 此处显式指定为文件系统版本，图标类型用完整限定名。
using IOPath = System.IO.Path;

// WinForms 与 WPF 存在同名类型，此处显式指定使用 WPF 版本。
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexTokenMeter.App;

/// <summary>
/// 浮层窗口：胶囊态与展开态。
/// </summary>
/// <remarks>
/// <para>
/// 窗口无边框、可穿透背景、不夺取焦点。位置由外部按宿主窗口矩形设置。
/// </para>
/// <para>
/// 不夺取焦点靠 <c>WS_EX_NOACTIVATE</c> 实现；点击胶囊展开面板时
/// 也不会把 Codex 输入框的焦点抢走。
/// </para>
/// </remarks>
public partial class OverlayWindow : Window
{
    /// <summary>上下文占用超过此比例时进度条转为警示色。</summary>
    private const double ContextWarnPercent = 80d;

    /// <summary>上下文圆环的半径与圆心，与 XAML 中的 18×18 网格匹配。</summary>
    private const double RingSize = 18d;
    private const double RingRadius = 6.8d;
    private const double RingCenter = 9d;
    private const double RingThickness = 2.2d;

    /// <summary>图标尺寸（DIP）。略小于圆环，让文字保持为主角。</summary>
    private const double IconSize = 14d;

    private bool _isPanelOpen;
    private bool _isDark = true;

    /// <summary>当前胶囊显示的指标。</summary>
    private IReadOnlyList<CapsuleMetric> _capsuleMetrics = CapsuleMetricCatalog.Default;

    /// <summary>最近一次收到的数据，供重建胶囊时复用。</summary>
    private OverlayData _lastData = OverlayData.Empty;

    /// <summary>上次构建胶囊时的内容签名，用于跳过重复渲染。</summary>
    private string? _capsuleSignature;

    /// <summary>当前渲染出的圆环，随指标配置变化。</summary>
    private readonly List<System.Windows.Shapes.Path> _contextRings = [];

    /// <summary>当前渲染出的费用文本，随指标配置变化。</summary>
    private readonly List<TextBlock> _costTexts = [];

    public OverlayWindow()
    {
        InitializeComponent();

        ApplyTheme(dark: true);
        Loaded += OnLoaded;

        // 窗口被隐藏时（用户切走 Codex、托盘隐藏）必须清理拖动状态：
        // 此时收不到 MouseUp，残留的捕获会让后续点击失效。
        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    /// <summary>当前是否展开面板。</summary>
    public bool IsPanelOpen => _isPanelOpen;

    /// <summary>
    /// 面板展开时点击到面板与胶囊之外的区域。
    /// </summary>
    /// <remarks>
    /// 由于浮层不夺取焦点，无法依靠失去激活事件判断；
    /// 改由 App 层的全局鼠标钩子监听，这里只接收结果。
    /// </remarks>
    public event EventHandler? OutsideClick;

    /// <summary>通知窗口收到了一次外部点击。</summary>
    internal void NotifyOutsideClick() => OutsideClick?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 应用设置。
    /// </summary>
    /// <remarks>
    /// 缩放作用于整个视觉树，因此文字、圆角、间距与内边距同步变化，
    /// 而不是只放大文字。
    /// </remarks>
    public void ApplySettings(Core.Settings.OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        RootPanel.LayoutTransform = Math.Abs(settings.Scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(settings.Scale, settings.Scale);

        // 胶囊指标变化会改变内容宽度。
        var metrics = settings.ResolvedCapsuleMetrics;

        if (!metrics.SequenceEqual(_capsuleMetrics))
        {
            _capsuleMetrics = metrics;
            BuildCapsule(_lastData);
        }

        // 缩放改变内容尺寸。
        RefreshSize();
    }

    /// <summary>
    /// 窗口的完整内容尺寸，含外层阴影预留。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不依赖 <c>ActualWidth</c>：窗口不再使用 <c>SizeToContent</c>，
    /// 而是由本属性驱动显式尺寸设置。
    /// </para>
    /// <para>
    /// 只测量 <c>RootPanel</c> 而不含外层 Grid 的 Margin，
    /// 因此还须手动加上阴影预留，否则右侧内容会被窗口边缘裁掉。
    /// </para>
    /// </remarks>
    public Size ContentSize
    {
        get
        {
            // 以无限约束测量，得到内容的自然尺寸（不受窗口当前尺寸限制）。
            RootPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var desired = RootPanel.DesiredSize;
            var scale = RootPanel.LayoutTransform is ScaleTransform transform
                ? transform.ScaleX
                : 1.0;

            // LayoutTransform 不参与 Measure 的结果，需手动乘上缩放。
            var contentWidth = desired.Width * scale;
            var contentHeight = desired.Height * scale;

            return new Size(
                contentWidth + ShadowLeft + ShadowRight,
                contentHeight + ShadowTop + ShadowBottom);
        }
    }

    /// <summary>阴影预留：左。</summary>
    internal const double ShadowLeft = 16;

    /// <summary>阴影预留：上。</summary>
    internal const double ShadowTop = 16;

    /// <summary>
    /// 阴影预留：右。
    /// </summary>
    /// <remarks>
    /// 比左侧多 4 像素：尺寸取整与 DPI 缩放会让内容边界落在亚像素位置，
    /// 实测右侧会比左侧少 1-2 像素，多留一点可避免文字或圆角被切。
    /// </remarks>
    internal const double ShadowRight = 20;

    /// <summary>阴影预留：下。</summary>
    internal const double ShadowBottom = 16;

    /// <summary>
    /// 按当前内容重新设置窗口尺寸。
    /// </summary>
    /// <remarks>
    /// 内容变化（数据刷新、面板展开/收起、主题切换、缩放变更）后调用。
    /// 尺寸未变时不写值，避免无效的布局失效与重定位通知。
    /// </remarks>
    public void RefreshSize()
    {
        var size = ContentSize;

        var targetWidth = Math.Ceiling(size.Width);
        var targetHeight = Math.Ceiling(size.Height);

        if (Math.Abs(Width - targetWidth) <= 0.5 && Math.Abs(Height - targetHeight) <= 0.5)
        {
            return;
        }

        _suppressReposition = true;

        try
        {
            Width = targetWidth;
            Height = targetHeight;
        }
        finally
        {
            _suppressReposition = false;
        }

        // 尺寸已变，位置需重算；在抑制块外发一次通知。
        RequestReposition?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按系统主题切换配色。</summary>
    public void ApplyTheme(bool dark)
    {
        _isDark = dark;

        var source = new Uri(
            dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
            UriKind.Relative);

        var dictionary = new ResourceDictionary { Source = source };

        // 替换主题字典，保留其它资源。
        var merged = Resources.MergedDictionaries;
        if (merged.Count > 0 && merged[0].Source?.OriginalString.Contains("Themes/") == true)
        {
            merged[0] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        // 单独设置的画刷不会随 DynamicResource 更新，需要重新应用。
        ReapplyThemeBrushes();

        // 主题可能改变字体度量与边框宽度，进而改变内容尺寸。
        RefreshSize();
    }

    /// <summary>更新展示数据。</summary>
    public void UpdateData(OverlayData data)
    {
        _lastData = data;
        BuildCapsule(data);

        // ── 展开态 ──
        ProjectLabel.Text = ProjectResolver.Resolve(data.WorkingDirectory).Name ?? "—";
        ModelLabel.Text = data.Model ?? "未知模型";

        var duration = data.ActiveDuration;
        DurationLabel.Text = duration > TimeSpan.Zero
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : string.Empty;

        // 一个 turn 可能包含几十次调用，只写「本轮」容易被误读为「最近一次调用」。
        CurrentTurnHeader.Text = data.CurrentTurnCalls > 1
            ? $"本轮花费 · {NumberFormatter.Count(data.CurrentTurnCalls)} 次调用"
            : "本轮花费";

        TurnCost.Text = data.CurrentTurnCost is { } tc
            ? $"${NumberFormatter.Cost(tc)}"
            : "—";

        // 摘要行给精确值：胶囊里的紧凑格式（如 1.1M）无法反映真实量级。
        TurnSummary.Text =
            $"{NumberFormatter.Full(data.CurrentTurn.Total)} tokens" +
            $" · {NumberFormatter.Full(data.CurrentTurn.Input)} 输入" +
            $" · {NumberFormatter.Full(data.CurrentTurn.Output)} 输出";

        TurnInput.Text = NumberFormatter.Full(data.CurrentTurn.Input);
        TurnOutput.Text = NumberFormatter.Full(data.CurrentTurn.Output);

        TotalHeader.Text = data.CompactionCount > 0
            ? $"会话累计 · 压缩 {NumberFormatter.Count(data.CompactionCount)} 次"
            : "会话累计";

        // 展开面板用精确值：紧凑格式会让相邻数字看起来一样。
        TotalInput.Text = NumberFormatter.Full(data.Cumulative.Input);
        TotalOutput.Text = NumberFormatter.Full(data.Cumulative.Output);
        TotalCacheRead.Text = NumberFormatter.Full(data.Cumulative.CachedInput);
        TotalTokens.Text = NumberFormatter.Full(data.Cumulative.Total);
        TotalCost.Text = data.TotalCost is { } cost
            ? $"${NumberFormatter.Cost(cost)}"
            : "—";
        CacheHitRate.Text = $"{data.CacheHitRate:F1}%";

        ContextDetail.Text = data.ContextWindowTokens > 0
            ? $"{data.ContextPercent:F1}% / {NumberFormatter.ContextWindow(data.ContextWindowTokens)}"
            : "—";

        MessageDetail.Text =
            $"{NumberFormatter.Count(data.UserMessageCount)}"
            + $" / {NumberFormatter.Count(data.AssistantMessageCount)}";

        ToolDetail.Text = NumberFormatter.Count(data.ToolCallCount);

        UpdateNotice(data);
        UpdateCostBrush(data);
        UpdateContextBar(data.ContextPercent);

        // 数值位数变化会改变内容宽度；不重算尺寸会导致文字被窗口边缘裁掉。
        RefreshSize();
    }

    /// <summary>展开或折叠面板。</summary>
    public void SetPanelOpen(bool open)
    {
        if (_isPanelOpen == open)
        {
            return;
        }

        _isPanelOpen = open;
        PanelBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // 面板显隐改变内容尺寸，先重算窗口尺寸，外部才能拿到正确值。
        RefreshSize();

        // 展开时淡入并轻微上移，避免突兀。
        if (open)
        {
            PanelBorder.Opacity = 0;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            PanelBorder.BeginAnimation(OpacityProperty, fade);
        }

        // 尺寸变化后通知外部重新定位。
        RequestReposition?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>展开面板的实际高度，供诊断使用。</summary>
    internal double PanelHeight => PanelBorder.ActualHeight;

    /// <summary>请求外部重新计算窗口位置。面板展开或收起时触发。</summary>
    public event EventHandler? RequestReposition;

    /// <summary>
    /// 判断当前尺寸是否与给定尺寸一致（容忍取整误差）。
    /// </summary>
    /// <remarks>
    /// 供外部判断“尺寸真的变了”从而避免无谓的重定位。
    /// </remarks>
    public bool SizeMatches(Size other) =>
        Math.Abs(ContentSize.Width - other.Width) <= 1.0
        && Math.Abs(ContentSize.Height - other.Height) <= 1.0;

    /// <summary>
    /// 把物理像素坐标换算为设备无关单位（DIP）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Win32 的窗口矩形是**物理像素**，而 WPF 的 <c>Left/Top/Width/Height</c>
    /// 是**设备无关单位**。在非 100% 缩放的显示器上两者相差一个缩放系数，
    /// 混用会把窗口摆到屏幕之外。
    /// </para>
    /// <para>
    /// 实机踩到：150% 缩放下，按物理像素算出的 x=3404 被当作 DIP 设置，
    /// 实际绘制到物理 x=5106，而屏幕只有 3840 宽，浮层完全不可见。
    /// </para>
    /// </remarks>
    public Point DeviceToDip(double deviceX, double deviceY)
    {
        var scale = DeviceScale;

        return Math.Abs(scale - 1.0) < 0.001
            ? new Point(deviceX, deviceY)
            : new Point(deviceX / scale, deviceY / scale);
    }

    /// <summary>
    /// 把物理像素矩形换算为设备无关单位（DIP）。
    /// </summary>
    /// <remarks>
    /// 向左上取整、向右下取整，保证换算后的矩形完全覆盖原矩形，
    /// 避免因取整而让浮层离开宿主边界。
    /// </remarks>
    public IntRect DeviceToDip(IntRect deviceRect)
    {
        var scale = DeviceScale;

        if (Math.Abs(scale - 1.0) < 0.001)
        {
            return deviceRect;
        }

        return new IntRect(
            (int)Math.Floor(deviceRect.Left / scale),
            (int)Math.Floor(deviceRect.Top / scale),
            (int)Math.Ceiling(deviceRect.Right / scale),
            (int)Math.Ceiling(deviceRect.Bottom / scale));
    }

    /// <summary>当前显示器相对 96 DPI 的缩放系数。</summary>
    public double DeviceScale
    {
        get
        {
            var source = PresentationSource.FromVisual(this);

            if (source?.CompositionTarget is not { } target)
            {
                return 1.0;
            }

            // 变换矩阵的 M11 即水平缩放系数。
            return target.TransformToDevice.M11;
        }
    }

    /// <summary>
    /// 判断给定屏幕坐标是否落在窗口可见区域内。
    /// </summary>
    /// <remarks>
    /// 用于外部点击检测：窗口矩形包含透明阴影边距，
    /// 点击阴影区也应视为点击外部。
    /// </remarks>
    public bool ContainsScreenPoint(double screenX, double screenY)
    {
        var left = Left + ShadowLeft;
        var top = Top + ShadowTop;
        var size = ContentSize;

        return screenX >= left
            && screenY >= top
            && screenX <= left + size.Width - ShadowLeft - ShadowRight
            && screenY <= top + size.Height - ShadowTop - ShadowBottom;
    }

    /// <summary>切换展开状态。</summary>
    public void TogglePanel() => SetPanelOpen(!_isPanelOpen);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // 无焦点、不占任务栏，且胶囊本身需要接收点击，因此不整体穿透。
        NativeWindow.ApplyOverlayStyles(handle, clickThrough: false);
        NativeWindow.ApplyDarkMode(handle, _isDark);

        // 让窗口不进入 Alt+Tab 列表。
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowHook);

        // 首次布局前先定好尺寸，否则窗口会以默认尺寸闪现一帧。
        RefreshSize();

        // 布局完成后内容尺寸才可靠，此时才淡入，避免定位前的闪跳。
        Opacity = 0;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                RefreshSize();
                Opacity = 1;
                RequestReposition?.Invoke(this, EventArgs.Empty);
            });
    }

    /// <summary>
    /// 窗口尺寸变化时请求外部重新定位。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内容变化会改变窗口尺寸，而位置是按尺寸算出来的，
    /// 因此尺寸一变就必须重新定位，否则窗口会停在旧位置而溢出宿主。
    /// </para>
    /// <para>
    /// <see cref="RefreshSize"/> 自身会写 <c>Width/Height</c>，
    /// 从而递归进入这里；由它设置 <c>_suppressReposition</c> 断开循环。
    /// 调用方（<see cref="RefreshSize"/> 之后）负责发一次通知。
    /// </para>
    /// </remarks>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_suppressReposition)
        {
            return;
        }

        RequestReposition?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>为 true 时抑制尺寸变化引发的重定位通知。</summary>
    private bool _suppressReposition;

    /// <summary>
    /// 拦截激活消息，确保点击浮层不会把焦点从 Codex 抢走。
    /// </summary>
    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x0021;
        const int WmNcHitTest = 0x0084;
        const int HtClient = 1;

        if (message == WmMouseActivate)
        {
            // MA_NOACTIVATE：允许鼠标交互但不激活窗口。
            handled = true;
            return 3;
        }

        if (message == WmNcHitTest)
        {
            handled = true;
            return HtClient;
        }

        return 0;
    }

    /// <summary>
    /// 胶囊上的鼠标按下：开启拖动或单击展开。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不用 WPF 的 <c>DragMove</c>：它内部调用 <c>SendMessage(WM_NCLBUTTONDOWN)</c>，
    /// 会让窗口进入系统的拖动模态循环，期间会抢走前台并阻塞消息泵。
    /// 对不夺取焦点的浮层而言不可接受。
    /// </para>
    /// <para>
    /// 改为自己跟鼠标：按下时记录起点，移动超过阈值才认定为拖动，
    /// 松开时若未超过阈值则当作单击（展开面板）。
    /// 这样拖动与点击不会互相干扰。
    /// </para>
    /// </remarks>
    private void OnCapsuleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_allowDrag || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragOriginDevice = NativeWindow.GetCursorPosition();
        _dragOriginWindow = new Point(Left, Top);
        _dragActive = false;
        _dragStarted = true;

        // 捕获鼠标：即使指针移出胶囊（拖得快时很常见）也能收到移动与松开。
        CapsuleBorder.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>拖动中。</summary>
    private void OnCapsuleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragStarted || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = NativeWindow.GetCursorPosition();
        var scale = DeviceScale;

        // 鼠标坐标是物理像素，窗口坐标是 DIP；换算后才能相加。
        var deltaX = (cursor.X - _dragOriginDevice.X) / scale;
        var deltaY = (cursor.Y - _dragOriginDevice.Y) / scale;

        // 超过阈值才认定为拖动，否则手抖会把单击变成微移。
        if (!_dragActive)
        {
            if (Math.Abs(deltaX) < DragThreshold && Math.Abs(deltaY) < DragThreshold)
            {
                return;
            }

            _dragActive = true;
        }

        Left = _dragOriginWindow.X + deltaX;
        Top = _dragOriginWindow.Y + deltaY;
    }

    /// <summary>拖动静止：提交新位置或当作单击。</summary>
    private void OnCapsuleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragStarted)
        {
            return;
        }

        _dragStarted = false;
        CapsuleBorder.ReleaseMouseCapture();

        if (_dragActive)
        {
            _dragActive = false;

            // 拖动结束后把屏幕位置换算回「相对宿主的偏移量」并持久化。
            PositionDragged?.Invoke(this, (Left, Top));
            e.Handled = true;
            return;
        }

        // 未移动：视为单击，展开或收起面板。
        TogglePanel();
        e.Handled = true;
    }

    /// <summary>
    /// 用户把浮层拖到了新位置。
    /// </summary>
    /// <remarks>
    /// 参数为窗口左上角的 DIP 坐标。上层负责换算为相对宿主的偏移量并保存。
    /// </remarks>
    public event EventHandler<(double X, double Y)>? PositionDragged;

    /// <summary>是否允许拖动。</summary>
    public bool AllowDrag
    {
        get => _allowDrag;
        set => _allowDrag = value;
    }

    /// <summary>
    /// 把浮层移动到指定屏幕位置，走与真实拖动相同的处理路径。
    /// </summary>
    /// <remarks>
    /// 供 <c>--drag</c> 诊断参数使用。
    /// 不直接设 <c>Left/Top</c> 就了事：那会绕过 <see cref="PositionDragged"/>，
    /// 验证不到「屏幕位置 → 偏移量 → 持久化」这条链路。
    /// </remarks>
    internal void SimulateDragTo(double x, double y)
    {
        Left = x;
        Top = y;

        PositionDragged?.Invoke(this, (x, y));
    }

    private void OnCapsuleClick(object sender, MouseButtonEventArgs e)
    {
        // 单击处理已移到 MouseUp（需先区分拖动与单击），此处不再切换面板。
        e.Handled = true;
    }

    /// <summary>构造从顶部顺时针扫过指定百分比的圆弧。</summary>
    private static PathGeometry BuildArcGeometry(double percent)
    {
        var sweepDegrees = Math.Clamp(percent, 0, 100) / 100d * 360d;

        // 满圆无法用单条弧线表示，收窄一点避免退化。
        if (sweepDegrees >= 359.9)
        {
            sweepDegrees = 359.9;
        }

        var startAngle = -90d;
        var endAngle = startAngle + sweepDegrees;

        var start = PointOnCircle(startAngle);
        var end = PointOnCircle(endAngle);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(RingRadius, RingRadius),
            IsLargeArc = sweepDegrees > 180d,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            RingCenter + (RingRadius * Math.Cos(radians)),
            RingCenter + (RingRadius * Math.Sin(radians)));
    }

    /// <summary>是否允许拖动浮层。</summary>
    private bool _allowDrag = true;

    /// <summary>按下时的鼠标位置（物理像素）。</summary>
    private (int X, int Y) _dragOriginDevice;

    /// <summary>按下时的窗口位置（DIP）。</summary>
    private Point _dragOriginWindow;

    /// <summary>本次按下是否已进入拖动状态。</summary>
    private bool _dragActive;

    /// <summary>是否有未结束的按下。</summary>
    private bool _dragStarted;

    /// <summary>认定为拖动所需的最小位移（DIP）。</summary>
    private const double DragThreshold = 3d;

    /// <summary>
    /// 中断进行中的拖动（不提交位置）。
    /// </summary>
    /// <remarks>
    /// 拖动中窗口被隐藏（用户切走 Codex、托盘隐藏）时收不到 <c>MouseUp</c>，
    /// 拖动状态与鼠标捕获会残留下来：下一次点击会被误判为“正在拖动中”，
    /// 而捕获中的鼠标会让其它控件的点击失效。
    /// 隐藏窗口与丢失捕获时都必须清理。
    /// </remarks>
    private void CancelDrag()
    {
        if (!_dragStarted)
        {
            return;
        }

        _dragStarted = false;
        _dragActive = false;

        if (CapsuleBorder.IsMouseCaptured)
        {
            CapsuleBorder.ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// 鼠标捕获意外丢失时的清理。
    /// </summary>
    /// <remarks>
    /// 系统在窗口失活、弹出模态对话框、切换用户等情况下会直接释放捕获，
    /// 此时不会有 <c>MouseUp</c>。不清理会留下“永远在拖动”的假状态。
    /// </remarks>
    private void OnCapsuleLostMouseCapture(object sender, MouseEventArgs e) => CancelDrag();

    /// <summary>
    /// 窗口隐藏时清理拖动状态。
    /// </summary>
    /// <remarks>
    /// 用事件而不是重写 <c>OnIsVisibleChanged</c>：
    /// 后者在 <see cref="Window"/> 上不是可重写的虚方法。
    /// </remarks>
    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CancelDrag();
        }
    }

    /// <summary>上下文超阈值时改用警示色。</summary>
    private Brush ContextBrush(double percent) => percent >= ContextWarnPercent
        ? (Brush)FindResource("CostWarnBrush")
        : (Brush)FindResource("AccentBrush");

    private void UpdateContextBar(double percent)
    {
        var track = ContextBarFill.Parent as Border;
        var available = track?.ActualWidth ?? 0;

        if (available <= 0)
        {
            return;
        }

        var target = Math.Clamp(percent, 0, 100) / 100d * available;

        // 用宽度动画而不是直接赋值，让变化可见但不突兀。
        ContextBarFill.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        ContextBarFill.Background = ContextBrush(percent);
    }

    /// <summary>
    /// 按选中指标重建胶囊内容。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 完全重建而不是复用元素：指标组合是任意的，
    /// 复用需要一套繁琐的回收逻辑，而胶囊元素只有几个，
    /// 重建的开销远小于维护复用逻辑的复杂度。
    /// </para>
    /// <para>
    /// 仅在数据或指标真正变化时重建。轮询间隔为 700ms，
    /// 每轮都清空重建会产生不必要的布局开销与视觉闪烁。
    /// </para>
    /// </remarks>
    private void BuildCapsule(OverlayData data)
    {
        // 计算签名以跳过重复渲染。
        var signature = string.Join('|',
            _capsuleMetrics.Select(metric => CapsuleMetricCatalog.ToKey(metric)))
            + "#" + data.ThreadId
            + "#" + data.CurrentTurn
            + "#" + data.Cumulative
            + "#" + data.CurrentTurnCost
            + "#" + data.TotalCost
            + "#" + data.ContextUsedTokens
            + "#" + data.ContextWindowTokens
            + "#" + data.UserMessageCount
            + "#" + data.AssistantMessageCount
            + "#" + data.ToolCallCount
            + "#" + data.Model
            + "#" + data.WorkingDirectory
            + "#" + data.ActiveDuration
            + "#" + data.IsConnected;

        if (signature == _capsuleSignature)
        {
            return;
        }

        _capsuleSignature = signature;

        CapsuleContent.Children.Clear();
        _contextRings.Clear();
        _costTexts.Clear();

        var groups = CapsulePresenter.Build(data, _capsuleMetrics);

        foreach (var group in groups)
        {
            // 分组之间插入细竖线：无文字说明也能看出哪几项是一类。
            if (group.PrecededByDivider)
            {
                CapsuleContent.Children.Add(new Border
                {
                    Width = 1,
                    Height = 14,
                    Margin = new Thickness(13, 0, 13, 0),
                    Background = (Brush)FindResource("SurfaceBorderStrongBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var isFirstInGroup = true;

            foreach (var item in group.Items)
            {
                CapsuleContent.Children.Add(CreateCapsuleItem(item, isFirstInGroup, data));
                isFirstInGroup = false;
            }
        }

        // 未连接时在末尾追加状态提示。
        if (!data.IsConnected)
        {
            CapsuleContent.Children.Add(new Border
            {
                Width = 1,
                Height = 14,
                Margin = new Thickness(13, 0, 13, 0),
                Background = (Brush)FindResource("SurfaceBorderStrongBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            CapsuleContent.Children.Add(new TextBlock
            {
                Text = "Codex 未连接",
                FontFamily = (FontFamily)FindResource("LabelFont"),
                FontSize = (double)FindResource("FontSizeCaption"),
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        ApplyThemeBrushesToCapsule();
    }

    /// <summary>
    /// 构建胶囊中的一项。
    /// </summary>
    /// <remarks>
    /// 需要读取主题资源（FindResource），因此是实例方法。
    /// </remarks>
    private StackPanel CreateCapsuleItem(CapsuleItem item, bool isFirst, OverlayData data)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isFirst ? new Thickness(0) : new Thickness(15, 0, 0, 0),
        };

        // 上下文占用用圆环呈现，比一个孤立的百分比数字直观得多。
        if (item.RingPercent is { } percent)
        {
            var ring = new Grid
            {
                Width = RingSize,
                Height = RingSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            ring.Children.Add(new Ellipse
            {
                Stroke = (Brush)FindResource("TrackBrush"),
                StrokeThickness = RingThickness,
            });

            var arc = new System.Windows.Shapes.Path
            {
                StrokeThickness = RingThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = percent > 0 ? BuildArcGeometry(percent) : null,
                Stroke = ContextBrush(percent),
            };

            ring.Children.Add(arc);
            _contextRings.Add(arc);
            panel.Children.Add(ring);
        }
        else if (CapsuleIcons.Get(item.IconKey) is { } geometry)
        {
            // 用 Viewbox 包住固定 16×16 的 Canvas。
            //
            // 不能直接给 Path 设 Stretch=Uniform：那会按**该路径自身的边界**
            // 缩放，而各图标的边界差异很大（dollar 6.4×11.6、terminal 10.8×7.4），
            // 结果是一排图标视觉尺寸参差不齐。
            // Canvas 提供稳定的 16×16 视口，Viewbox 再把它整体缩到目标尺寸，
            // 这样所有图标的相对大小与设计时一致。
            var viewport = new Canvas { Width = 16, Height = 16 };

            viewport.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(geometry),
                Stroke = (Brush)FindResource("TextMutedBrush"),
                StrokeThickness = 1.55,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });

            panel.Children.Add(new Viewbox
            {
                Width = IconSize,
                Height = IconSize,
                Child = viewport,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var text = new TextBlock
        {
            Text = item.Text,
            FontFamily = (FontFamily)FindResource("NumericFont"),
            Foreground = item.IsCost
                ? (Brush)FindResource(data.IsPartial || data.IsUnpriced ? "CostWarnBrush" : "CostBrush")
                : (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,

            // 费用是关注重点，略大且加粗；其余指标保持一致。
            FontSize = item.IsCost ? 13d : 12.5d,
            FontWeight = item.IsCost ? FontWeights.SemiBold : FontWeights.Normal,
        };

        if (item.IconKey is not null || item.RingPercent is not null)
        {
            text.Margin = new Thickness(7, 0, 0, 0);
        }

        if (item.IsCost)
        {
            _costTexts.Add(text);
        }

        panel.Children.Add(text);

        // 指标多时图标存在歧义，悬停提示给出完整名称与精确值。
        if (item.Tooltip is { } tooltip)
        {
            panel.ToolTip = tooltip;
        }

        return panel;
    }

    /// <summary>重建后重新应用随主题变化的画刷。</summary>
    private void ApplyThemeBrushesToCapsule()
    {
        foreach (var ring in _contextRings)
        {
            if (ring.Data is not null)
            {
                ring.Stroke = ring.Stroke is SolidColorBrush current
                    && current.Color == ((SolidColorBrush)FindResource("CostWarnBrush")).Color
                        ? (Brush)FindResource("CostWarnBrush")
                        : (Brush)FindResource("AccentBrush");
            }
        }
    }

    private void UpdateNotice(OverlayData data)
    {
        var notice = data switch
        {
            // 价格表问题优先展示：它会让所有费用数字不可信，
            // 比“未定价”或“数据不完整”更严重。
            { PricingWarning: { } pricing } => pricing,
            { IsUnpriced: true, Model: { } model } => $"该模型未收录价格，费用不可用：{model}",
            { IsPartial: true } => "日志尚未读完，数值可能偏低",
            { IsConnected: false } => "未连接 Codex，显示的是最后一次已知数据",
            _ => null,
        };

        NoticeLabel.Text = notice ?? string.Empty;
        NoticeLabel.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCostBrush(OverlayData data)
    {
        // 数据不完整或未定价时用警示色，避免把估算值当作准确值。
        var brush = data.IsPartial || data.IsUnpriced
            ? (Brush)FindResource("CostWarnBrush")
            : (Brush)FindResource("CostBrush");

        // 费用文本是动态生成的，这里直接重上色。
        foreach (var text in _costTexts)
        {
            text.Foreground = brush;
        }

        TotalCost.Foreground = brush;
        TurnCost.Foreground = brush;
    }

    private void ReapplyThemeBrushes()
    {
        // 胶囊内容是按数据动态生成的，无法靠 DynamicResource 自动跟随主题，
        // 因此这里重画一次。
        _capsuleSignature = null;
        BuildCapsule(_lastData);
    }
}

