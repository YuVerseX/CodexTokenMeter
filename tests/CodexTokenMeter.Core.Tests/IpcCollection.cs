namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 依赖真实命名管道与计时的测试集合。
/// </summary>
/// <remarks>
/// xUnit 默认并行执行不同测试类；这些测试会创建同名管道服务器、
/// 并依赖墙钟超时，并行时会互相干扰而出现假失败。
/// 归入同一集合后它们串行执行。
/// </remarks>
[CollectionDefinition("IPC", DisableParallelization = true)]
public sealed class IpcCollection;
