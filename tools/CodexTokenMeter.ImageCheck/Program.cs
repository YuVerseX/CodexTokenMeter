using System.Drawing;
using System.Drawing.Imaging;

// 开发期工具：逐列输出截图的像素分布，定位内容边界。
// 不参与发布产物。

if (args.Length == 0)
{
    Console.Error.WriteLine("需要 PNG 路径。");
    return 1;
}

using var bitmap = new Bitmap(args[0]);

Console.WriteLine($"尺寸：{bitmap.Width}×{bitmap.Height}");
Console.WriteLine();

var background = bitmap.GetPixel(0, 0);
Console.WriteLine($"背景色：(0,0) = R{background.R} G{background.G} B{background.B}");
Console.WriteLine();

Console.WriteLine("最右 20 列的非背景像素统计：");
Console.WriteLine("  列号    非背景像素数   颜色样本");

for (var x = Math.Max(0, bitmap.Width - 20); x < bitmap.Width; x++)
{
    var count = 0;
    Color? sample = null;

    for (var y = 0; y < bitmap.Height; y++)
    {
        var pixel = bitmap.GetPixel(x, y);

        var difference = Math.Abs(pixel.R - background.R)
            + Math.Abs(pixel.G - background.G)
            + Math.Abs(pixel.B - background.B);

        if (difference > 30)
        {
            count++;
            sample ??= pixel;
        }
    }

    var sampleText = sample is { } color
        ? $"R{color.R} G{color.G} B{color.B}"
        : "-";

    Console.WriteLine($"  {x,4}    {count,8}      {sampleText}");
}

Console.WriteLine();
Console.WriteLine("最左 20 列的非背景像素统计：");

for (var x = 0; x < Math.Min(20, bitmap.Width); x++)
{
    var count = 0;

    for (var y = 0; y < bitmap.Height; y++)
    {
        var pixel = bitmap.GetPixel(x, y);

        var difference = Math.Abs(pixel.R - background.R)
            + Math.Abs(pixel.G - background.G)
            + Math.Abs(pixel.B - background.B);

        if (difference > 30)
        {
            count++;
        }
    }

    Console.WriteLine($"  {x,4}    {count,8}");
}

return 0;
