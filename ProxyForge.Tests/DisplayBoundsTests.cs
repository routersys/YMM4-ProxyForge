using ProxyForge.Encoding;

namespace ProxyForge.Tests;

public sealed class DisplayBoundsTests
{
    [Theory]
    [InlineData(1920f, 1080f, 1920, 1080)]
    [InlineData(1281f, 721f, 1281, 721)]
    [InlineData(1280.5f, 720.5f, 1281, 721)]
    [InlineData(1280.4f, 720.4f, 1280, 720)]
    public void PixelSizesRoundToTheNearestPixel(float width, float height, int expectedWidth, int expectedHeight)
    {
        var bounds = new DisplayBounds(0f, 0f, width, height);

        Assert.Equal(expectedWidth, bounds.PixelWidth);
        Assert.Equal(expectedHeight, bounds.PixelHeight);
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 1f, true)]
    [InlineData(-960f, -540f, 1920f, 1080f, true)]
    [InlineData(0f, 0f, 16384f, 16384f, true)]
    [InlineData(0f, 0f, 16385f, 1080f, false)]
    [InlineData(0f, 0f, 1920f, 16385f, false)]
    [InlineData(0f, 0f, 0.4f, 1f, false)]
    [InlineData(0f, 0f, 1f, 0f, false)]
    [InlineData(0f, 0f, -1920f, 1080f, false)]
    [InlineData(float.NaN, 0f, 1920f, 1080f, false)]
    [InlineData(0f, float.PositiveInfinity, 1920f, 1080f, false)]
    [InlineData(0f, 0f, float.NaN, 1080f, false)]
    [InlineData(0f, 0f, 1920f, float.NegativeInfinity, false)]
    public void UsabilityRequiresFiniteBoundsWithinTheSupportedSize(float left, float top, float width, float height, bool expected)
        => Assert.Equal(expected, new DisplayBounds(left, top, width, height).IsUsable);
}
