using CodexTokenMeter.Core.Pricing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 价格表加载的容错。
/// </summary>
/// <remarks>
/// 回归保护：早期版本在覆盖文件解析失败时抛 <c>InvalidDataException</c>，
/// 而调用方未捕获——用户手改价格文件时打错一个逗号，
/// 浮层就再也启动不了（实测退出码 0xE0434352），且没有任何提示。
/// 价格表是可选配置，其损坏不应让程序无法使用。
/// </remarks>
public class PricingCatalogLoadTests : IDisposable
{
    private readonly string _directory;

    public PricingCatalogLoadTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"ctm-pricing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    private string WriteFile(string content, string name = "pricing.json")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MissingPathReturnsBuiltInCatalog()
    {
        var result = PricingCatalog.LoadFrom(null);

        Assert.True(result.IsClean);
        Assert.True(result.Catalog.Count > 0);
        Assert.NotNull(result.Catalog.Find("gpt-6-sol"));
    }

    [Fact]
    public void NonExistentFileFallsBackWithWarning()
    {
        // 配置了路径但文件不在：必须提示，否则用户会以为自定义价格生效了。
        var result = PricingCatalog.LoadFrom(Path.Combine(_directory, "不存在.json"));

        Assert.False(result.IsClean);
        Assert.Contains("不存在", result.Warning);
        Assert.NotNull(result.Catalog.Find("gpt-6-sol"));
    }

    [Theory]
    [InlineData("{ \"Models\": { \"x\": { ")]
    [InlineData("{ 这不是 JSON }")]
    [InlineData("")]
    [InlineData("[]")]
    public void CorruptedJsonFallsBackInsteadOfThrowing(string content)
    {
        // 核心回归：绝不抛异常。
        var result = PricingCatalog.LoadFrom(WriteFile(content));

        Assert.True(result.Catalog.Count > 0);
    }

    [Fact]
    public void JsonWithoutModelsFieldFallsBackWithWarning()
    {
        var result = PricingCatalog.LoadFrom(
            WriteFile("""{ "models_typo": { "gpt-6-sol": { "input": 99 } } }"""));

        Assert.False(result.IsClean);
        Assert.Contains("Models", result.Warning);
        Assert.Equal(0, result.AppliedModelCount);
    }

    [Fact]
    public void EmptyModelsObjectFallsBackWithWarning()
    {
        var result = PricingCatalog.LoadFrom(WriteFile("""{ "Models": {} }"""));

        Assert.False(result.IsClean);
        Assert.Equal(0, result.AppliedModelCount);
    }

    [Fact]
    public void ValidOverrideIsApplied()
    {
        var result = PricingCatalog.LoadFrom(WriteFile("""
            {
              "Models": {
                "my-model": { "Input": 1, "Output": 2, "CacheRead": 0.1, "CacheWrite": 1.25 }
              }
            }
            """));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.AppliedModelCount);

        var pricing = result.Catalog.Find("my-model");
        Assert.NotNull(pricing);
        Assert.Equal(1, pricing.Input);
    }

    [Fact]
    public void OverrideReplacesBuiltInEntryButKeepsOthers()
    {
        var result = PricingCatalog.LoadFrom(WriteFile("""
            {
              "Models": {
                "gpt-6-sol": { "Input": 99, "Output": 99, "CacheRead": 9, "CacheWrite": 9 }
              }
            }
            """));

        Assert.Equal(99, result.Catalog.Find("gpt-6-sol")!.Input);

        // 未覆盖的模型必须保留内置价。
        Assert.NotNull(result.Catalog.Find("gpt-5.6-terra"));
    }

    [Fact]
    public void ModelKeysAreTrimmedAndWhitespaceOnlyKeysIgnored()
    {
        var result = PricingCatalog.LoadFrom(WriteFile("""
            {
              "Models": {
                "  spaced-model  ": { "Input": 5, "Output": 5, "CacheRead": 0.5, "CacheWrite": 5 },
                "   ": { "Input": 99, "Output": 99, "CacheRead": 9, "CacheWrite": 9 }
              }
            }
            """));

        Assert.Equal(1, result.AppliedModelCount);
        Assert.NotNull(result.Catalog.Find("spaced-model"));
    }

    [Fact]
    public void WarningMessageIsActionable()
    {
        // 提示必须说明「发生了什么」+「现在用什么」，而不是只报错误。
        var result = PricingCatalog.LoadFrom(WriteFile("{ 坏 JSON }"));

        Assert.False(result.IsClean);
        Assert.Contains("内置价格", result.Warning);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
    }
}
