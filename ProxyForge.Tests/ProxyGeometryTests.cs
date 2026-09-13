using ProxyForge.Encoding;

namespace ProxyForge.Tests;

public sealed class ProxyGeometryTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(540, 540)]
    [InlineData(541, 540)]
    [InlineData(16384, 16384)]
    public void AlignEvenRoundsDownWithAMinimumOfTwo(int value, int expected)
        => Assert.Equal(expected, ProxyGeometryCalculator.AlignEven(value));

    [Fact]
    public void HalfOfFullHdKeepsTheAspectRatioExactly()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, 50);

        Assert.Equal(new ProxyGeometry(960, 540), geometry);
    }

    [Theory]
    [InlineData(10, 192, 108)]
    [InlineData(25, 480, 270)]
    [InlineData(33, 634, 356)]
    [InlineData(75, 1440, 810)]
    [InlineData(100, 1920, 1080)]
    public void FullHdProducesEvenDimensionsAtEveryScale(int scale, int expectedWidth, int expectedHeight)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(new ProxyGeometry(expectedWidth, expectedHeight), geometry);
        Assert.Equal(0, geometry.Width % 2);
        Assert.Equal(0, geometry.Height % 2);
    }

    [Fact]
    public void APortraitSourceStaysPortrait()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1080, 1920, 50);

        Assert.Equal(new ProxyGeometry(540, 960), geometry);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(int.MaxValue)]
    public void AScaleAboveOneHundredNeverExceedsTheSource(int scale)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(new ProxyGeometry(1920, 1080), geometry);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(int.MinValue)]
    public void AScaleBelowTenIsRaisedToTen(int scale)
    {
        var geometry = ProxyGeometryCalculator.Calculate(1920, 1080, scale);

        Assert.Equal(ProxyGeometryCalculator.Calculate(1920, 1080, 10), geometry);
    }

    [Fact]
    public void OddSourceDimensionsAreClampedToTheEvenSource()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1081, 1081, 100);

        Assert.Equal(new ProxyGeometry(1080, 1080), geometry);
    }

    [Fact]
    public void HalfOfAnOddDimensionRoundsBeforeAligning()
    {
        var geometry = ProxyGeometryCalculator.Calculate(1081, 1919, 50);

        Assert.Equal(new ProxyGeometry(540, 960), geometry);
    }

    [Fact]
    public void ATinySourceStaysAtTheMinimumDimensions()
    {
        var geometry = ProxyGeometryCalculator.Calculate(10, 10, 10);

        Assert.Equal(new ProxyGeometry(2, 2), geometry);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    [InlineData(16385, 1080)]
    [InlineData(1920, 16385)]
    public void AnUnusableSourceSizeIsRejected(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ProxyGeometryCalculator.Calculate(width, height, 50));

    [Fact]
    public void TheLargestSourceIsAccepted()
    {
        var geometry = ProxyGeometryCalculator.Calculate(16384, 16384, 100);

        Assert.Equal(new ProxyGeometry(16384, 16384), geometry);
    }
}
