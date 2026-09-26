using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// 开发期探针：观测 Codex Desktop 的 IPC 帧协议。
// 只读连接，不发送任何会改变 Codex 状态的消息。
// 不参与发布产物。
//
// 用法：dotnet run --project tools/CodexTokenMeter.IpcProbe -- <秒数> [日志路径]

var deadline = args.Length > 0 ? TimeSpan.FromSeconds(int.Parse(args[0])) : TimeSpan.FromSeconds(20);
var logPath = args.Length > 1 ? args[1] : null;

// 同时写控制台与日志文件：后台运行时可事后核对完整过程。
var log = logPath is null
    ? null
    : new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

void Report(string message)
{
    Console.WriteLine(message);
    log?.WriteLine(message);
}

Report($"开始观测，时长 {deadline.TotalSeconds:F0}s");
Report("连接 \\.\\pipe\\codex-ipc ...");

using var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);

try
{
    await pipe.ConnectAsync(3000);
}
catch (Exception exception)
{
    Report($"连接失败：{exception.Message}");
    Report("请确认 Codex Desktop 正在运行。");
    log?.Dispose();
    return 1;
}

Report("已连接。发送 initialize 握手...");

await SendInitializeAsync(pipe);

var frames = 0;
var methodCounts = new Dictionary<string, int>(StringComparer.Ordinal);
string? sampleThread = null;

using var cts = new CancellationTokenSource(deadline);

try
{
    while (!cts.IsCancellationRequested && pipe.IsConnected)
    {
        var prefix = new byte[4];
        if (!await ReadExactlyAsync(pipe, prefix, cts.Token))
        {
            Report("流已结束。");
            break;
        }

        var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (frameLength == 0 || frameLength > 4 * 1024 * 1024)
        {
            Report($"帧长度异常：{frameLength}，停止。");
            break;
        }

        var payload = new byte[frameLength];
        if (!await ReadExactlyAsync(pipe, payload, cts.Token))
        {
            break;
        }

        frames++;
        Inspect(payload, frames, methodCounts, ref sampleThread, Report);
    }
}
catch (OperationCanceledException)
{
    Report($"（达到观测时长 {deadline.TotalSeconds:F0}s）");
}

Report(string.Empty);
Report($"共收到 {frames} 帧。");

if (methodCounts.Count > 0)
{
    Report(string.Empty);
    Report("=== 方法分布 ===");
    foreach (var (method, count) in methodCounts.OrderByDescending(pair => pair.Value))
    {
        Report($"  {count,5}  {method}");
    }
}

if (sampleThread is not null)
{
    Report(string.Empty);
    Report($"首个观测到的 threadId：{sampleThread}");
}

log?.Dispose();
return frames > 0 ? 0 : 2;

static async Task SendInitializeAsync(Stream pipe)
{
    var request = new
    {
        type = "request",
        requestId = Guid.NewGuid().ToString(),
        sourceClientId = "codex-token-meter-probe",
        version = 0,
        method = "initialize",
        @params = new { clientType = "codex-token-meter" },
    };

    var payload = JsonSerializer.SerializeToUtf8Bytes(request);
    var prefix = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

    await pipe.WriteAsync(prefix);
    await pipe.WriteAsync(payload);
    await pipe.FlushAsync();
}

static void Inspect(
    byte[] payload,
    int index,
    Dictionary<string, int> methodCounts,
    ref string? sampleThread,
    Action<string> report)
{
    try
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;

        var key = $"{type}/{method}";
        methodCounts[key] = methodCounts.GetValueOrDefault(key) + 1;

        if (method == "thread-stream-following-changed")
        {
            // 会话跟随广播是核心关注点，逐条完整打印并标注时间。
            report($"[{DateTime.Now:HH:mm:ss.fff}] FOLLOWING  {Encoding.UTF8.GetString(payload)}");
        }
        else if (index <= 4)
        {
            // 只在前几帧打印完整结构，用于确定字段形状。
            report($"--- 帧 {index}  {key} ---");
            var text = Encoding.UTF8.GetString(payload);
            report(text.Length > 900 ? text[..900] + " ...(截断)" : text);
            report(string.Empty);
        }

        if (root.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "conversationId", "threadId", "thread_id" })
            {
                if (parameters.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    sampleThread ??= value.GetString();
                }
            }
        }
    }
    catch (JsonException)
    {
        methodCounts["<无法解析>"] = methodCounts.GetValueOrDefault("<无法解析>") + 1;
    }
}

static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (read <= 0)
        {
            return false;
        }

        offset += read;
    }

    return true;
}
