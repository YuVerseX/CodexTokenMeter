using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Settings;

/// <summary>
/// 用户设置。
/// </summary>
/// <remarks>
/// 所有字段都有合理默认值，因此首次运行无需任何配置文件。
/// 读取失败时回退到默认值而不是中断启动：设置损坏不应让浮层无法使用。
/// </remarks>
public sealed record OverlaySettings
{
    /// <summary>浮层相对宿主窗口的锚点。</summary>
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.TopRight;

    /// <summary>
    /// 水平偏移（DIP）。正值表示向宿主内部移动。
    /// </summary>
    /// <remarks>
    /// 存「相对宿主的偏移」而不是屏幕绝对坐标：
    /// 浮层跟随 Codex 窗口，绝对坐标会在宿主移动时立刻失效。
    /// </remarks>
    public int OffsetX { get; init; }

    /// <summary>垂直偏移（DIP）。正值表示向宿主内部移动。</summary>
    public int OffsetY { get; init; }

    /// <summary>
    /// 是否允许拖动浮层。
    /// </summary>
    /// <remarks>
    /// 默认开启。关闭后浮层位置完全由锚点与偏移量决定，
    /// 适合固定工作环境的用户，避免误拖后还要手动调回。
    /// </remarks>
    public bool AllowDrag { get; init; } = true;

    /// <summary>
    /// 顶部内缩量（物理像素）。
    /// </summary>
    /// <remarks>
    /// Codex Desktop 是全屏无边框窗口，标题栏由应用自绘，
    /// 系统指标无法给出其视觉标题栏高度，因此由该值控制，
    /// 默认避开其窗口按钮区域。
    /// </remarks>
    public int TopInset { get; init; } = OverlayPlacementCalculator.DefaultTopInset;

    /// <summary>与宿主窗口边缘的间距。</summary>
    public int Margin { get; init; } = OverlayPlacementCalculator.DefaultMargin;

    /// <summary>是否跟随系统主题。</summary>
    public bool FollowSystemTheme { get; init; } = true;

    /// <summary>不跟随系统主题时使用的主题：true 为深色。</summary>
    public bool DarkTheme { get; init; } = true;

    /// <summary>浮层整体缩放比例，用于适配不同 DPI 与个人偏好。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// 胶囊态显示的指标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存键名而不是序号：枚举值一旦调整，序号会让用户的配置
    /// 静默地指向另一个指标，而键名不会。
    /// </para>
    /// <para>
    /// 为 null 表示使用默认集合，与“用户主动清空”区分开。
    /// 后者由 <see cref="CapsuleMetricCatalog.Sanitize"/> 回退到默认值。
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? CapsuleMetrics { get; init; }

    /// <summary>解析后的胶囊指标列表（按规范顺序）。</summary>
    [JsonIgnore]
    public IReadOnlyList<CapsuleMetric> ResolvedCapsuleMetrics =>
        CapsuleMetricCatalog.Sanitize(CapsuleMetrics);

    /// <summary>价格覆盖文件路径。为 null 时只用内置价格表。</summary>
    public string? PricingOverridePath { get; init; }

    /// <summary>
    /// 上游分组倍率。
    /// </summary>
    /// <remarks>
    /// 上游站点在牌价之上叠加自己的倍率，该值无法从本地数据推断，
    /// 只能由用户在站点后台核对 <c>actual_cost / total_cost</c> 后填写。
    /// </remarks>
    public double RateMultiplier { get; init; } = 1.0;

    /// <summary>把设置约束到有效范围。</summary>
    public OverlaySettings Normalize() => this with
    {
        TopInset = Math.Clamp(TopInset, 0, 400),
        Margin = Math.Clamp(Margin, 0, 200),
        OffsetX = Math.Clamp(OffsetX, -OverlayPlacementCalculator.MaxOffset, OverlayPlacementCalculator.MaxOffset),
        OffsetY = Math.Clamp(OffsetY, -OverlayPlacementCalculator.MaxOffset, OverlayPlacementCalculator.MaxOffset),
        Scale = Math.Clamp(Scale, 0.6, 2.0),
        RateMultiplier = RateMultiplier is > 0 and < 100 ? RateMultiplier : 1.0,
        CapsuleMetrics = NormalizeCapsuleMetrics(CapsuleMetrics),
        PricingOverridePath = string.IsNullOrWhiteSpace(PricingOverridePath)
            ? null
            : PricingOverridePath,
    };

    /// <summary>
    /// 规范化胶囊指标列表。
    /// </summary>
    /// <remarks>
    /// 为 null 时保持 null（“未配置”）；否则去重、丢弃无法识别的键，
    /// 并重排为规范顺序。
    /// <para>
    /// 重排是有意为之：胶囊始终按规范顺序渲染，
    /// 若设置文件保留用户的书写顺序，文件内容与屏幕实际排列会不一致。
    /// 让文件如实反映渲染结果比保留书写顺序更有价值。
    /// </para>
    /// <para>
    /// 不做“空列表则回退默认”的处理：那是读取时的职责（见
    /// <see cref="ResolvedCapsuleMetrics"/>）。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string>? NormalizeCapsuleMetrics(IReadOnlyList<string>? keys)
    {
        if (keys is null)
        {
            return null;
        }

        var parsed = CapsuleMetricCatalog.Sanitize(keys);
        return CapsuleMetricCatalog.ToKeys(parsed);
    }
}

/// <summary>
/// 设置的读写。
/// </summary>
/// <remarks>
/// 存放于 <c>%LOCALAPPDATA%\CodexTokenMeter\settings.json</c>。
/// 写入采用「先写临时文件再替换」，避免中途失败损坏既有配置。
/// </remarks>
public static class SettingsStore
{
    /// <summary>命令行参数名：指定设置文件路径。</summary>
    public const string SettingsArgument = "--settings";

    /// <summary>设置文件所在目录。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTokenMeter");

    /// <summary>设置文件的完整路径。</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>
    /// 解析设置文件路径：优先使用 <c>--settings &lt;路径&gt;</c>，否则用默认位置。
    /// </summary>
    /// <remarks>
    /// 参数供开发与测试隔离配置使用，普通用户无需关心。
    /// </remarks>
    public static string ResolveOverride(IReadOnlyList<string>? arguments = null)
    {
        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals(SettingsArgument, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Path.GetFullPath(arguments[index + 1].Trim().Trim('"'));
                }
            }
        }

        return DefaultPath;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 读取设置。
    /// </summary>
    /// <param name="path">设置文件路径。为 null 时使用默认位置。</param>
    /// <returns>
    /// 解析成功的设置；文件不存在、无法读取或内容损坏时返回默认设置。
    /// </returns>
    public static OverlaySettings Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path))
            {
                return new OverlaySettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<OverlaySettings>(json, Options);

            return (settings ?? new OverlaySettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // 设置损坏不应阻止启动：用默认值继续，用户可随时重新调整。
            return new OverlaySettings();
        }
    }

    /// <summary>
    /// 写入设置。
    /// </summary>
    /// <returns>写入是否成功。</returns>
    public static bool Save(OverlaySettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        path ??= DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings.Normalize(), Options);

            // 先写临时文件再替换：写入过程中断电或异常不会留下半个 JSON。
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
