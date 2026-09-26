using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 活动会话追踪的行为验证。
/// 场景取自对真实 IPC 的观测：切换任务时会连续发出
/// following=false 与 following=true 两条广播，间隔约 4ms。
/// </summary>
public class ActiveConversationTrackerTests
{
    private const string WindowA = "client-a";
    private const string WindowB = "client-b";
    private const string Host = "local";

    [Fact]
    public void InitialStateHasNoActiveConversation()
    {
        var tracker = new ActiveConversationTracker();

        Assert.Null(tracker.ActiveConversationId);
        Assert.Equal(0, tracker.ActiveWindowCount);
    }

    [Fact]
    public void FollowingTrueSetsActiveConversation()
    {
        var tracker = new ActiveConversationTracker();

        var changed = tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);

        Assert.True(changed);
        Assert.Equal("conv-1", tracker.ActiveConversationId);
        Assert.Equal(1, tracker.ActiveWindowCount);
    }

    [Fact]
    public void RealSwitchSequenceMovesActiveConversation()
    {
        // 实测序列：旧会话 false 先到，新会话 true 后到，间隔约 4ms。
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-old", following: true, Host, WindowA);

        var onUnfollow = tracker.ApplyFollowingChanged("conv-old", following: false, Host, WindowA);
        var onFollow = tracker.ApplyFollowingChanged("conv-new", following: true, Host, WindowA);

        Assert.True(onUnfollow);
        Assert.True(onFollow);
        Assert.Equal("conv-new", tracker.ActiveConversationId);
        Assert.Equal(1, tracker.ActiveWindowCount);
    }

    [Fact]
    public void UnfollowOnlyAffectsTheMatchingConversation()
    {
        // 过期的 false 广播不应影响已经切到别的会话的窗口。
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-new", following: true, Host, WindowA);

        var changed = tracker.ApplyFollowingChanged("conv-stale", following: false, Host, WindowA);

        Assert.False(changed);
        Assert.Equal("conv-new", tracker.ActiveConversationId);
    }

    [Fact]
    public void UnfollowForUnknownWindowIsIgnored()
    {
        var tracker = new ActiveConversationTracker();

        var changed = tracker.ApplyFollowingChanged("conv-1", following: false, Host, WindowA);

        Assert.False(changed);
        Assert.Null(tracker.ActiveConversationId);
    }

    [Fact]
    public void RepeatedFollowForSameConversationReportsNoChange()
    {
        // 重复广播同一会话不应触发无意义的状态变更通知。
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);

        var changed = tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);

        Assert.False(changed);
    }

    [Fact]
    public void MultipleWindowsTrackIndependentConversations()
    {
        var tracker = new ActiveConversationTracker();

        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);
        tracker.ApplyFollowingChanged("conv-2", following: true, Host, WindowB);

        Assert.Equal(2, tracker.ActiveWindowCount);
        // 多窗口时取最后开始跟随的会话。
        Assert.Equal("conv-2", tracker.ActiveConversationId);
    }

    [Fact]
    public void ClosingLatestWindowFallsBackToEarlierWindow()
    {
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);
        tracker.ApplyFollowingChanged("conv-2", following: true, Host, WindowB);

        var changed = tracker.ApplyFollowingChanged("conv-2", following: false, Host, WindowB);

        Assert.True(changed);
        Assert.Equal("conv-1", tracker.ActiveConversationId);
    }

    [Fact]
    public void SameWindowOnDifferentHostsIsTrackedSeparately()
    {
        // 窗口键由 sourceClientId 与 hostId 共同决定。
        var tracker = new ActiveConversationTracker();

        tracker.ApplyFollowingChanged("conv-remote", following: true, "remote-host", WindowA);
        tracker.ApplyFollowingChanged("conv-local", following: true, Host, WindowA);

        Assert.Equal(2, tracker.ActiveWindowCount);
        Assert.Equal("conv-local", tracker.ActiveConversationId);
    }

    [Fact]
    public void ClientDisconnectedRemovesAllItsWindows()
    {
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);
        tracker.ApplyFollowingChanged("conv-2", following: true, "remote-host", WindowA);

        var changed = tracker.ApplyClientDisconnected(WindowA);

        Assert.True(changed);
        Assert.Null(tracker.ActiveConversationId);
        Assert.Equal(0, tracker.ActiveWindowCount);
    }

    [Fact]
    public void ClientDisconnectedForUnknownClientIsIgnored()
    {
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);

        var changed = tracker.ApplyClientDisconnected("unknown-client");

        Assert.False(changed);
        Assert.Equal("conv-1", tracker.ActiveConversationId);
    }

    [Fact]
    public void ClientDisconnectedDoesNotMatchSimilarClientIds()
    {
        // 前缀匹配必须带分隔符，否则 "client-a" 会误伤 "client-ab"。
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, "client-ab");
        tracker.ApplyFollowingChanged("conv-2", following: true, Host, "client-a");

        tracker.ApplyClientDisconnected("client-a");

        Assert.Equal(1, tracker.ActiveWindowCount);
        Assert.Equal("conv-1", tracker.ActiveConversationId);
    }

    [Fact]
    public void ResetClearsEverythingAndReportsChange()
    {
        var tracker = new ActiveConversationTracker();
        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);

        var changed = tracker.Reset();

        Assert.True(changed);
        Assert.Null(tracker.ActiveConversationId);
        Assert.Equal(0, tracker.ActiveWindowCount);
    }

    [Fact]
    public void ResetOnEmptyStateReportsNoChange()
    {
        var tracker = new ActiveConversationTracker();
        Assert.False(tracker.Reset());
    }

    [Fact]
    public void VersionIncrementsOnEveryActiveConversationChange()
    {
        var tracker = new ActiveConversationTracker();
        var initial = tracker.Version;

        tracker.ApplyFollowingChanged("conv-1", following: true, Host, WindowA);
        var afterFirst = tracker.Version;
        tracker.ApplyFollowingChanged("conv-2", following: true, Host, WindowA);
        var afterSecond = tracker.Version;

        Assert.True(afterFirst > initial);
        Assert.True(afterSecond > afterFirst);
    }

    [Theory]
    [InlineData("", true, Host, WindowA)]
    [InlineData("conv-1", true, "", WindowA)]
    [InlineData("conv-1", true, Host, "")]
    [InlineData("   ", true, Host, WindowA)]
    public void BlankIdentifiersAreRejected(string conversationId, bool following, string hostId, string clientId)
    {
        var tracker = new ActiveConversationTracker();

        var changed = tracker.ApplyFollowingChanged(conversationId, following, hostId, clientId);

        Assert.False(changed);
        Assert.Null(tracker.ActiveConversationId);
    }
}
