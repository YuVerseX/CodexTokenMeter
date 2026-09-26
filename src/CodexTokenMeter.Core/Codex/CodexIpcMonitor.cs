using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 只读订阅 Codex Desktop 的本地 IPC，追踪当前跟随的会话。
/// </summary>
/// <remarks>
/// <para>
/// 仅连接并监听广播，不发送任何会改变 Codex 状态的消息。
/// Codex 未运行时自动重试，不影响其它功能。
/// </para>
/// <para>
/// 帧格式（实测）：4 字节小端长度前缀 + UTF-8 JSON 负载。
/// </para>
/// <para>
/// 本类线程安全；内部状态由锁保护，事件在后台线程触发。
/// </para>
/// </remarks>
public sealed class CodexIpcMonitor : IDisposable
{
    /// <summary>Windows 上的管道名称。</summary>
    public const string DefaultPipeName = "codex-ipc";

    /// <summary>单帧上限。超过此值的帧按异常处理并重连。</summary>
    private const int MaximumJsonFrameBytes = 4 * 1024 * 1024;

    /// <summary>连接超时。</summary>
    private const int ConnectTimeoutMilliseconds = 2500;

    private readonly object _sync = new();
    private readonly ActiveConversationTracker _tracker = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _runner;
    private readonly string _pipeName;

    private bool _isConnected;
    private string? _lastError;
    private long _framesReceived;

    /// <param name="pipeName">管道名称，测试时可覆盖。</param>
    public CodexIpcMonitor(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
        _runner = Task.Run(() => RunAsync(_cancellation.Token));
    }

    /// <summary>连接状态或活动会话变化时触发。</summary>
    public event EventHandler? StatusChanged;

    /// <summary>取得当前状态的快照。</summary>
    public IpcConnectionStatus GetStatus()
    {
        lock (_sync)
        {
            return new IpcConnectionStatus
            {
                IsConnected = _isConnected,
                ActiveConversationId = _tracker.ActiveConversationId,
                Version = _tracker.Version,
                ActiveWindowCount = _tracker.ActiveWindowCount,
                LastError = _lastError,
                FramesReceived = _framesReceived,
            };
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            // 不在 UI 线程上无限等待：Codex 可能长时间不发数据。
            _runner.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 取消导致的异常属预期。
        }

        _cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(350);

        while (!cancellationToken.IsCancellationRequested)
        {
            // 本次连接结束的原因。为 null 表示对端正常关闭。
            //
            // 必须在循环内声明：若在 catch 里置为断开原因，
            // 而循环尾又无条件调用 SetDisconnected(null)，
            // 会把原因覆盖成“正常断开”，诊断信息就丢了。
            string? disconnectReason = null;

            try
            {
                await RunSessionAsync(cancellationToken).ConfigureAwait(false);

                // 正常返回意味着对端关闭了连接，重新开始。
                retryDelay = TimeSpan.FromMilliseconds(350);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or TimeoutException)
            {
                disconnectReason = $"{exception.GetType().Name}：{exception.Message}";
            }

            SetDisconnected(disconnectReason);

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 指数退避，上限 5 秒：Codex 长期未运行时不应持续高频重试。
            retryDelay = TimeSpan.FromMilliseconds(
                Math.Min(5000, retryDelay.TotalMilliseconds * 2));
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        SetConnected();

        await SendInitializeAsync(pipe, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var prefix = new byte[sizeof(uint)];
            if (!await ReadExactlyAsync(pipe, prefix, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (frameLength == 0 || frameLength > MaximumJsonFrameBytes)
            {
                throw new InvalidDataException($"Codex IPC 帧长度无效：{frameLength}");
            }

            var payload = new byte[frameLength];
            if (!await ReadExactlyAsync(pipe, payload, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            ProcessFrame(payload);
        }
    }

    private static async Task SendInitializeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var request = new
        {
            type = "request",
            requestId = Guid.NewGuid().ToString(),
            sourceClientId = "codex-token-meter",
            version = 0,
            method = "initialize",
            @params = new { clientType = "codex-token-meter" },
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await pipe.WriteAsync(prefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析一帧广播并更新追踪状态。
    /// </summary>
    /// <remarks>
    /// 无法识别的帧直接忽略：Codex 会新增消息类型，
    /// 未知类型不应中断监听。
    /// </remarks>
    private void ProcessFrame(byte[] payload)
    {
        var changed = false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            lock (_sync)
            {
                _framesReceived++;
            }

            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeElement.GetString();

            // initialize 的响应带 resultType 与 handledByClientId，不需要处理，
            // 但记录一次连接成功是有意义的。
            if (type == "response")
            {
                return;
            }

            if (type != "broadcast"
                || !root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (methodElement.GetString())
            {
                case "thread-stream-following-changed":
                    changed = HandleFollowingChanged(root, parameters);
                    break;

                case "client-status-changed":
                    changed = HandleClientStatusChanged(root, parameters);
                    break;

                default:
                    break;
            }
        }
        catch (JsonException)
        {
            // 单帧畸形不影响后续帧。
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HandleFollowingChanged(JsonElement root, JsonElement parameters)
    {
        var conversationId = ReadString(parameters, "conversationId");
        var hostId = ReadString(parameters, "hostId");
        var sourceClientId = ReadString(root, "sourceClientId");

        if (conversationId is null
            || hostId is null
            || sourceClientId is null
            || !parameters.TryGetProperty("following", out var followingElement)
            || followingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        var following = followingElement.GetBoolean();

        lock (_sync)
        {
            return _tracker.ApplyFollowingChanged(conversationId, following, hostId, sourceClientId);
        }
    }

    private bool HandleClientStatusChanged(JsonElement root, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || statusElement.GetString() != "disconnected")
        {
            return false;
        }

        var clientId = ReadString(parameters, "clientId");
        if (clientId is null)
        {
            return false;
        }

        lock (_sync)
        {
            return _tracker.ApplyClientDisconnected(clientId);
        }
    }

    private void SetConnected()
    {
        lock (_sync)
        {
            if (_isConnected)
            {
                return;
            }

            _isConnected = true;
            _lastError = null;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDisconnected(string? error)
    {
        var notify = false;

        lock (_sync)
        {
            // 断开时清空活动会话：保留旧值会让浮层继续显示已经失效的会话。
            var cleared = _tracker.Reset();

            if (_isConnected || cleared || !string.Equals(_lastError, error, StringComparison.Ordinal))
            {
                _isConnected = false;
                _lastError = error;
                notify = true;
            }
        }

        if (notify)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
