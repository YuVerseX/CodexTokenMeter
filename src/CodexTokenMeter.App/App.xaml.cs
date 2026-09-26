using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Formatting;
using CodexTokenMeter.Core.Pricing;
using CodexTokenMeter.Core.Settings;
using CodexTokenMeter.Core.Windowing;

// WinForms 与 WPF 存在同名类型，此处显式指定使用 WPF 版本。
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace CodexTokenMeter.App;

/// <summary>应用入口与窗口跟随循环。</summary>
/// <remarks>
/// 本类持有多个可释放资源（鼠标钩子、托盘图标、数据提供者、单实例互斥体），
/// 但它们的生命周期与 <see cref="Application"/> 一致，
/// 全部在 <c>OnExit</c> 中释放，不实现 <see cref="IDisposable"/>：
/// WPF 不为 <see cref="Application"/> 调用 <c>Dispose</c>，
/// 实现了也永远不会被触发，反而造成“已妥善处理”的错觉。
/// </remarks>
public partial class App : Application
{
    /// <summary>跟随间隔（毫秒）。可由环境变量覆盖以定位性能问题。</summary>
    private static readonly int _followIntervalMilliseconds =
        int.TryParse(Environment.GetEnvironmentVariable("CTM_FOLLOW_MS"), out var follow)
            ? Math.Max(0, follow)
            : 60;

    /// <summary>
    /// 跟随定时器间隔。
    /// </summary>
    /// <remarks>
    /// 间隔为 0 时用一个大值代替：<see cref="DispatcherTimer"/> 在间隔为 0 时
    /// 会连续触发而不等待，用它作“关闭”标志会得到错误的测量结果。
    /// </remarks>
    private static readonly TimeSpan FollowInterval =
        TimeSpan.FromMilliseconds(_followIntervalMilliseconds == 0 ? 86_400_000 : _followIntervalMilliseconds);

    /// <summary>数据刷新间隔（毫秒）。可由环境变量覆盖以定位性能问题。</summary>
    private static readonly TimeSpan DataInterval = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("CTM_DATA_MS"), out var data)
            ? Math.Max(50, data)
            : 700);

    private Mutex? _singleInstance;
    private OverlayWindow? _window;
    private OverlayDataProvider? _provider;
    private TrayIcon? _tray;
    private MouseHook? _mouseHook;
    private DispatcherTimer? _followTimer;
    private DispatcherTimer? _dataTimer;
    private DispatcherTimer? _themeTimer;

    private OverlaySettings _settings = new();

    /// <summary>价格表加载时的问题，需要展示给用户。为 null 表示正常。</summary>
    private string? _pricingWarning;
    private bool _userHidden;
    private IntRect? _lastHostBounds;

    /// <summary>
    /// 上次定位时使用的窗口尺寸。
    /// </summary>
    /// <remarks>
    /// 仅比较宿主矩形是不够的：首次显示时 <see cref="OverlayWindow.ContentSize"/>
    /// 给出的是布局前的估算值，与实际渲染尺寸不同。
    /// 若不跟踪尺寸变化，窗口会停在用估算值算出的位置上。
    /// </remarks>
    private System.Windows.Size _lastPlacedSize;

    private bool _lastDark = true;
    private bool _lastCodexForeground;

    /// <summary>
    /// 忽略前台限制，始终跟随 Codex 主窗口。
    /// </summary>
    /// <remarks>
    /// 由 <c>--self-check</c> 开启。正常运行时为 false：
    /// 只有 Codex 在前台才显示浮层，否则会遮挡其它应用。
    /// </remarks>
    private bool _forceFollow;

    /// <summary>
    /// 上一次已知的前景窗口句柄，用于跳过无变化的跟随轮次。
    /// </summary>
    private nint _lastForegroundHandle;

    /// <summary>
    /// 上一次定位后浮层自身的窗口矩形（物理像素）。
    /// </summary>
    /// <remarks>
    /// 用于检测浮层被外部挪动的情况，见 <c>FollowHostWindow</c> 的预检三。
    /// </remarks>
    private IntRect? _lastPlacedBounds;

    /// <summary>
    /// 强制下一次定位跳过快取路径。
    /// </summary>
    /// <remarks>
    /// 浮层被外部移动时，WPF 缓存的 <c>Left/Top</c> 仍是旧值，
    /// 按“位置未变就不设置”的逻辑会跳过纠正。
    /// 此标志使那一次定位无条件写值。
    /// </remarks>
    private bool _forcedReposition;

    /// <summary>当前宿主窗口句柄，为零表示尚未确定。</summary>
    private nint _hostHandle;

    /// <summary>
    /// 宿主几何需要重新读取。
    /// </summary>
    /// <remarks>
    /// 窗口尺寸变化、面板展开收起、主题切换、缩放变更、
    /// 用户切换锚点等都会置位此标志，强制下一轮重新定位。
    /// </remarks>
    private bool _hostGeometryDirty = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底。
        //
        // 本程序是常驻后台的浮层，没有主窗口也没有控制台。
        // 未处理的异常会让进程静默消失，而用户看不到任何提示，
        // 只会发现托盘图标不见了，也无从报告问题。
        // 这里至少把异常写入日志，并在 UI 线程异常时避免直接崩溃。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal("未处理异常", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal("未观察的任务异常", args.Exception);

            // 标记为已观察，避免进程被终止。
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal("UI 线程异常", args.Exception);

            // 标记为已处理。
            //
            // 权衡：一个未知的异常可能意味着状态已不一致，
            // 继续运行有风险。但对一个只读的展示型浮层而言，
            // 保持可见比直接消失更有价值——定时器下一轮会重新取数，
            // 不会留下错误的持久化状态。
            // 真正的致命错误（如启动失败）不在此路径，仍会退出。
            args.Handled = true;
        };

        // 单实例：多个浮层会互相重叠且各自占用 IPC 连接。
        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexTokenMeterOverlay",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var settingsPath = SettingsStore.ResolveOverride(e.Args);
        _settings = SettingsStore.Load(settingsPath);

        var sessionsRoot = SessionPathResolver.Resolve(e.Args);

        // 价格表加载失败不应阻断启动：回退内置表并在界面上提示。
        var pricing = PricingCatalog.LoadFrom(_settings.PricingOverridePath);
        _pricingWarning = pricing.Warning;

        _provider = new OverlayDataProvider(
            sessionsRoot,
            pricing.Catalog,
            _settings.RateMultiplier,
            pricing.Warning);

        _window = new OverlayWindow();
        _window.RequestReposition += OnRequestReposition;
        _window.OutsideClick += (_, _) => _window.SetPanelOpen(false);
        _window.PositionDragged += OnPositionDragged;
        _window.ApplySettings(_settings);

        _tray = CreateTrayIcon();

        // 点击面板外部收起面板。浮层不夺取焦点，
        // 无法用失去激活事件判断，因此改用全局鼠标钩子。
        _mouseHook = new MouseHook();
        _mouseHook.ButtonDown += OnGlobalButtonDown;
        _mouseHook.Install();

        // 不在此处 Show：只有确认 Codex 在前台时才显示，
        // 否则启动瞬间会在屏幕角落闪出一个无意义的浮层。
        _lastCodexForeground = false;

        StartTimers();

        // 诊断用途：忽略前台限制，始终跟随 Codex 主窗口。
        // 便于在自动化场景下截图或观察浮层，同时不影响正常行为。
        if (e.Args.Contains("--force-follow"))
        {
            _forceFollow = true;
            _hostGeometryDirty = true;
        }

        // 诊断用途：把跟随循环的活动情况写入日志，用于排查预检逻辑。
        if (e.Args.Contains("--trace-follow"))
        {
            _traceFollow = true;
        }

        // 诊断与验证用途：把胶囊拖到指定位置（DIP），用于验证拖动链路。
        // 例：--drag 300,200
        if (FindArgumentValue(e.Args, "--drag") is { } dragTarget)
        {
            var parts = dragTarget.Split(',');

            if (parts.Length == 2
                && double.TryParse(parts[0], out var dragX)
                && double.TryParse(parts[1], out var dragY))
            {
                var dragTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

                dragTimer.Tick += (_, _) =>
                {
                    dragTimer.Stop();

                    // 走与真实拖动相同的处理路径，而不是直接设 Left/Top。
                    _window?.SimulateDragTo(dragX, dragY);
                };

                dragTimer.Start();
            }
        }

        // 诊断用途：启动时直接展开面板，便于截图与视觉验证。
        //
        // 延迟到窗口真正显示后再展开：
        // 启动阶段窗口尚未定位，且跟随循环尚未建立宿主，
        // 此时展开会被随后的“宿主不可用则收起”逻辑覆盖。
        if (e.Args.Contains("--start-expanded"))
        {
            var expandTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };

            expandTimer.Tick += (_, _) =>
            {
                expandTimer.Stop();
                _window?.SetPanelOpen(true);
            };

            expandTimer.Start();
        }

        // 诊断用途：反复切换主题，验证资源字典不会累积。
        if (e.Args.Contains("--theme-stress"))
        {
            _forceFollow = true;

            var themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            var iterations = 0;

            themeTimer.Tick += (_, _) =>
            {
                iterations++;

                if (_window is null || iterations > 10)
                {
                    themeTimer.Stop();
                    Report($"theme-stress：切换 {iterations - 1} 次后，"
                        + $"合并字典数={_window?.MergedDictionaryCount}");
                    Shutdown();
                    return;
                }

                _window.ApplyTheme(iterations % 2 == 0);
            };

            themeTimer.Start();
        }

        if (e.Args.Contains("--self-check"))
        {
            _forceFollow = true;
            RunSelfCheck();
        }

        // 诊断与验证用途：指定胶囊指标，冒号分隔。
        // 例：--metrics contextPercent,turnCost,totalCost
        if (FindArgumentValue(e.Args, "--metrics") is { } metricList)
        {
            var parsed = CapsuleMetricCatalog.Sanitize(
                metricList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            _settings = (_settings with { CapsuleMetrics = CapsuleMetricCatalog.ToKeys(parsed) }).Normalize();
            _window.ApplySettings(_settings);
            _tray?.SyncSettings(_settings);
        }

        // 诊断用途：读取指定偏移下的定位结果，验证设置是否生效。
        if (e.Args.Contains("--check-placement"))
        {
            _forceFollow = true;
            RunPlacementCheck();
        }

        // 诊断用途：打印解析后的设置，不依赖 Codex 是否运行。
        // 用于验证持久化：改设置 → 重启 → 看是否读到同一份值。
        if (e.Args.Contains("--dump-settings"))
        {
            var target = FindArgumentValue(e.Args, "--settings")
                ?? SettingsStore.DefaultPath;

            Report($"设置文件={target}");
            Report($"  存在={File.Exists(target)}");
            Report($"  锚点={_settings.Anchor}  偏移=({_settings.OffsetX},{_settings.OffsetY})");
            Report($"  边距={_settings.Margin}  顶部内缩={_settings.TopInset}  缩放={_settings.Scale}");
            Report($"  允许拖动={_settings.AllowDrag}  跟随主题={_settings.FollowSystemTheme}");
            Report($"  胶囊指标={string.Join(", ", _settings.ResolvedCapsuleMetrics.Select(CapsuleMetricCatalog.ToKey))}");

            Shutdown();
        }

        // 诊断用途：模拟托盘菜单的勾选动作，验证写入链路。
        // 例：--toggle-metric cacheHitRate （写入后立即退出）
        if (FindArgumentValue(e.Args, "--toggle-metric") is { } toggleKey)
        {
            if (CapsuleMetricCatalog.TryParse(toggleKey, out var toggleTarget))
            {
                ToggleCapsuleMetric(toggleTarget);

                Report("toggle-metric：已切换");
                Report($"  胶囊指标={string.Join(", ", _settings.ResolvedCapsuleMetrics.Select(CapsuleMetricCatalog.ToKey))}");
                Report($"  写入成功={SettingsStore.Save(_settings, SettingsStore.ResolveOverride(e.Args))}");
            }
            else
            {
                Report($"toggle-metric：无法识别的指标 {toggleKey}");
            }

            Shutdown();
        }

        // 诊断用途：模拟「恢复默认」，验证它写入的确实是默认集合。
        if (e.Args.Contains("--restore-default-metrics"))
        {
            UpdateSettings(_settings with { CapsuleMetrics = CapsuleMetricCatalog.DefaultKeys });

            Report("restore-default-metrics：已恢复");
            Report($"  胶囊指标={string.Join(", ", _settings.ResolvedCapsuleMetrics.Select(CapsuleMetricCatalog.ToKey))}");
            Report($"  等于默认集合={_settings.ResolvedCapsuleMetrics.SequenceEqual(CapsuleMetricCatalog.Default)}");

            Shutdown();
        }
    }

    /// <summary>
    /// 验证锚点与偏移量是否真的影响了定位。
    /// </summary>
    /// <remarks>
    /// 专为排查「设置改了但位置没变」而写：
    /// 早期版本把设置值读进来却根本没传给定位计算，
    /// 界面上表现为设置完全无效却毫无提示。
    /// </remarks>
    private void RunPlacementCheck()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (_window is null || _hostHandle == nint.Zero)
            {
                Report("placement-check 失败：窗口或宿主不可用");
                Shutdown();
                return;
            }

            var scale = _window.DeviceScale;
            var size = _window.ContentSize;
            var hostBounds = NativeWindow.GetWindowBounds(_hostHandle);
            var hostInDip = _window.DeviceToDip(hostBounds);

            var placement = OverlayPlacementCalculator.Calculate(
                hostInDip,
                (int)Math.Ceiling(size.Width),
                (int)Math.Ceiling(size.Height),
                _settings.Anchor,
                _settings.Margin,
                _settings.TopInset,
                _settings.OffsetX,
                _settings.OffsetY);

            Report($"  锚点={_settings.Anchor}  偏移=({_settings.OffsetX},{_settings.OffsetY})  "
                + $"边距={_settings.Margin}  顶部内缩={_settings.TopInset}");
            Report($"  预期位置(DIP)=({placement.Bounds.Left},{placement.Bounds.Top})  "
                + $"实际位置(DIP)=({Num(_window.Left)},{Num(_window.Top)})");
            Report($"  位置一致={Math.Abs(_window.Left - placement.Bounds.Left) <= 1
                && Math.Abs(_window.Top - placement.Bounds.Top) <= 1}");
            Report($"  允许拖动={_settings.AllowDrag}");

            Shutdown();
        };

        timer.Start();
    }

    /// <summary>
    /// 读取 <c>--名称 值</c> 形式的参数值。
    /// </summary>
    /// <remarks>
    /// 参数个数固定且极少，用 <see cref="IReadOnlyList{T}"/> 与 <c>string[]</c>
    /// 的差异可忽略不计；这里保留接口形式以便测试传任意集合。
    /// </remarks>
    private static string? FindArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// 记录致命异常。
    /// </summary>
    /// <remarks>
    /// 写到固定位置的日志文件，而不是依赖控制台（WinExe 子系统不分配控制台）。
    /// 写入本身也可能失败（磁盘满、权限不足），因此必须吞掉其异常——
    /// 在异常处理器里再抛异常会导致进程立即终止。
    /// </remarks>
    private static void LogFatal(string kind, Exception? exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "codex-token-meter-crash.log");

            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}"
                + Environment.NewLine
                + (exception?.ToString() ?? "(无异常对象)")
                + Environment.NewLine
                + new string('-', 70)
                + Environment.NewLine;

            File.AppendAllText(path, text);
        }
        catch (Exception loggingFailure) when (loggingFailure is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // 日志写不进去也不能再抛：那会把可恢复的异常变成崩溃。
        }
    }

    /// <summary>是否记录跟随循环的预检结果。</summary>
    private bool _traceFollow;

    /// <summary>把跟随循环的诊断信息写入日志。</summary>
    private static void Trace(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "codex-token-meter-follow.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写不进去不应影响跟随。
        }
    }

    /// <summary>构建托盘图标并接上各菜单动作。</summary>
    private TrayIcon CreateTrayIcon()
    {
        var tray = new TrayIcon();

        tray.ToggleVisibilityRequested += (_, _) =>
        {
            _userHidden = !_userHidden;

            if (_userHidden)
            {
                _window?.Hide();
                _lastCodexForeground = false;
            }
            else
            {
                // 让跟随循环重新把它显示到正确位置。
                RequestRelayout();
                _lastForegroundHandle = nint.Zero;
            }
        };

        tray.TogglePanelRequested += (_, _) => _window?.TogglePanel();

        tray.CapsuleMetricToggled += (_, metric) => ToggleCapsuleMetric(metric);

        tray.RestoreDefaultMetricsRequested += (_, _) =>
            UpdateSettings(_settings with
            {
                CapsuleMetrics = CapsuleMetricCatalog.DefaultKeys,
            });

        tray.OpenSettingsRequested += (_, _) => OpenSettingsLocation();

        tray.AnchorChanged += (_, anchor) =>
        {
            // 换锚点时归零偏移：
            // 偏移量的正方向随锚点变化（贴右边缘时向左为正），
            // 沿用旧值会把浮层甩到不可预期的位置。
            UpdateSettings(_settings with { Anchor = anchor, OffsetX = 0, OffsetY = 0 });
        };

        tray.PositionResetRequested += (_, _) =>
            UpdateSettings(_settings with { OffsetX = 0, OffsetY = 0 });

        tray.DragToggled += (_, _) =>
            UpdateSettings(_settings with { AllowDrag = !_settings.AllowDrag });

        tray.ExitRequested += (_, _) => Shutdown();

        tray.SyncSettings(_settings);
        return tray;
    }

    /// <summary>
    /// 切换一个胶囊指标的显隐。
    /// </summary>
    /// <remarks>
    /// 勾选顺序不影响显示顺序：<see cref="CapsuleMetricCatalog.Ordered"/>
    /// 定义了规范顺序，因此无论怎么勾选，胶囊的视觉节奏都保持一致。
    /// </remarks>
    private void ToggleCapsuleMetric(CapsuleMetric metric)
    {
        // 以当前生效的列表为基准，而不是设置文件里的原始列表：
        // 后者可能为 null（表示默认），直接改会把默认集合写死成显式列表。
        var current = _settings.ResolvedCapsuleMetrics.ToList();

        if (!current.Remove(metric))
        {
            if (current.Count >= TrayIcon.MaxVisibleMetrics)
            {
                return;
            }

            current.Add(metric);
        }

        // 全部取消勾选会让胶囊变空，没有意义；此时回退到默认集合。
        if (current.Count == 0)
        {
            current = [.. CapsuleMetricCatalog.Default];
        }

        UpdateSettings(_settings with { CapsuleMetrics = CapsuleMetricCatalog.ToKeys(current) });
    }

    /// <summary>
    /// 用户拖动浮层后，把屏幕位置换算为相对宿主的偏移量并保存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存偏移量而不是屏幕坐标，因为浮层跟随 Codex 窗口：
    /// 绝对坐标会在宿主移动时失效。
    /// </para>
    /// <para>
    /// 拖动后偏移量的符号方向可能与拖动方向相反（贴右边缘时向左拖是增大偏移），
    /// 这是由锚点的语义决定的，不是错误。
    /// </para>
    /// </remarks>
    private void OnPositionDragged(object? sender, (double X, double Y) position)
    {
        if (_window is null || _hostHandle == nint.Zero)
        {
            return;
        }

        var hostBounds = NativeWindow.GetWindowBounds(_hostHandle);
        if (hostBounds.IsEmpty)
        {
            return;
        }

        var size = _window.ContentSize;

        // 窗口位置是 DIP，而偏移量的计算在 DIP 域内完成，
        // 因此先把宿主矩形换算为 DIP。
        var hostInDip = _window.DeviceToDip(hostBounds);

        var (offsetX, offsetY) = OverlayPlacementCalculator.OffsetFromPosition(
            hostInDip,
            (int)Math.Ceiling(size.Width),
            (int)Math.Ceiling(size.Height),
            (int)Math.Round(position.X),
            (int)Math.Round(position.Y),
            _settings.Anchor,
            _settings.Margin,
            _settings.TopInset);

        UpdateSettings(_settings with { OffsetX = offsetX, OffsetY = offsetY });

        // 位置已被拖动改变，浮层自身的矩形记录需要刷新，
        // 否则跟随循环的「被外部挪动」预检会把它当成异常而拉回旧位置。
        _lastPlacedBounds = NativeWindow.GetWindowBounds(
            new System.Windows.Interop.WindowInteropHelper(_window).Handle);
    }

    /// <summary>应用一份新设置并持久化。</summary>
    private void UpdateSettings(OverlaySettings settings)
    {
        _settings = settings.Normalize();

        _window?.ApplySettings(_settings);

        if (_window is not null)
        {
            _window.AllowDrag = _settings.AllowDrag;
        }

        _tray?.SyncSettings(_settings);

        RequestRelayout();
        PersistSettings();
    }

    /// <summary>
    /// 打开设置文件所在目录。
    /// </summary>
    /// <remarks>
    /// 若设置文件尚不存在，先写入一份默认值，
    /// 否则用户打开目录后会看不到任何文件。
    /// </remarks>
    private void OpenSettingsLocation()
    {
        var path = SettingsStore.ResolveOverride(Environment.GetCommandLineArgs());

        if (!File.Exists(path))
        {
            SettingsStore.Save(_settings, path);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            // 资源管理器不可用时忽略，不影响其它功能。
        }
    }

    private void PersistSettings() =>
        SettingsStore.Save(_settings, SettingsStore.ResolveOverride(Environment.GetCommandLineArgs()));

    /// <summary>
    /// 窗口自身尺寸变化时重新定位。
    /// </summary>
    /// <remarks>
    /// 尺寸变化意味着按旧尺寸算出的位置已失效，必须立即重算——
    /// 若等下一轮跟随循环，窗口会在错误位置停留一帧而可见地抖动。
    /// </remarks>
    private void OnRequestReposition(object? sender, EventArgs e)
    {
        if (_window is null || !_lastCodexForeground)
        {
            // 尚未显示或宿主未知：标记待重算，交给跟随循环。
            RequestRelayout();
            return;
        }

        // 窗口尺寸已变，之前缓存的宿主矩形仍有效，直接重算位置。
        if (_window.SizeMatches(_lastPlacedSize))
        {
            return;
        }

        var hostBounds = _hostHandle != nint.Zero
            ? NativeWindow.GetWindowBounds(_hostHandle)
            : _lastHostBounds ?? IntRect.Empty;

        if (hostBounds.IsEmpty)
        {
            RequestRelayout();
            return;
        }

        ApplyPlacement(hostBounds);
    }

    /// <summary>
    /// 标记需要重新定位。
    /// </summary>
    /// <remarks>
    /// 任何影响最终位置的因素变化时调用：宿主几何、锚点、主题、缩放。
    /// 下一轮跟随循环的廉价预检会命中此标志并执行完整重算。
    /// </remarks>
    private void RequestRelayout()
    {
        _hostGeometryDirty = true;
        _lastPlacedSize = default;
    }

    /// <summary>
    /// 处理全局鼠标按下。
    /// </summary>
    /// <remarks>
    /// 仅在面板展开时关心：折叠态下浮层很小，误判成外部点击会把面板
    /// 刚展开就又收起。回调必须尽快返回，系统对低层钩子有超时限制。
    /// </remarks>
    private void OnGlobalButtonDown(object? sender, (double X, double Y) point)
    {
        if (_window is null || !_window.IsPanelOpen)
        {
            return;
        }

        // 鼠标钩子给的是物理像素，而窗口坐标是 DIP；先换算再比较。
        var dip = _window.DeviceToDip(point.X, point.Y);

        if (!_window.ContainsScreenPoint(dip.X, dip.Y))
        {
            _window.SetPanelOpen(false);
        }
    }

    /// <summary>
    /// 开发期自检：直接驱动跟随逻辑并报告结果。
    /// </summary>
    /// <remarks>
    /// 不依赖前台状态。<c>SetForegroundWindow</c> 在非交互场景下不能可靠地
    /// 把窗口带到前台（只有当前前台进程或刚收到输入的进程有此权限），
    /// 因此自检直接以 Codex 主窗口作为宿主输入，验证定位与数据链路。
    /// </remarks>
    private void RunSelfCheck()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "codex-token-meter-selfcheck.log");
        try
        {
            File.Delete(logPath);
        }
        catch (IOException)
        {
            // 首次运行时该文件不存在。
        }

        var host = SelfCheck.FindOrRestoreCodexHostWindow();

        if (host is null)
        {
            Report("self-check 失败：未找到 Codex 主窗口");
            Shutdown();
            return;
        }

        Report($"self-check：找到 Codex 主窗口 句柄={host.Handle}  {host.Bounds}");

        // 交给跟随循环去显示与定位：这样验证的是产品实际路径，
        // 而不是自检自己拼凑出来的一条旁路。

        // 等跟随与数据刷新各跑几轮，让尺寸稳定下来。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (_window is null)
            {
                Report("self-check 失败：窗口为空");
                Shutdown();
                return;
            }

            var size = _window.ContentSize;

            // 自检报告统一用**物理像素**，以便与 Win32 读取的宿主矩形直接比较。
            // 早期版本拿 DIP 位置去比物理宿主矩形，单位不同却数值相近，
            // 在 100% 缩放下恰好蒙混过关，在 150% 缩放下则完全失真。
            var scale = _window.DeviceScale;
            var overlayLeft = _window.Left * scale;
            var overlayTop = _window.Top * scale;
            var overlayWidth = size.Width * scale;
            var overlayHeight = size.Height * scale;
            var overlayRight = overlayLeft + overlayWidth;
            var overlayBottom = overlayTop + overlayHeight;

            var inside = overlayLeft >= host.Bounds.Left
                && overlayTop >= host.Bounds.Top
                && overlayRight <= host.Bounds.Right + 1
                && overlayBottom <= host.Bounds.Bottom + 1;

            // 窗口尺寸与内容尺寸必须一致（同一坐标系下的 DIP 值）。
            var sizeMatches = Math.Abs(_window.ActualWidth - Math.Ceiling(size.Width)) <= 1
                && Math.Abs(_window.ActualHeight - Math.Ceiling(size.Height)) <= 1;

            Report($"  窗口可见={_window.IsVisible}  " +
                   $"缩放={Num(scale, 2)}x  " +
                   $"位置(DIP)=({Num(_window.Left)},{Num(_window.Top)})");
            Report($"  内容尺寸={Num(size.Width, 1)}x{Num(size.Height, 1)}  " +
                   $"窗口尺寸={Num(_window.ActualWidth)}x{Num(_window.ActualHeight)}  " +
                   $"尺寸一致={sizeMatches}");
            Report($"  浮层物理位置=({Num(overlayLeft)},{Num(overlayTop)})-({Num(overlayRight)},{Num(overlayBottom)})");
            Report($"  在宿主内={inside}  距右边缘={Num(host.Bounds.Right - overlayRight)} 像素");

            var data = _provider?.Current;
            Report($"  数据：会话={data?.ThreadId ?? "(无)"}");
            Report($"  胶囊指标={string.Join(", ", _settings.ResolvedCapsuleMetrics.Select(CapsuleMetricCatalog.ToKey))}");
            Report($"  项目={ProjectResolver.Resolve(data?.WorkingDirectory).Name ?? "(无法确定)"}  "
                + $"路径={data?.WorkingDirectory ?? "(无)"}");
            Report($"  模型={data?.Model ?? "(无)"}  " +
                   $"本轮={data?.CurrentTurn.Total:N0} 累计={data?.Cumulative.Total:N0}");
            Report($"  费用：本轮=${Num(data?.CurrentTurnCost ?? 0, 6)}  累计=${Num(data?.TotalCost ?? 0, 6)}");
            Report($"  上下文={Num(data is null ? 0 : data.ContextPercent, 1)}%  " +
                   $"缓存命中率={Num(data is null ? 0 : data.CacheHitRate, 1)}%");
            Report($"  连接={data?.IsConnected}  不完整={data?.IsPartial}  未定价={data?.IsUnpriced}");

            // 展开面板，验证尺寸变化后的重定位。
            _window.SetPanelOpen(true);

            Report($"  已请求展开：IsPanelOpen={_window.IsPanelOpen}");

            var expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            expandTimer.Tick += (_, _) =>
            {
                expandTimer.Stop();

                var expanded = _window.ContentSize;

                // 同样统一到物理像素后再与宿主比较。
                var expandedLeft = _window.Left * scale;
                var expandedTop = _window.Top * scale;
                var expandedRight = expandedLeft + (expanded.Width * scale);
                var expandedBottom = expandedTop + (expanded.Height * scale);

                var expandedInside = expandedLeft >= host.Bounds.Left
                    && expandedTop >= host.Bounds.Top
                    && expandedRight <= host.Bounds.Right + 1
                    && expandedBottom <= host.Bounds.Bottom + 1;

                Report(string.Empty);
                Report($"  展开后：IsPanelOpen={_window.IsPanelOpen}  " +
                       $"面板高度={Num(_window.PanelHeight)}");

                var expandedSizeMatches = Math.Abs(_window.ActualWidth - Math.Ceiling(expanded.Width)) <= 1
                    && Math.Abs(_window.ActualHeight - Math.Ceiling(expanded.Height)) <= 1;

                Report($"  展开后内容尺寸={Num(expanded.Width, 1)}x{Num(expanded.Height, 1)}  " +
                       $"窗口尺寸={Num(_window.ActualWidth)}x{Num(_window.ActualHeight)}  " +
                       $"尺寸一致={expandedSizeMatches}");
                Report($"  展开后物理位置=({Num(expandedLeft)},{Num(expandedTop)})-" +
                       $"({Num(expandedRight)},{Num(expandedBottom)})");
                Report($"  展开后在宿主内={expandedInside}");

                Shutdown();
            };

            expandTimer.Start();
        };

        timer.Start();
    }

    /// <summary>
    /// 向诊断文件输出自检结果。
    /// </summary>
    /// <remarks>
    /// 不用控制台：WinExe 子系统不分配控制台，
    /// 且父控制台在重定向场景下 AttachConsole 也不可靠。写文件最稳定。
    /// </remarks>
    /// <summary>
    /// 按不变文化格式化诊断输出中的小数。
    /// </summary>
    /// <remarks>
    /// 诊断日志是给人读的调试信息，数字必须是确定形式。
    /// 在阿拉伯语（ar-SA）或波斯语（fa-IR）区域下，
    /// 插值里的 <c>$"{x:F0}"</c> 会输出本地数字或 <c>٫</c> 小数点，
    /// 让日志难以比对。
    ///
    /// 用 <see cref="NumberFormatter.Fixed"/> 而不是
    /// <c>PercentRaw</c>：后者是百分比专用，位数受限（0–2）。
    /// </remarks>
    private static string Num(double value, int decimals = 0) =>
        NumberFormatter.Fixed(value, decimals);

    /// <summary>按不变文化格式化诊断输出中的整数。</summary>
    private static string Num(long value) => NumberFormatter.Count(value);

    private static void Report(string message)
    {
        Debug.WriteLine(message);

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "codex-token-meter-selfcheck.log");
            File.AppendAllText(path, message + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写不进去也不应影响自检流程。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _followTimer?.Stop();
        _dataTimer?.Stop();
        _themeTimer?.Stop();

        _mouseHook?.Dispose();
        _tray?.Dispose();
        _provider?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    private void StartTimers()
    {
        _followTimer = new DispatcherTimer { Interval = FollowInterval };
        _followTimer.Tick += (_, _) => FollowHostWindow();
        _followTimer.Start();

        _dataTimer = new DispatcherTimer { Interval = DataInterval };
        _dataTimer.Tick += (_, _) => RefreshData();
        _dataTimer.Start();

        // 系统主题切换不频繁，1 秒检查一次足够。
        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _themeTimer.Tick += (_, _) => SyncTheme();
        _themeTimer.Start();
    }

    /// <summary>
    /// 让浮层跟随 Codex 主窗口。
    /// </summary>
    /// <remarks>
    /// 仅在宿主矩形变化时才移动窗口：每帧无条件设置位置会造成持续重绘。
    /// 另外，窗口自身尺寸变化会通过 <c>RequestReposition</c> 主动请求重算，
    /// 因此这里同时处理“宿主变了”与“自己变了”两种情况。
    /// </remarks>
    private void FollowHostWindow()
    {
        // 用局部引用固定非空状态：字段可能在方法执行期间被外部改变，
        // 且编译器不会跨语句推导字段的非空性。
        if (_window is not { } window)
        {
            return;
        }

        // 诊断用途：设 CTM_FOLLOW_MS=0 时完全跳过跟随循环。
        // 注意不能把定时器间隔直接设为 0：那会让它连续触发，
        // 反而把 CPU 烧满，测量结果会完全失真。
        if (_followIntervalMilliseconds == 0)
        {
            return;
        }

        var foreground = NativeWindow.GetForegroundWindowHandle();

        // 廉价预检一：前景窗口换了，宿主可能跟着换。
        var foregroundChanged = foreground != _lastForegroundHandle;

        // 廉价预检二：已知宿主的矩形变了（移动或缩放）。
        //
        // 只看前景句柄不够：拖动 Codex 窗口时前景不变，
        // 仅靠句柄比较会漏掉移动。而 GetWindowRect 实测约 3 微秒，
        // 每轮一次的开销可忽略。
        var hostMoved = false;

        if (_hostHandle != nint.Zero && _lastHostBounds is { } known)
        {
            hostMoved = NativeWindow.GetWindowBounds(_hostHandle) != known;
        }

        // 廉价预检三：浮层自身被外部移动了。
        //
        // 浮层不可拖动，但它仍可能被系统挪动：显示器插拔改变 DPI、
        // 分辨率切换、远程桌面会话变化等都会让窗口位置失效。
        //
        // 这里必须用 Win32 读取实际矩形：WPF 缓存的 Left/Top 不会
        // 随外部 SetWindowPos 更新，只比较 Left/Top 会得出“未移动”的错误结论。
        var overlayDisplaced = false;

        if (_lastCodexForeground && _window.IsVisible && _lastPlacedBounds is { } placed)
        {
            var actual = NativeWindow.GetWindowBounds(
                new System.Windows.Interop.WindowInteropHelper(_window).Handle);

            overlayDisplaced = actual != placed;
        }

        if (!foregroundChanged && !hostMoved && !overlayDisplaced && !_hostGeometryDirty)
        {
            if (_traceFollow && Environment.TickCount64 % 2000 < 60)
            {
                Trace($"跳过：前景未变 hostMoved={hostMoved} "
                    + $"overlayDisplaced={overlayDisplaced} "
                    + $"dirty={_hostGeometryDirty} "
                    + $"lastPlaced={_lastPlacedBounds?.ToString() ?? "null"}");
            }

            return;
        }

        if (_traceFollow)
        {
            Trace($"重算：前景变了={foregroundChanged} 宿主动了={hostMoved} "
                + $"浮层被挪={overlayDisplaced} dirty={_hostGeometryDirty}");
        }

        // 浮层被外部挪动时，必须让 WPF 的逻辑位置与实际情况重新对齐，
        // 否则下面的“位置未变”判断会因缓存的 Left/Top 未变而跳过纠正。
        if (overlayDisplaced && _window is not null)
        {
            _forcedReposition = true;
        }

        _lastForegroundHandle = foreground;
        _hostGeometryDirty = false;

        var codexProcessIds = SnapshotCodexProcessIds();

        var candidates = NativeWindow.EnumerateTopLevelWindows(codexProcessIds.Contains);

        // 自检模式下不依赖前台：SetForegroundWindow 在非交互场景不可靠，
        // 而自检的目标是验证定位与数据链路，而非 Windows 的前台规则。
        //
        // 这里用「同进程 + 面积最大」而不是直接在整个候选集里取最大：
        // 用户可能开着多个 Codex 实例，跨进程取最大会跟随到另一个实例的窗口。
        // 句柄作为次键保证结果稳定，否则同尺寸窗口在不同轮次可能选中不同窗口，
        // 浮层会无规律地跳换宿主。
        var host = _forceFollow
            ? SelectLargestHostCandidate(candidates)
            : CodexWindowSelector.Select(candidates, foreground);

        _hostHandle = host?.Handle ?? nint.Zero;

        if (host is null)
        {
            if (_lastCodexForeground)
            {
                window.Hide();
                _lastCodexForeground = false;
            }

            // 宿主不可用时也要收起面板，避免下次显示时仍是展开态。
            window.SetPanelOpen(false);
            return;
        }

        // 用户用托盘菜单隐藏时，不要因为 Codex 重新回到前台而弹回来。
        if (_userHidden)
        {
            return;
        }

        if (!_lastCodexForeground)
        {
            // 先定尺寸再定位：定位需要真实尺寸，否则会摆错位置。
            window.RefreshSize();

            _hostGeometryDirty = true;
            _lastPlacedSize = default;
            ApplyPlacement(host.Bounds);

            if (_pendingPlacement is { } pending)
            {
                window.Left = pending.X;
                window.Top = pending.Y;
                _pendingPlacement = null;
            }

            window.Show();
            _lastCodexForeground = true;
            window.UpdateLayout();

            // 不在此处返回：布局后尺寸会变，
            // 让下面的尺寸比较逻辑在本轮就完成修正。
        }

        // 需要重新定位的任一条件：宿主矩形变化、窗口尺寸变化、
        // 或浮层被外部挪动（后者需要强制纠正）。
        var size = window.ContentSize;

        var hostChanged = OverlayPlacementCalculator.NeedsReposition(host.Bounds, _lastHostBounds);
        var sizeChanged = Math.Abs(size.Width - _lastPlacedSize.Width) > 0.5
            || Math.Abs(size.Height - _lastPlacedSize.Height) > 0.5;

        if (!hostChanged && !sizeChanged && !overlayDisplaced)
        {
            return;
        }

        _lastHostBounds = host.Bounds;
        ApplyPlacement(host.Bounds);
    }

    /// <summary>
    /// 从候选中选出面积最大的主窗口，用于 <c>--force-follow</c> 诊断模式。
    /// </summary>
    /// <remarks>
    /// 选路实现在 Core 中以便单元测试，见
    /// <see cref="CodexWindowSelector.SelectLargest"/>。
    /// </remarks>
    private static WindowCandidate? SelectLargestHostCandidate(
        IReadOnlyList<WindowCandidate> candidates) =>
        CodexWindowSelector.SelectLargest(candidates);

    private void ApplyPlacement(IntRect hostBounds)
    {
        if (_window is null || _isApplyingPlacement)
        {
            return;
        }

        // 重入保护：设置 Left/Top 可能再次触发尺寸变化，
        // 若不在入口拦截会形成无限重定位。
        _isApplyingPlacement = true;

        try
        {
            var size = _window.ContentSize;

            // 关键：宿主矩形来自 Win32，是物理像素；
            // 而窗口的 Left/Top 与 ContentSize 都是设备无关单位。
            // 必须先换算，否则在非 100% 缩放下会把窗口摆到屏幕之外。
            var hostInDip = _window.DeviceToDip(hostBounds);

            var placement = OverlayPlacementCalculator.Calculate(
                hostInDip,
                (int)Math.Ceiling(size.Width),
                (int)Math.Ceiling(size.Height),
                _settings.Anchor,
                _settings.Margin,
                _settings.TopInset,
                _settings.OffsetX,
                _settings.OffsetY);

            if (!placement.IsVisible)
            {
                return;
            }

            if (_traceFollow)
            {
                Trace($"  定位：宿主DIP={hostInDip} 目标DIP={placement.Bounds} "
                    + $"当前DIP=({Num(_window.Left)},{Num(_window.Top)}) "
                    + $"可见={_window.IsVisible} 强制={_forcedReposition}");
            }

            // 记录本次使用的尺寸，供下一轮判断是否需要因尺寸变化而重算。
            _lastPlacedSize = size;

            // placement 给出的矩形就是窗口应处的位置，直接赋给 Left/Top 即可。
            var left = placement.Bounds.Left;
            var top = placement.Bounds.Top;

            if (_window.IsVisible)
            {
                // 位置未变时不赋值，减少无谓的布局失效；
                // 但被外部挪动过时必须强制纠正。
                if (!_forcedReposition
                    && Math.Abs(_window.Left - left) < 0.5
                    && Math.Abs(_window.Top - top) < 0.5)
                {
                    return;
                }

                _forcedReposition = false;
                _window.Left = left;
                _window.Top = top;

                // 记录实际落位，供下一轮预检比对。
                // 用 Win32 读取而不是直接用 left/top：
                // 后者是 DIP，与实际窗口矩形不在同一坐标系。
                _lastPlacedBounds = NativeWindow.GetWindowBounds(
                    new System.Windows.Interop.WindowInteropHelper(_window).Handle);

                return;
            }

            _pendingPlacement = new Point(left, top);
        }
        finally
        {
            _isApplyingPlacement = false;
        }
    }

    /// <summary>防止重定位引发的布局循环。</summary>
    private bool _isApplyingPlacement;

    /// <summary>窗口尚未显示时暂存的目标位置，在显示后应用。</summary>
    private Point? _pendingPlacement;

    private void RefreshData()
    {
        if (_provider is null || _window is null)
        {
            return;
        }

        _provider.Poll();

        if (!_lastCodexForeground)
        {
            return;
        }

        var data = _provider.Current;
        _window.UpdateData(data);

        _tray?.UpdateTooltip(BuildTooltip(data));

        // 面板展开会改变窗口尺寸，尺寸变化事件会触发重定位。
        // 这里只标记待重算，让下一轮兜底一次。
        if (_window.IsPanelOpen)
        {
            RequestRelayout();
        }
    }

    /// <summary>构造托盘悬停提示文本。</summary>
    private static string? BuildTooltip(OverlayData data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        var cost = data.TotalCost is { } total ? $"${NumberFormatter.Cost(total)}" : "未定价";
        return $"本轮 {NumberFormatter.Compact(data.CurrentTurn.Total)} · {cost}";
    }

    private void SyncTheme()
    {
        if (_window is null)
        {
            return;
        }

        // 不跟随系统时用设置里的固定主题。
        var isDark = _settings.FollowSystemTheme ? IsSystemDarkMode() : _settings.DarkTheme;

        if (isDark == _lastDark)
        {
            return;
        }

        _lastDark = isDark;
        _window.ApplyTheme(isDark);

        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeWindow.ApplyDarkMode(handle, isDark);
        }

        // 主题变化可能改变尺寸，重新定位。
        RequestRelayout();
    }

    /// <summary>读取系统应用主题偏好。</summary>
    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme: 0 表示深色，1 表示浅色。
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException)
        {
            // 读不到时按深色处理：Codex 默认深色主题。
            return true;
        }
    }

    /// <summary>
    /// 判定进程是否属于 Codex Desktop。
    /// </summary>
    /// <remarks>
    /// 结果按进程 id 缓存：查询模块路径是开销可观的系统调用，
    /// 而跟随循环每 60ms 就会枚举一次全部顶层窗口。
    /// 缓存同时记住进程名，以避免系统复用 PID 时把旧结论误用到新进程上。
    /// <summary>
    /// 已识别的 Codex 进程集合，供跟随循环快速查询。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不能对每个窗口 PID 单独调 <c>Process.MainModule</c>。
    /// 那是跨进程查询，实机实测（570 个顶层窗口、60ms 轮询）
    /// 会把单核跑到 98%，而其中真正属于 Codex 的进程只有十几个。
    /// </para>
    /// <para>
    /// 改为整体扫描一次进程列表：进程总数远小于窗口数，
    /// 且同一次扫描里每个进程只读一次模块路径。
    /// 结果按 (PID, 启动时间) 缓存，以便在 PID 被复用后重新判定。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<int, (DateTime StartTime, bool IsCodex)> ProcessCache = [];

    /// <summary>最近一次全量扫描的时段标记，用于限制扫描频率。</summary>
    private static long _processScanEpoch = -1;

    /// <summary>
    /// 取得属于 Codex Desktop 的进程 id 集合。
    /// </summary>
    /// <remarks>
    /// 每秒最多全量扫描两次：进程创建/退出不频繁，
    /// 而每次扫描要遍历系统全部进程。
    /// </remarks>
    private static HashSet<int> SnapshotCodexProcessIds()
    {
        // 用 500ms 作为“时代”标记；同一时段内复用上次结果。
        var epoch = Environment.TickCount64 / 500;

        lock (ProcessCache)
        {
            if (epoch == _processScanEpoch)
            {
                return [.. ProcessCache.Where(pair => pair.Value.IsCodex).Select(pair => pair.Key)];
            }

            _processScanEpoch = epoch;

            var alive = new HashSet<int>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var processId = process.Id;
                    alive.Add(processId);

                    DateTime startTime;

                    try
                    {
                        startTime = process.StartTime;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                        // 取不到启动时间：按每次重查处理。
                        startTime = DateTime.MinValue;
                    }

                    // (PID, 启动时间) 相同即认为仍是同一进程，无需重读模块路径。
                    if (ProcessCache.TryGetValue(processId, out var cached)
                        && cached.StartTime == startTime
                        && startTime != DateTime.MinValue)
                    {
                        continue;
                    }

                    var isCodex = false;

                    try
                    {
                        isCodex = CodexProcessIdentifier.IsCodexDesktop(process.MainModule?.FileName);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                        // 系统进程或权限不足。
                    }

                    ProcessCache[processId] = (startTime, isCodex);
                }
                catch (InvalidOperationException)
                {
                    // 进程在枚举过程中退出。
                }
                finally
                {
                    process.Dispose();
                }
            }

            // 清理已退出的进程，避免缓存无限增长。
            foreach (var stale in ProcessCache.Keys.Where(id => !alive.Contains(id)).ToArray())
            {
                ProcessCache.Remove(stale);
            }

            return [.. ProcessCache.Where(pair => pair.Value.IsCodex).Select(pair => pair.Key)];
        }
    }
}
