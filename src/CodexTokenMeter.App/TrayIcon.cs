using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CodexTokenMeter.Core.Settings;
using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.App;

/// <summary>
/// 系统托盘图标与菜单。
/// </summary>
/// <remarks>
/// <para>
/// 使用 WinForms 的 <see cref="NotifyIcon"/>：WPF 没有内置托盘 API，
/// 而 WinForms 已随 WindowsDesktop 框架提供，不引入额外依赖。
/// </para>
/// <para>
/// 托盘图标是浮层唯一的常驻入口：浮层本身不占任务栏，
/// 用户从托盘控制显示、位置与退出。
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _positionMenu;
    private readonly ToolStripMenuItem _metricMenu;
    private readonly Dictionary<CapsuleMetric, ToolStripMenuItem> _metricItems = [];

    /// <summary>请求切换浮层显隐。</summary>
    internal event EventHandler? ToggleVisibilityRequested;

    /// <summary>请求切换面板展开状态。</summary>
    internal event EventHandler? TogglePanelRequested;

    /// <summary>请求切换某个胶囊指标的显隐。</summary>
    internal event EventHandler<CapsuleMetric>? CapsuleMetricToggled;

    /// <summary>请求打开设置文件所在目录。</summary>
    internal event EventHandler? OpenSettingsRequested;

    /// <summary>请求退出应用。</summary>
    internal event EventHandler? ExitRequested;

    internal TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(BuildHeader());
        menu.Items.Add(new ToolStripSeparator());

        var toggle = new ToolStripMenuItem("显示 / 隐藏", null, (_, _) =>
            ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty))
        {
            // 便于用户理解快捷键式的行为。
            ShortcutKeyDisplayString = "点击图标",
        };
        menu.Items.Add(toggle);

        menu.Items.Add(new ToolStripMenuItem("展开 / 收起面板", null, (_, _) =>
            TogglePanelRequested?.Invoke(this, EventArgs.Empty)));

        menu.Items.Add(new ToolStripSeparator());

        _metricMenu = BuildMetricMenu();
        menu.Items.Add(_metricMenu);

        _positionMenu = BuildPositionMenu();
        menu.Items.Add(_positionMenu);

        menu.Items.Add(new ToolStripMenuItem("打开设置文件位置", null, (_, _) =>
            OpenSettingsRequested?.Invoke(this, EventArgs.Empty)));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) =>
            ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Codex Token Meter",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 双击图标等同于切换显隐，符合托盘惯例。
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新悬停提示，展示当前会话摘要。</summary>
    internal void UpdateTooltip(string? summary)
    {
        // NotifyIcon.Text 有 63 字符上限，超出会抛异常。
        var text = string.IsNullOrWhiteSpace(summary)
            ? "Codex Token Meter"
            : $"Codex Token Meter\n{summary}";

        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
    }

    /// <summary>按当前设置更新菜单的选中项。</summary>
    internal void SyncSettings(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (ToolStripItem item in _positionMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is OverlayAnchor anchor)
            {
                menuItem.Checked = anchor == settings.Anchor;
            }
        }

        var selected = settings.ResolvedCapsuleMetrics.ToHashSet();

        foreach (var (metric, item) in _metricItems)
        {
            var isChecked = selected.Contains(metric);

            item.Checked = isChecked;

            // 未选中且已达上限时置灰，让“为什么点不动”有可见的理由。
            item.Enabled = isChecked || selected.Count < MaxVisibleMetrics;
        }

        AllowDrag = settings.AllowDrag;
    }

    /// <summary>
    /// 胶囊里最多同时显示的指标数。
    /// </summary>
    /// <remarks>
    /// 不设上限的话，把 18 项全部勾上会让胶囊宽到遮住 Codex 的标题栏按钮，
    /// 而浮层存在的意义是不干扰工作。
    /// </remarks>
    internal const int MaxVisibleMetrics = 8;

    /// <summary>所在屏幕上的锚点选择。</summary>
    internal event EventHandler<OverlayAnchor>? AnchorChanged;

    public void Dispose()
    {
        // 必须先隐藏再释放，否则图标会残留在通知区域直到鼠标划过。
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private static ToolStripMenuItem BuildHeader()
    {
        var header = new ToolStripMenuItem("Codex Token Meter")
        {
            Enabled = false,
        };

        // SystemFonts.MenuFont 在极端环境下可能为 null；
        // 为空时保留默认字体，不强行加粗。
        if (SystemFonts.MenuFont is { } menuFont)
        {
            header.Font = new Font(menuFont, FontStyle.Bold);
        }

        return header;
    }

    private ToolStripMenuItem BuildMetricMenu()
    {
        var menu = new ToolStripMenuItem("胶囊显示指标");

        // 按分组呈现而不是平铺 18 项：用户找的是“上下文”而不是
        // “contextPercent”，分组能大幅缩短定位时间。
        var byGroup = CapsuleMetricCatalog.Ordered
            .GroupBy(CapsuleMetricCatalog.GetGroup)
            .ToList();

        for (var index = 0; index < byGroup.Count; index++)
        {
            if (index > 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var metric in byGroup[index])
            {
                var item = new ToolStripMenuItem(
                    CapsuleMetricCatalog.GetDisplayName(metric),
                    null,
                    (_, _) => CapsuleMetricToggled?.Invoke(this, metric))
                {
                    Tag = metric,
                    ToolTipText = CapsuleMetricCatalog.GetDescription(metric),
                };

                _metricItems[metric] = item;
                menu.DropDownItems.Add(item);
            }
        }

        menu.DropDownItems.Add(new ToolStripSeparator());

        menu.DropDownItems.Add(new ToolStripMenuItem("恢复默认", null, (_, _) =>
        {
            // 这一项会一次性改写整份列表，点完就关菜单。
            _allowClose = true;
            RestoreDefaultMetricsRequested?.Invoke(this, EventArgs.Empty);
        }));

        // 保持菜单打开，让用户可以连续勾选多项。
        //
        // WinForms 默认在点击菜单项后关闭整条菜单链，
        // 于是每改一项都要重新右键展开，对一个 18 项、常需组合调整的
        // 列表而言体验很差。
        menu.DropDown.Closing += OnMetricDropDownClosing;

        // 每次打开菜单都清掉“允许关闭”标志，避免它跨会话残留：
        // 用户点了「恢复默认」后没让 Closing 触发就点到别处时，
        // 标志会留着，使下一次的普通勾选意外关闭菜单。
        menu.DropDown.Opened += (_, _) => _allowClose = false;

        return menu;
    }

    /// <summary>
    /// 决定「胶囊显示指标」子菜单点击后是否保持打开。
    /// </summary>
    /// <remarks>
    /// 只拦 <see cref="ToolStripDropDownCloseReason.ItemClicked"/>：
    /// 点到别处、按 Esc、窗口失活等仍应正常关闭，否则菜单会“粘”在屏幕上。
    /// 「恢复默认」也不拦——它一次改写整份列表，没有继续勾选的需求。
    /// </remarks>
    private void OnMetricDropDownClosing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        // 判定规则在 Core 中实现以便单元测试（菜单无法在测试里真实点击）。
        //
        // 只拦「点击菜单项」：点到别处、按 Esc、窗口失活仍应正常关闭，
        // 否则菜单会“粘”在屏幕上。
        if (!TrayMenuRules.ShouldKeepMenuOpen(
            e.CloseReason == ToolStripDropDownCloseReason.ItemClicked,
            _allowClose))
        {
            return;
        }

        e.Cancel = true;

        // 取消关闭后，勾选框不会自动重绘，需要手动刷新。
        _metricMenu.DropDown.Refresh();
    }

    /// <summary>为 true 时，本次点击允许菜单正常关闭。</summary>
    private bool _allowClose;

    /// <summary>请求把胶囊指标恢复为默认集合。</summary>
    internal event EventHandler? RestoreDefaultMetricsRequested;

    private ToolStripMenuItem BuildPositionMenu()
    {
        var menu = new ToolStripMenuItem("浮层位置");

        foreach (var (anchor, label) in new[]
        {
            (OverlayAnchor.TopRight, "窗口右上角"),
            (OverlayAnchor.TopLeft, "窗口左上角"),
            (OverlayAnchor.BottomRight, "窗口右下角"),
            (OverlayAnchor.BottomLeft, "窗口左下角"),
        })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) =>
                AnchorChanged?.Invoke(this, anchor))
            {
                Tag = anchor,
            };

            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());

        // 拖动会把位置变成偏移量，用户需要一个回到默认位置的入口。
        menu.DropDownItems.Add(new ToolStripMenuItem("恢复默认位置", null, (_, _) =>
            PositionResetRequested?.Invoke(this, EventArgs.Empty)));

        var dragItem = new ToolStripMenuItem("允许拖动", null, (_, _) =>
            DragToggled?.Invoke(this, EventArgs.Empty));

        _allowDragItem = dragItem;
        menu.DropDownItems.Add(dragItem);

        return menu;
    }

    /// <summary>是否允许拖动浮层的菜单项。</summary>
    private ToolStripMenuItem? _allowDragItem;

    /// <summary>是否允许拖动浮层。</summary>
    internal bool AllowDrag
    {
        get => _allowDragItem?.Checked ?? true;
        set
        {
            if (_allowDragItem is not null)
            {
                _allowDragItem.Checked = value;
            }
        }
    }

    /// <summary>请求重置浮层位置。</summary>
    internal event EventHandler? PositionResetRequested;

    /// <summary>请求切换是否允许拖动。</summary>
    internal event EventHandler? DragToggled;

    /// <summary>
    /// 程序化生成托盘图标。
    /// </summary>
    /// <remarks>
    /// 不依赖外部图标文件：从嵌入资源加载会增加打包复杂度，
    /// 而这里画出的图形足够识别，且任意 DPI 下都清晰。
    /// </remarks>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(255, 26, 29, 35));
        graphics.FillEllipse(background, 1, 1, 30, 30);

        // 上下箭头：直观表示 token 的输入与输出。
        using var accent = new Pen(Color.FromArgb(255, 91, 156, 248), 2.4f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };

        graphics.DrawLine(accent, 12, 21, 12, 10);
        graphics.DrawLine(accent, 8, 14, 12, 10);
        graphics.DrawLine(accent, 16, 14, 12, 10);

        using var cost = new Pen(Color.FromArgb(255, 91, 214, 160), 2.4f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };

        graphics.DrawLine(cost, 21, 11, 21, 22);

        var handle = bitmap.GetHicon();

        try
        {
            // 从句柄克隆出独立图标，以便随后安全释放原生资源。
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
