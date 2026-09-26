using CodexTokenMeter.Core.Windowing;

// 开发期验证：对真实 Codex 主窗口的矩形运行定位计算。
// 不依赖前台状态，因此可在任何环境下复现。
// 不参与发布产物。
//
// 用法：dotnet run --project tools/CodexTokenMeter.GeometryCheck [浮层宽] [浮层高]

var overlayWidth = args.Length > 0 && int.TryParse(args[0], out var w) ? w : 253;
var overlayHeight = args.Length > 1 && int.TryParse(args[1], out var h) ? h : 69;

// 实机实测的 Codex 主窗口矩形（全屏）。
var hostBounds = new IntRect(0, 0, 3840, 2100);

Console.WriteLine($"宿主窗口：{hostBounds}");
Console.WriteLine($"浮层尺寸：{overlayWidth}×{overlayHeight}");
Console.WriteLine();

var passed = true;

foreach (var anchor in new[] { OverlayAnchor.TopRight, OverlayAnchor.BottomRight })
{
    var placement = OverlayPlacementCalculator.Calculate(
        hostBounds,
        overlayWidth,
        overlayHeight,
        anchor);

    var inside = placement.Bounds.Left >= hostBounds.Left
        && placement.Bounds.Top >= hostBounds.Top
        && placement.Bounds.Right <= hostBounds.Right
        && placement.Bounds.Bottom <= hostBounds.Bottom;

    var gapRight = hostBounds.Right - placement.Bounds.Right;
    var gapTop = placement.Bounds.Top - hostBounds.Top;

    Console.WriteLine($"{anchor,-14} {placement.Bounds}");
    Console.WriteLine($"  在宿主内={inside}  距右边缘={gapRight}  距上边缘={gapTop}");

    if (!inside)
    {
        Console.WriteLine("  <== 失败：浮层超出宿主边界");
        passed = false;
    }

    if (gapRight != OverlayPlacementCalculator.DefaultMargin)
    {
        Console.WriteLine($"  <== 失败：右边距应为 {OverlayPlacementCalculator.DefaultMargin}，实际 {gapRight}");
        passed = false;
    }

    Console.WriteLine();
}

// 验证小屏与极端尺寸下的收敛。
Console.WriteLine("=== 极端尺寸收敛 ===");

var cases = new (IntRect Host, int W, int H, string Label)[]
{
    (new IntRect(0, 0, 800, 600), 253, 69, "小窗口"),
    (new IntRect(0, 0, 300, 600), 253, 69, "窄窗口"),
    (new IntRect(0, 0, 800, 200), 253, 69, "矮窗口"),
    (new IntRect(0, 0, 800, 60), 253, 69, "比浮层矮"),
    (new IntRect(-1920, 0, 0, 1080), 253, 69, "副屏(负坐标)"),
};

foreach (var (host, width, height, label) in cases)
{
    var placement = OverlayPlacementCalculator.Calculate(host, width, height);

    var inside = placement.Bounds.Left >= host.Left
        && placement.Bounds.Top >= host.Top
        && placement.Bounds.Right <= host.Right
        && placement.Bounds.Bottom <= host.Bottom;

    var fits = height <= host.Height - OverlayPlacementCalculator.DefaultMargin;

    // 浮层比宿主高时无法完整放入，此时只要求它从宿主顶部开始。
    var acceptable = fits ? inside : placement.Bounds.Top == host.Top;

    Console.WriteLine($"  {label,-14} {host}  ->  {placement.Bounds}  可接受={acceptable}");

    if (!acceptable)
    {
        passed = false;
    }
}

Console.WriteLine();
Console.WriteLine(passed ? "几何验证通过。" : "几何验证失败。");

return passed ? 0 : 1;
