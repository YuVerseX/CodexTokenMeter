namespace CodexTokenMeter.Core.Windowing;

/// <summary>屏幕坐标矩形，左上角为原点，单位为物理像素。</summary>
public readonly record struct IntRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static IntRect Empty { get; } = new(0, 0, 0, 0);

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) [{Width}×{Height}]";
}

/// <summary>
/// 描述一个候选顶层窗口的可判定事实。
/// </summary>
/// <remarks>
/// 这些字段是识别主窗口的全部依据，刻意与 Win32 查询分离：
/// 识别规则因此可以在没有真实窗口的情况下完整测试。
/// </remarks>
public sealed record WindowCandidate
{
    public required nint Handle { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>是否属于 Codex 进程。</summary>
    public required bool IsCodexProcess { get; init; }

    public required bool IsVisible { get; init; }

    public required bool IsMinimized { get; init; }

    /// <summary>拥有者窗口句柄。非零表示它是工具窗口或对话框的附属窗口。</summary>
    public required nint OwnerHandle { get; init; }

    public required long ExtendedStyle { get; init; }

    public required IntRect Bounds { get; init; }

    public required string ClassName { get; init; }
}
