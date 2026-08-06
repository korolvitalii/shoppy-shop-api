using ShoppyShop.Application;

namespace ShoppyShop.UnitTests;

public sealed class PricePresetsTests
{
    [Theory]
    [InlineData("0-50", 0, 49.99)]
    [InlineData("50-200", 50, 199.99)]
    public void ResolveMapsKnownPresetsToTheirBounds(string preset, decimal expectedMin, decimal expectedMax)
    {
        var (min, max) = PricePresets.Resolve(preset, null, null);

        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
    }
    [Fact]
    public void ResolveMapsOpenEndedPresetToNullMax()
    {
        var (min, max) = PricePresets.Resolve("200+", null, null);

        Assert.Equal(200m, min);
        Assert.Null(max);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("not-a-preset")]
    public void ResolvePassesThroughExplicitBoundsWhenPresetIsUnrecognized(string? preset)
    {
        var (min, max) = PricePresets.Resolve(preset, 12m, 34m);

        Assert.Equal(12m, min);
        Assert.Equal(34m, max);
    }
}