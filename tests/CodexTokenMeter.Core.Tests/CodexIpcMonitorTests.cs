using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 以一个模拟 Codex 的命名管道服务器驱动真实的 IPC 客户端，
/// 覆盖帧解析、状态通知、重连与断开等无法靠真实 Codex 稳定复现的路径。
/// </summary>
/// <remarks>
/// 这些测试依赖真实管道与定时，并发执行会相互干扰，
/// 因此单独归为一组串行运行。
/// </remarks>
[Collection("IPC")]
public sealed class CodexIpcMonitorTests : IAsyncLifetime
{
    private readonly string _pipeName = "codex-token-meter-test-" + Guid.NewGuid().ToString("N");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConnectsAndReceivesInitializeRequest()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));

        // 首个请求必须是 initialize 握手。
        var handshake = await ReadFrameAsync(server, TimeSpan.FromSeconds(10));
        Assert.Equal("initialize", handshake.GetProperty("method").GetString());
        Assert.Equal("request", handshake.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReportsConnectedAfterHandshake()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        var status = await WaitForStatusAsync(monitor, s => s.IsConnected, TimeSpan.FromSeconds(10));

        Assert.True(status.IsConnected);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task TracksFollowingChangedBroadcast()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(server, "conv-1", following: true, "client-a", "local");

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-1", TimeSpan.FromSeconds(10));

        Assert.Equal("conv-1", status.ActiveConversationId);
        Assert.Equal(1, status.ActiveWindowCount);
    }

    [Fact]
    public async Task TracksRealSwitchSequence()
    {
        // 实测序列：旧会话 false 与新会话 true 相继到达。
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(server, "conv-old", following: true, "client-a", "local");
        await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-old", TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(server, "conv-old", following: false, "client-a", "local");
        await WriteFollowingChangedAsync(server, "conv-new", following: true, "client-a", "local");

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-new", TimeSpan.FromSeconds(10));

        Assert.Equal("conv-new", status.ActiveConversationId);
        Assert.Equal(1, status.ActiveWindowCount);
    }

    [Fact]
    public async Task IgnoresUnknownBroadcastMethods()
    {
        // Codex 会新增消息类型，未知类型不应中断监听。
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        await WriteFrameAsync(server, new
        {
            type = "broadcast",
            method = "some-future-method",
            @params = new { anything = 1 },
        });
        await WriteFollowingChangedAsync(server, "conv-after", following: true, "client-a", "local");

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-after", TimeSpan.FromSeconds(10));

        Assert.Equal("conv-after", status.ActiveConversationId);
    }

    [Fact]
    public async Task SurvivesMalformedFrame()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        // 长度合法但内容不是 JSON。
        var garbage = Encoding.UTF8.GetBytes("这不是 JSON");
        await WriteRawFrameAsync(server, garbage);

        await WriteFollowingChangedAsync(server, "conv-after", following: true, "client-a", "local");

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-after", TimeSpan.FromSeconds(10));

        Assert.Equal("conv-after", status.ActiveConversationId);
    }

    [Fact]
    public async Task HandlesClientDisconnectedBroadcast()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(server, "conv-1", following: true, "client-a", "local");
        await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-1", TimeSpan.FromSeconds(10));

        await WriteFrameAsync(server, new
        {
            type = "broadcast",
            method = "client-status-changed",
            @params = new { clientId = "client-a", status = "disconnected" },
        });

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId is null, TimeSpan.FromSeconds(10));

        Assert.Null(status.ActiveConversationId);
    }

    [Fact]
    public async Task ClearsActiveConversationWhenServerCloses()
    {
        // 断开时必须清空会话，否则浮层会继续显示已经失效的会话。
        var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(server, "conv-1", following: true, "client-a", "local");
        await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-1", TimeSpan.FromSeconds(10));

        await server.DisposeAsync();

        // 断开由后台读取循环检测，需要等待其收敛。
        var status = await WaitForStatusAsync(monitor, s => !s.IsConnected, TimeSpan.FromSeconds(15));
        var cleared = await WaitForStatusAsync(monitor, s => s.ActiveConversationId is null, TimeSpan.FromSeconds(5));

        Assert.False(status.IsConnected);
        Assert.Null(cleared.ActiveConversationId);
    }

    [Fact]
    public async Task ReconnectsAfterServerRestarts()
    {
        // Codex 重启后客户端必须能自行恢复，无需重启浮层。
        var firstServer = CreateServer();
        var firstAccepted = firstServer.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await firstAccepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(firstServer, TimeSpan.FromSeconds(10));
        await firstServer.DisposeAsync();

        await WaitForStatusAsync(monitor, s => !monitor.GetStatus().IsConnected, TimeSpan.FromSeconds(15));

        // 重启服务器，等待客户端自行重连。
        await using var secondServer = CreateServer();
        var secondAccepted = secondServer.WaitForConnectionAsync();
        await secondAccepted.WaitAsync(TimeSpan.FromSeconds(20));
        await ReadFrameAsync(secondServer, TimeSpan.FromSeconds(10));

        await WriteFollowingChangedAsync(secondServer, "conv-restored", following: true, "client-a", "local");

        var status = await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-restored", TimeSpan.FromSeconds(20));

        Assert.True(status.IsConnected);
        Assert.Equal("conv-restored", status.ActiveConversationId);
    }

    [Fact]
    public async Task RaisesStatusChangedEvent()
    {
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        var notifications = 0;
        monitor.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);

        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await ReadFrameAsync(server, TimeSpan.FromSeconds(10));
        await WriteFollowingChangedAsync(server, "conv-1", following: true, "client-a", "local");

        await WaitForStatusAsync(monitor, s => s.ActiveConversationId == "conv-1", TimeSpan.FromSeconds(10));

        // 通知在后台线程发出，需等待其到达后再断言计数。
        await WaitForConditionAsync(
            () => Volatile.Read(ref notifications) > 0,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DoesNotSendAnythingBeyondInitialize()
    {
        // 只读约束：握手之外不应向 Codex 发送任何消息。
        await using var server = CreateServer();
        var accepted = server.WaitForConnectionAsync();

        using var monitor = new CodexIpcMonitor(_pipeName);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));

        var handshake = await ReadFrameAsync(server, TimeSpan.FromSeconds(10));
        Assert.Equal("initialize", handshake.GetProperty("method").GetString());

        // 触发若干状态变化。
        await WriteFollowingChangedAsync(server, "conv-1", following: true, "client-a", "local");
        await WriteFollowingChangedAsync(server, "conv-1", following: false, "client-a", "local");
        await Task.Delay(500);

        // 服务器端不应再有可读数据。用极短超时探测。
        using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var buffer = new byte[1024];
        var extra = 0;

        try
        {
            extra = await server.ReadAsync(buffer, probe.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期：无更多数据。
        }

        Assert.Equal(0, extra);
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static async Task WriteFollowingChangedAsync(
        Stream server,
        string conversationId,
        bool following,
        string sourceClientId,
        string hostId)
    {
        await WriteFrameAsync(server, new
        {
            type = "broadcast",
            method = "thread-stream-following-changed",
            sourceClientId,
            @params = new { conversationId, hostId, following },
            version = 1,
        });
    }

    private static async Task WriteFrameAsync(Stream server, object payload)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteRawFrameAsync(server, bytes);
    }

    private static async Task WriteRawFrameAsync(Stream server, byte[] payload)
    {
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await server.WriteAsync(prefix);
        await server.WriteAsync(payload);
        await server.FlushAsync();
    }

    private static async Task<System.Text.Json.JsonElement> ReadFrameAsync(
        Stream server,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        var prefix = new byte[sizeof(uint)];
        await ReadExactlyAsync(server, prefix, cts.Token);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var payload = new byte[length];
        await ReadExactlyAsync(server, payload, cts.Token);

        // JsonDocument 释放后其元素不可用，因此这里克隆出独立副本。
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read <= 0)
            {
                throw new IOException("流提前结束。");
            }

            offset += read;
        }
    }

    /// <summary>轮询等待状态条件成立，返回成立时的状态快照。</summary>
    private static async Task<IpcConnectionStatus> WaitForStatusAsync(
        CodexIpcMonitor monitor,
        Func<IpcConnectionStatus, bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var status = monitor.GetStatus();
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"等待条件在 {timeout.TotalSeconds:F0} 秒内未满足。");
    }

    /// <summary>轮询等待布尔条件成立，用于事件计数等非状态量。</summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"等待条件在 {timeout.TotalSeconds:F0} 秒内未满足。");
    }
}

