using CodexTokenMeter.Core.Settings;
using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "CodexTokenMeterSettings-" + Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响结论。
        }
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void LoadReturnsDefaultsWhenFileMissing()
    {
        // 首次运行不应因缺少配置文件而失败。
        var settings = SettingsStore.Load(SettingsPath);

        Assert.Equal(OverlayAnchor.TopRight, settings.Anchor);
        Assert.Equal(OverlayPlacementCalculator.DefaultTopInset, settings.TopInset);
        Assert.Equal(1.0, settings.Scale);
        Assert.Equal(1.0, settings.RateMultiplier);
        Assert.True(settings.FollowSystemTheme);
        Assert.Null(settings.PricingOverridePath);
    }

    [Fact]
    public void SaveThenLoadRoundTripsAllFields()
    {
        var original = new OverlaySettings
        {
            Anchor = OverlayAnchor.BottomRight,
            TopInset = 72,
            Margin = 20,
            FollowSystemTheme = false,
            DarkTheme = false,
            Scale = 1.25,
            RateMultiplier = 1.5,
            PricingOverridePath = @"D:\prices.json",
        };

        Assert.True(SettingsStore.Save(original, SettingsPath));
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Equal(original.Anchor, loaded.Anchor);
        Assert.Equal(original.TopInset, loaded.TopInset);
        Assert.Equal(original.Margin, loaded.Margin);
        Assert.Equal(original.FollowSystemTheme, loaded.FollowSystemTheme);
        Assert.Equal(original.DarkTheme, loaded.DarkTheme);
        Assert.Equal(original.Scale, loaded.Scale);
        Assert.Equal(original.RateMultiplier, loaded.RateMultiplier);
        Assert.Equal(original.PricingOverridePath, loaded.PricingOverridePath);
    }

    [Fact]
    public void SaveCreatesDirectoryWhenMissing()
    {
        var nested = Path.Combine(_directory, "a", "b", "settings.json");

        Assert.True(SettingsStore.Save(new OverlaySettings(), nested));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void LoadFallsBackToDefaultsWhenJsonIsCorrupt()
    {
        // 配置损坏不应阻止启动。
        File.WriteAllText(SettingsPath, "{ this is not valid json");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.Equal(new OverlaySettings().Anchor, settings.Anchor);
        Assert.Equal(1.0, settings.Scale);
    }

    [Fact]
    public void LoadFallsBackWhenJsonIsNotAnObject()
    {
        File.WriteAllText(SettingsPath, "[1,2,3]");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.Equal(new OverlaySettings().Anchor, settings.Anchor);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(48, 48)]
    [InlineData(5000, 400)]
    public void NormalizeClampsTopInset(int input, int expected)
    {
        Assert.Equal(expected, new OverlaySettings { TopInset = input }.Normalize().TopInset);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(12, 12)]
    [InlineData(1000, 200)]
    public void NormalizeClampsMargin(int input, int expected)
    {
        Assert.Equal(expected, new OverlaySettings { Margin = input }.Normalize().Margin);
    }

    [Theory]
    [InlineData(0.1, 0.6)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(10.0, 2.0)]
    public void NormalizeClampsScale(double input, double expected)
    {
        Assert.Equal(expected, new OverlaySettings { Scale = input }.Normalize().Scale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void NormalizeRejectsInvalidRateMultiplier(double input)
    {
        // 倍率为 0 会把全部费用算成免费，必须拒绝。
        Assert.Equal(1.0, new OverlaySettings { RateMultiplier = input }.Normalize().RateMultiplier);
    }

    [Fact]
    public void NormalizeKeepsValidRateMultiplier()
    {
        Assert.Equal(1.5, new OverlaySettings { RateMultiplier = 1.5 }.Normalize().RateMultiplier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeClearsBlankPricingPath(string? input)
    {
        Assert.Null(new OverlaySettings { PricingOverridePath = input }.Normalize().PricingOverridePath);
    }

    [Fact]
    public void SaveIsAtomicAndLeavesNoTemporaryFile()
    {
        SettingsStore.Save(new OverlaySettings { Scale = 1.2 }, SettingsPath);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void SaveOverwritesExistingFile()
    {
        SettingsStore.Save(new OverlaySettings { Scale = 0.8 }, SettingsPath);
        SettingsStore.Save(new OverlaySettings { Scale = 1.6 }, SettingsPath);

        Assert.Equal(1.6, SettingsStore.Load(SettingsPath).Scale);
    }

    [Fact]
    public void ResolveOverrideUsesCommandLineArgument()
    {
        var path = SettingsStore.ResolveOverride(["--settings", @"D:\custom\settings.json"]);

        Assert.Equal(@"D:\custom\settings.json", path);
    }

    [Fact]
    public void ResolveOverrideIsCaseInsensitive()
    {
        var path = SettingsStore.ResolveOverride(["--SETTINGS", @"D:\custom.json"]);

        Assert.Equal(@"D:\custom.json", path);
    }

    [Fact]
    public void ResolveOverrideFallsBackToDefault()
    {
        Assert.Equal(SettingsStore.DefaultPath, SettingsStore.ResolveOverride(["--other"]));
        Assert.Equal(SettingsStore.DefaultPath, SettingsStore.ResolveOverride(null));
    }

    [Fact]
    public void ResolveOverrideIgnoresArgumentWithoutValue()
    {
        Assert.Equal(SettingsStore.DefaultPath, SettingsStore.ResolveOverride(["--settings"]));
    }

    [Fact]
    public void DefaultPathIsUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, SettingsStore.DefaultPath);
        Assert.EndsWith("settings.json", SettingsStore.DefaultPath);
    }
}
