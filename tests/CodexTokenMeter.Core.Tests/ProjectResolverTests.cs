using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 项目信息推导。
/// </summary>
/// <remarks>
/// 回归保护：早期版本从会话日志所在目录名推导项目名，
/// 而日志按「年/月/日」分层存放，界面上因此显示成了日期（如「25」）。
/// </remarks>
public class ProjectResolverTests
{
    [Fact]
    public void UsesLastSegmentOfWindowsPath()
    {
        var info = ProjectResolver.Resolve(@"E:\Code\scs-3d-reconstruction");

        Assert.Equal("scs-3d-reconstruction", info.Name);
        Assert.Equal(@"E:\Code\scs-3d-reconstruction", info.Root);
    }

    [Fact]
    public void HandlesForwardSlashes()
    {
        var info = ProjectResolver.Resolve("C:/Users/admin/Documents/AI-General");

        Assert.Equal("AI-General", info.Name);
    }

    [Fact]
    public void TrimsTrailingSeparator()
    {
        var info = ProjectResolver.Resolve(@"E:\Code\demo\");

        Assert.Equal("demo", info.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsUnknownForMissingDirectory(string? path)
    {
        Assert.Null(ProjectResolver.Resolve(path).Name);
    }

    [Fact]
    public void RejectsCodexVisualizationDirectory()
    {
        // workspace_roots 里可能出现 Codex 自动生成的辅助目录，
        // 它们不是用户的项目。
        var info = ProjectResolver.Resolve(
            @"C:\Users\example\.codex\visualizations\2026\09\25\01a0d656-cf19");

        Assert.Null(info.Name);
    }

    [Fact]
    public void RejectsCodexAttachmentsDirectory()
    {
        var info = ProjectResolver.Resolve(@"C:\Users\example\.codex\attachments\abc");

        Assert.Null(info.Name);
    }

    [Fact]
    public void RejectsCodexCacheDirectory()
    {
        var info = ProjectResolver.Resolve(@"C:\Users\example\.codex\cache\xyz");

        Assert.Null(info.Name);
    }

    [Fact]
    public void AcceptsDirectoryThatMerelyContainsCodexInName()
    {
        // 只排除 Codex 的辅助目录，不应误伤名字相近的用户项目。
        var info = ProjectResolver.Resolve(@"E:\Code\.codex-tools");

        Assert.Equal(".codex-tools", info.Name);
    }

    [Fact]
    public void DriveRootFallsBackToFullPath()
    {
        // 盘符根目录没有「名字」，显示完整路径而不是空。
        var info = ProjectResolver.Resolve(@"E:\");

        Assert.Equal("E:", info.Name);
    }

    [Fact]
    public void PathIsCasePreserved()
    {
        var info = ProjectResolver.Resolve(@"E:\Code\MyProject");

        Assert.Equal("MyProject", info.Name);
    }

    [Fact]
    public void DeepNestedPathUsesLastSegment()
    {
        var info = ProjectResolver.Resolve(@"E:\Code\org\team\service\api");

        Assert.Equal("api", info.Name);
        Assert.Equal(@"E:\Code\org\team\service\api", info.Root);
    }
}

