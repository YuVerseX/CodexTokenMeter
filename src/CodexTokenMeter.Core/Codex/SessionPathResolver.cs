namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 解析 Codex 会话日志的根目录。
/// </summary>
/// <remarks>
/// 解析顺序：
/// <list type="number">
/// <item><c>--sessions &lt;路径&gt;</c> 命令行参数，供开发与测试使用；</item>
/// <item><c>CODEX_HOME</c> 环境变量下的 <c>sessions</c> 目录；</item>
/// <item>默认的 <c>~/.codex/sessions</c>。</item>
/// </list>
/// </remarks>
public static class SessionPathResolver
{
    /// <summary>命令行参数名。</summary>
    public const string SessionsArgument = "--sessions";

    /// <summary>环境变量名。</summary>
    public const string CodexHomeVariable = "CODEX_HOME";

    /// <summary>按上述顺序解析会话根目录。</summary>
    /// <param name="arguments">命令行参数。为 null 时只检查环境变量与默认值。</param>
    /// <param name="environment">环境变量读取函数，便于测试注入。</param>
    /// <param name="userProfile">用户主目录，便于测试注入。</param>
    public static string Resolve(
        IReadOnlyList<string>? arguments = null,
        Func<string, string?>? environment = null,
        string? userProfile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals(SessionsArgument, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Normalize(arguments[index + 1], userProfile);
                }
            }
        }

        var codexHome = environment(CodexHomeVariable);
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(userProfile, ".codex");
        }

        return Path.Combine(Normalize(codexHome, userProfile), "sessions");
    }

    /// <summary>
    /// 判断文件是否是给定 thread id 的会话日志。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Codex 有两种文件名：
    /// <list type="bullet">
    /// <item><c>rollout-&lt;时间&gt;-&lt;id&gt;.jsonl</c> —— 普通会话</item>
    /// <item><c>rollout-&lt;时间&gt;-&lt;id&gt;_&lt;子id&gt;.jsonl</c> —— 分叉会话</item>
    /// </list>
    /// </para>
    /// <para>
    /// 分叉（fork）由「在新窗口继续此会话」类操作产生。它的文件名把父会话 id
    /// 放在前面、子 id 放后面，而 IPC 广播的 conversationId 仍是**父 id**。
    /// 不能只做 <c>EndsWith(id + ".jsonl")</c>：那样只会命中父文件，
    /// 浮层会跟随一个早已结束的旧会话（实测显示的是前一天的累计值）。
    /// </para>
    /// </remarks>
    /// <param name="path">日志文件路径。</param>
    /// <param name="threadId">要匹配的会话标识。</param>
    public static bool MatchesThreadId(string path, string threadId)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        var name = Path.GetFileName(path);

        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var withoutExtension = name[..^".jsonl".Length];
        var index = withoutExtension.IndexOf(threadId, StringComparison.OrdinalIgnoreCase);

        while (index >= 0)
        {
            var next = index + threadId.Length;

            // id 之后只能是字符串结尾（普通会话）或 '_'（分叉的子 id 分隔符）。
            var endsHere = next == withoutExtension.Length || withoutExtension[next] == '_';

            // 且它必须位于 id 段起点：前一字符是前缀末尾的 '-'。
            var startsHere = index > 0 && withoutExtension[index - 1] == '-';

            if (endsHere && startsHere)
            {
                return true;
            }

            index = withoutExtension.IndexOf(threadId, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// 归一化路径：展开环境变量、<c>~</c> 与引号，并转为绝对路径。
    /// </summary>
    internal static string Normalize(string path, string userProfile)
    {
        var expanded = Environment
            .ExpandEnvironmentVariables(path.Trim().Trim('"'))
            .Trim();

        if (expanded == "~")
        {
            expanded = userProfile;
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal)
            || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(userProfile, expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}
