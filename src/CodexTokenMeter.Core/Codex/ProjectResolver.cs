namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 从会话的工作目录推导项目信息。
/// </summary>
/// <remarks>
/// <para>
/// 会话日志按 <c>年\月\日</c> 分层存放，目录名不含项目信息——
/// 早期版本取目录最后一级，界面上显示成了日期（如「25」）。
/// </para>
/// <para>
/// 真实路径来自 <c>turn_context</c>，已解析为
/// <see cref="SessionSnapshot.WorkingDirectory"/>，此处只做展示层面的推导。
/// </para>
/// </remarks>
public static class ProjectResolver
{
    /// <summary>
    /// Codex 在用户目录下自动创建的辅助目录片段。
    /// </summary>
    /// <remarks>
    /// 这类路径会出现在 <c>workspace_roots</c> 里，但不是用户的项目。
    /// </remarks>
    private static readonly string[] AuxiliaryPathMarkers =
    [
        @"\.codex\visualizations\",
        @"\.codex\attachments\",
        @"\.codex\cache\",
        @"\.codex\.tmp\",
    ];

    /// <summary>
    /// 由工作目录推导项目信息。
    /// </summary>
    /// <param name="workingDirectory">会话的工作目录，取自 turn_context。</param>
    /// <returns>项目信息。无法确定时各项为 null。</returns>
    public static ProjectInfo Resolve(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ProjectInfo.Unknown;
        }

        var normalized = workingDirectory.Trim().TrimEnd('\\', '/');

        if (normalized.Length == 0 || IsAuxiliary(normalized))
        {
            return ProjectInfo.Unknown;
        }

        var name = Path.GetFileName(normalized);

        // 盘符根目录（如 E:\）没有名字，直接显示完整路径。
        return string.IsNullOrWhiteSpace(name)
            ? new ProjectInfo { Root = normalized, Name = normalized }
            : new ProjectInfo { Root = normalized, Name = name };
    }

    /// <summary>是否是 Codex 自动生成的辅助目录。</summary>
    public static bool IsAuxiliary(string path) =>
        AuxiliaryPathMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

/// <summary>项目信息。</summary>
public sealed record ProjectInfo
{
    /// <summary>项目根目录的完整路径。</summary>
    public string? Root { get; init; }

    /// <summary>项目名，即根目录的最后一级。</summary>
    public string? Name { get; init; }

    /// <summary>未能解析。</summary>
    public static ProjectInfo Unknown { get; } = new();
}
