using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 会话日志文件名匹配。
/// </summary>
/// <remarks>
/// 回归保护：Codex 会产生「分叉会话」，文件名把父会话 id 放在前面、
/// 子 id 放后面（下划线分隔），而 IPC 广播的 conversationId 仍是父 id。
///
/// 早期版本只做 <c>EndsWith(id + ".jsonl")</c> 匹配，因此只会命中父文件，
/// 浮层会跟随一个早已结束的旧会话——实测显示的是前一天的累计值
/// （32,564,280），而实际活动会话只有 1,349,335。
/// </remarks>
public class SessionFileMatchingTests
{
    private const string ParentId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string ChildId = "01a0db49-da6c-7f43-ac7f-014858381ce4";

    private static string ParentFile =>
        $@"C:\sessions\2026\09\25\rollout-2026-09-25T10-13-28-{ParentId}.jsonl";

    private static string ForkFile =>
        $@"C:\sessions\2026\09\26\rollout-2026-09-26T09-17-25-{ParentId}_{ChildId}.jsonl";

    [Fact]
    public void MatchesPlainSessionFile()
    {
        Assert.True(SessionPathResolver.MatchesThreadId(ParentFile, ParentId));
    }

    [Fact]
    public void MatchesForkSessionFileByParentId()
    {
        // 核心修复：分叉文件名含父 id，必须能匹配到。
        Assert.True(SessionPathResolver.MatchesThreadId(ForkFile, ParentId));
    }

    [Fact]
    public void MatchesForkSessionFileByChildIdIsNotRequired()
    {
        // 子 id 不在前缀段起点（前面是 '_' 而非 '-'），因此不匹配。
        // 这是刻意的：IPC 只广播父 id，浮层不应通过子 id 定位会话。
        Assert.False(SessionPathResolver.MatchesThreadId(ForkFile, ChildId));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.True(SessionPathResolver.MatchesThreadId(
            ForkFile,
            ParentId.ToUpperInvariant()));
    }

    [Fact]
    public void RejectsUnrelatedSessionFile()
    {
        const string other = "01a0d650-7d90-7ef0-a9ed-36c75bec684b";
        var file = $@"C:\sessions\rollout-2026-09-25T10-06-34-{other}.jsonl";

        Assert.False(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Fact]
    public void RejectsPartialIdMatch()
    {
        // id 必须是完整的段：后缀多出字符时不应匹配。
        var file = $@"C:\sessions\rollout-2026-09-25T10-13-28-{ParentId}extra.jsonl";

        Assert.False(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Fact]
    public void RejectsIdEmbeddedInMiddleOfLongerSegment()
    {
        // 前一个字符不是 '-'：说明 id 出现在段的中间，不是段首。
        var file = $@"C:\sessions\rollout-2026-09-25T10-13-28-xx{ParentId}.jsonl";

        Assert.False(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Fact]
    public void RejectsNonRolloutFileName()
    {
        var file = $@"C:\sessions\config-{ParentId}.jsonl";

        // 前缀不是 rollout-：Codex 的会话日志总是这个前缀，
        // 其它文件即使含相同 id 也不应被当作会话。
        Assert.False(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Fact]
    public void RejectsNonJsonlExtension()
    {
        var file = $@"C:\sessions\rollout-2026-09-25T10-13-28-{ParentId}.txt";

        Assert.False(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankThreadId(string threadId)
    {
        Assert.False(SessionPathResolver.MatchesThreadId(ParentFile, threadId));
    }

    [Fact]
    public void RejectsBlankPath()
    {
        Assert.False(SessionPathResolver.MatchesThreadId(string.Empty, ParentId));
    }

    [Fact]
    public void HandlesDeeplyNestedPaths()
    {
        var file = $@"C:\a\b\c\d\e\f\rollout-2026-09-26T09-17-25-{ParentId}_{ChildId}.jsonl";

        Assert.True(SessionPathResolver.MatchesThreadId(file, ParentId));
    }

    [Fact]
    public void BothParentAndForkMatchSoCallerCanPickNewest()
    {
        // 两种文件都可能存在，调用方按写入时间取最新的一份。
        // 分叉后父文件不再变化，分叉文件持续写入，因此这样是可靠的。
        Assert.True(SessionPathResolver.MatchesThreadId(ParentFile, ParentId));
        Assert.True(SessionPathResolver.MatchesThreadId(ForkFile, ParentId));
    }
}
