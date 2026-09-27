using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenMeter.Core.Pricing;

/// <summary>
/// 价格表加载结果。
/// </summary>
/// <param name="Catalog">生效的价格表。加载失败时为内置表。</param>
/// <param name="Warning">
/// 需要告知用户的问题。加载正常时为 null。
/// </param>
/// <param name="AppliedModelCount">从覆盖文件成功应用的模型数。</param>
/// <remarks>
/// 把「加载了但有话要说」作为返回值而不是异常：
/// 价格表是可选配置，其问题不应阻断启动，但也不该静默。
/// 一个拼错的价格文件会让费用显示为内置价，用户需要知道这件事。
/// </remarks>
public sealed record PricingLoadResult(
    PricingCatalog Catalog,
    string? Warning,
    int AppliedModelCount = 0)
{
    /// <summary>加载过程是否完全正常。</summary>
    public bool IsClean => Warning is null;
}

/// <summary>
/// 模型价格表：内置默认值，可由用户覆盖文件补充或改写。
/// </summary>
/// <remarks>
/// <para>
/// 价格参考 Sub2API 固定快照，单位 USD / 1M tokens，并非实时厂商牌价。
/// 上游站点可能在此基础上叠加分组倍率，该倍率无法从本地数据推断，
/// 见 <see cref="CostCalculator.Calculate"/> 的 rateMultiplier 参数。
/// </para>
/// <para>
/// 价格时效性无法保证：厂商调价后本地表不会自动更新。
/// 查不到价格时按「未定价」处理，不猜测、不回落到同类模型。
/// </para>
/// </remarks>
public sealed class PricingCatalog
{
    private readonly Dictionary<string, ModelPricing> _entries;

    private PricingCatalog(Dictionary<string, ModelPricing> entries)
    {
        _entries = entries;
    }

    /// <summary>已收录的模型数。</summary>
    public int Count => _entries.Count;

    /// <summary>已收录的模型标识。</summary>
    public IReadOnlyCollection<string> Models => _entries.Keys;

    /// <summary>
    /// 查询模型价格。模型标识大小写不敏感。
    /// </summary>
    /// <returns>未收录时返回 null，调用方应据此显示为「未定价」。</returns>
    public ModelPricing? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _entries.GetValueOrDefault(model.Trim());
    }

    /// <summary>创建仅含内置价格表。未知模型返回 null。</summary>
    public static PricingCatalog CreateDefault() =>
        new(new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            // 以下价格在 2026-09-27 与 Sub2API a3eb7ef 快照核对；
            // Astra 使用其 billing_service.go 后备价，其他项取自价卡资源。
            // 长上下文阈值统一为 272000，触发后整次请求按输入 ×2、输出 ×1.5 计价。
            ["gpt-6-sol"] = new ModelPricing
            {
                Input = 2,
                Output = 10,
                CacheRead = 0.2,
                CacheWrite = 2.5,
                Priority = new PriorityPricing { Input = 4, Output = 20, CacheRead = 0.4, CacheWrite = 5 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
            ["gpt-6-luna"] = new ModelPricing
            {
                Input = 0.1,
                Output = 0.5,
                CacheRead = 0.01,
                CacheWrite = 0.125,
                Priority = new PriorityPricing { Input = 0.2, Output = 1, CacheRead = 0.02, CacheWrite = 0.25 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
            ["gpt-6-astra"] = new ModelPricing
            {
                Input = 10,
                Output = 50,
                CacheRead = 1,
                CacheWrite = 12.5,
                Priority = new PriorityPricing { Input = 20, Output = 100, CacheRead = 2, CacheWrite = 25 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
            // 快照未必等于当下厂商价；按实际账单核对后可用覆盖文件改写。
            ["gpt-5.6-sol"] = new ModelPricing
            {
                Input = 5,
                Output = 30,
                CacheRead = 0.5,
                CacheWrite = 6.25,
                Priority = new PriorityPricing { Input = 10, Output = 60, CacheRead = 1, CacheWrite = 12.5 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
            ["gpt-5.6-terra"] = new ModelPricing
            {
                Input = 2,
                Output = 12,
                CacheRead = 0.2,
                CacheWrite = 2.5,
                Priority = new PriorityPricing { Input = 4, Output = 24, CacheRead = 0.4, CacheWrite = 5 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
            ["gpt-5.6-luna"] = new ModelPricing
            {
                Input = 0.2,
                Output = 1.2,
                CacheRead = 0.02,
                CacheWrite = 0.25,
                Priority = new PriorityPricing { Input = 0.4, Output = 2.4, CacheRead = 0.04, CacheWrite = 0.5 },
                LongContext = new LongContextPricing
                {
                    InputTokensAbove = 272_000,
                    InputMultiplier = 2,
                    OutputMultiplier = 1.5,
                },
            },
        });

    /// <summary>
    /// 从价格表文件加载，并叠加到内置表之上。
    /// </summary>
    /// <param name="path">价格表 JSON 路径。不存在时返回默认表。</param>
    /// <remarks>
    /// <para>
    /// <b>本方法不抛异常。</b>文件存在但内容损坏、无法读取或格式不合预期时，
    /// 一律回退到内置表并在结果中给出警告。
    /// </para>
    /// <para>
    /// 早期版本在解析失败时抛 <c>InvalidDataException</c>，而调用方未捕获：
    /// 用户手改价格文件时打错一个逗号，浮层就再也启动不了，且没有任何提示。
    /// 价格表属于可选配置，损坏不应让程序无法使用。
    /// </para>
    /// </remarks>
    public static PricingLoadResult LoadFrom(string? path)
    {
        var baseCatalog = CreateDefault();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new PricingLoadResult(baseCatalog, null);
        }

        if (!File.Exists(path))
        {
            // 配置了路径但文件不在：提示而非静默忽略，
            // 否则用户会以为自定义价格已生效。
            return new PricingLoadResult(baseCatalog, $"价格表文件不存在，已用内置价格：{path}");
        }

        PricingFile? file;

        try
        {
            using var stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize<PricingFile>(stream, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return new PricingLoadResult(
                baseCatalog,
                $"价格表无法读取，已用内置价格：{Path.GetFileName(path)}");
        }

        if (file?.Models is null)
        {
            return new PricingLoadResult(
                baseCatalog,
                $"价格表缺少 Models 字段，已用内置价格：{Path.GetFileName(path)}");
        }

        var merged = new Dictionary<string, ModelPricing>(
            baseCatalog._entries,
            StringComparer.OrdinalIgnoreCase);

        var applied = 0;

        foreach (var (model, entry) in file.Models)
        {
            if (string.IsNullOrWhiteSpace(model) || entry is null || !entry.IsValid())
            {
                return new PricingLoadResult(
                    baseCatalog,
                    $"价格表包含无效条目，整份覆盖未生效：{Path.GetFileName(path)}（模型：{model}）");
            }

            merged[model.Trim()] = entry.ToModelPricing();
            applied++;
        }

        // 解析成功但一条有效条目都没有：极可能是字段名拼错
        // （如用 input_price 而非 Input），此时用户会以为自定义价格已生效。
        if (applied == 0)
        {
            return new PricingLoadResult(
                baseCatalog,
                $"价格表无有效条目，已用内置价格：{Path.GetFileName(path)}");
        }

        return new PricingLoadResult(new PricingCatalog(merged), null, applied);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>价格表文件的根结构。</summary>
    internal sealed class PricingFile
    {
        [JsonPropertyName("models")]
        public Dictionary<string, PricingEntry>? Models { get; init; }
    }

    /// <summary>单个模型在文件中的表示。</summary>
    internal sealed class PricingEntry
    {
        public double? Input { get; init; }
        public double? Output { get; init; }
        public double? CacheRead { get; init; }
        public double? CacheWrite { get; init; }
        public PriorityEntry? Priority { get; init; }
        public double? FastMultiplier { get; init; }
        public double? FlexMultiplier { get; init; }
        public LongContextEntry? LongContext { get; init; }

        public bool IsValid() =>
            ValidPrice(Input) && ValidPrice(Output)
            && ValidPrice(CacheRead) && ValidPrice(CacheWrite)
            && (Priority is null || Priority.IsValid())
            && ValidMultiplier(FastMultiplier) && ValidMultiplier(FlexMultiplier)
            && (LongContext is null || LongContext.IsValid());

        public ModelPricing ToModelPricing() => new()
        {
            Input = Input!.Value,
            Output = Output!.Value,
            CacheRead = CacheRead!.Value,
            CacheWrite = CacheWrite!.Value,
            Priority = Priority?.ToPriorityPricing(),
            FastMultiplier = FastMultiplier,
            FlexMultiplier = FlexMultiplier,
            LongContext = LongContext?.ToLongContextPricing(),
        };
    }

    private static bool ValidPrice(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0;

    private static bool ValidMultiplier(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value > 0;

    internal sealed class PriorityEntry
    {
        public double? Input { get; init; }
        public double? Output { get; init; }
        public double? CacheRead { get; init; }
        public double? CacheWrite { get; init; }

        public bool IsValid() =>
            (Input is null || ValidPrice(Input))
            && (Output is null || ValidPrice(Output))
            && (CacheRead is null || ValidPrice(CacheRead))
            && (CacheWrite is null || ValidPrice(CacheWrite));

        public PriorityPricing ToPriorityPricing() => new()
        {
            Input = Input.GetValueOrDefault(),
            Output = Output.GetValueOrDefault(),
            CacheRead = CacheRead.GetValueOrDefault(),
            CacheWrite = CacheWrite.GetValueOrDefault(),
        };
    }

    internal sealed class LongContextEntry
    {
        public long? InputTokensAbove { get; init; }
        public double? InputMultiplier { get; init; }

        [JsonPropertyName("outputMultiplier")]
        public double? OutputMultiplier { get; init; }

        public bool ThresholdInclusive { get; init; }

        public bool IsValid() =>
            InputTokensAbove is > 0
            && ValidPrice(InputMultiplier) && ValidPrice(OutputMultiplier);

        public LongContextPricing ToLongContextPricing() => new()
        {
            InputTokensAbove = InputTokensAbove!.Value,
            InputMultiplier = InputMultiplier!.Value,
            OutputMultiplier = OutputMultiplier!.Value,
            ThresholdInclusive = ThresholdInclusive,
        };
    }
}
