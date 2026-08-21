using ProxyForge.Transcoding;
using Xunit;

namespace ProxyForge.Tests;

public sealed class ProxyGeometryCalculatorTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    [InlineData(360, false)]
    [InlineData(-90, true)]
    [InlineData(-180, false)]
    [InlineData(-270, true)]
    [InlineData(450, true)]
    public void IsQuarterTurn_NormalizesRotation(int rotation, bool expected) =>
        Assert.Equal(expected, ProxyGeometryCalculator.IsQuarterTurn(rotation));

    [Theory]
    [InlineData(1920u, 1080u, 0, 1920u, 1080u)]
    [InlineData(1920u, 1080u, 90, 1080u, 1920u)]
    [InlineData(1920u, 1080u, 180, 1920u, 1080u)]
    [InlineData(1920u, 1080u, 270, 1080u, 1920u)]
    [InlineData(1920u, 1080u, -90, 1080u, 1920u)]
    public void GetDisplaySize_SwapsOnQuarterTurns(
        uint codedWidth, uint codedHeight, int rotation, uint expectedWidth, uint expectedHeight)
    {
        var (width, height) = ProxyGeometryCalculator.GetDisplaySize(codedWidth, codedHeight, rotation);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(1u, 2u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 2u)]
    [InlineData(540u, 540u)]
    [InlineData(541u, 540u)]
    public void AlignEven_RoundsDownWithMinimumOfTwo(uint value, uint expected) =>
        Assert.Equal(expected, ProxyGeometryCalculator.AlignEven(value));

    [Fact]
    public void Calculate_HalfScaleOfFullHd_PreservesAspectRatioExactly()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, 0.5f);

        Assert.Equal(960u, geometry.Width);
        Assert.Equal(540u, geometry.Height);
        Assert.Equal(0.5f, geometry.EffectiveScale);
        Assert.Equal(1920d / 1080d, (double)geometry.Width / geometry.Height, 12);
    }

    [Fact]
    public void Calculate_RotatedSource_ProducesPortraitProxy()
    {
        var (displayWidth, displayHeight) = ProxyGeometryCalculator.GetDisplaySize(1920, 1080, 90);
        var geometry = ProxyGeometryCalculator.Calculate(displayWidth, displayHeight, 0.5f);

        Assert.Equal(540u, geometry.Width);
        Assert.Equal(960u, geometry.Height);
        Assert.Equal(0.5f, geometry.EffectiveScale);
    }

    [Theory]
    [InlineData(0.1f, 192u, 108u)]
    [InlineData(0.25f, 480u, 270u)]
    [InlineData(0.75f, 1440u, 810u)]
    [InlineData(1.0f, 1920u, 1080u)]
    public void Calculate_ProducesEvenDimensionsForFullHd(float scale, uint expectedWidth, uint expectedHeight)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(expectedWidth, geometry.Width);
        Assert.Equal(expectedHeight, geometry.Height);
        Assert.Equal(0u, geometry.Width % 2);
        Assert.Equal(0u, geometry.Height % 2);
    }

    [Theory]
    [InlineData(2.0f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    public void Calculate_NeverExceedsSourceResolution(float scale)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(1920u, geometry.Width);
        Assert.Equal(1080u, geometry.Height);
    }

    [Fact]
    public void Calculate_OddSourceDimensions_ClampsToEvenSource()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1081, 1081, 1.0f);

        Assert.Equal(1080u, geometry.Width);
        Assert.Equal(1080u, geometry.Height);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void Calculate_NonPositiveScale_FallsBackToMinimumDimensions(float scale)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(ProxyGeometryCalculator.MinimumDimension, geometry.Width);
        Assert.Equal(ProxyGeometryCalculator.MinimumDimension, geometry.Height);
    }

    [Fact]
    public void Calculate_TinySource_StaysAtMinimumDimensions()
    {
        var geometry = ProxyGeometryCalculator.Calculate(10, 10, 0.1f);

        Assert.Equal(2u, geometry.Width);
        Assert.Equal(2u, geometry.Height);
        Assert.Equal(0.2f, geometry.EffectiveScale);
    }

    [Theory]
    [InlineData(0u, 1080u)]
    [InlineData(1920u, 0u)]
    public void Calculate_ZeroDimension_Throws(uint width, uint height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ProxyGeometryCalculator.Calculate(width, height, 0.5f));

    [Fact]
    public void Calculate_EffectiveScaleRestoresSourceWidth()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1440, 1080, 0.33f);

        Assert.Equal(1440d, geometry.Width / geometry.EffectiveScale, 3);
    }
}
