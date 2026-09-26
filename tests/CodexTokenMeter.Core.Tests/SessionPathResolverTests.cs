using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

public class SessionPathResolverTests
{
    private const string Profile = @"C:\Users\tester";

    [Fact]
    public void CommandLineArgumentTakesPrecedence()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--sessions", @"D:\custom\sessions"],
            environment: _ => @"E:\ignored",
            userProfile: Profile);

        Assert.Equal(@"D:\custom\sessions", resolved);
    }

    [Fact]
    public void CommandLineArgumentIsCaseInsensitive()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--SESSIONS", @"D:\custom"],
            environment: _ => null,
            userProfile: Profile);

        Assert.Equal(@"D:\custom", resolved);
    }

    [Fact]
    public void CommandLineArgumentWithoutValueIsIgnored()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--sessions"],
            environment: _ => null,
            userProfile: Profile);

        Assert.Equal(@"C:\Users\tester\.codex\sessions", resolved);
    }

    [Fact]
    public void CodexHomeEnvironmentVariableIsUsed()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: null,
            environment: name => name == SessionPathResolver.CodexHomeVariable ? @"D:\codex-home" : null,
            userProfile: Profile);

        Assert.Equal(@"D:\codex-home\sessions", resolved);
    }

    [Fact]
    public void BlankCodexHomeFallsBackToDefault()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: null,
            environment: _ => "   ",
            userProfile: Profile);

        Assert.Equal(@"C:\Users\tester\.codex\sessions", resolved);
    }

    [Fact]
    public void DefaultIsDotCodexUnderUserProfile()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: null,
            environment: _ => null,
            userProfile: Profile);

        Assert.Equal(@"C:\Users\tester\.codex\sessions", resolved);
    }

    [Fact]
    public void TildeIsExpandedToUserProfile()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--sessions", @"~\.codex\sessions"],
            environment: _ => null,
            userProfile: Profile);

        Assert.Equal(@"C:\Users\tester\.codex\sessions", resolved);
    }

    [Fact]
    public void QuotesAreStripped()
    {
        // 命令行参数常带引号，尤其路径含空格时。
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--sessions", @"""D:\my sessions"""],
            environment: _ => null,
            userProfile: Profile);

        Assert.Equal(@"D:\my sessions", resolved);
    }

    [Fact]
    public void RelativePathIsResolvedToAbsolute()
    {
        var resolved = SessionPathResolver.Resolve(
            arguments: ["--sessions", "relative\\sessions"],
            environment: _ => null,
            userProfile: Profile);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.EndsWith("relative\\sessions", resolved);
    }
}
