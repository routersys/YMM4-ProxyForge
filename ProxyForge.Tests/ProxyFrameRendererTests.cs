using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

public sealed class ProxyFrameRendererTests
{
    static (byte Blue, byte Green, byte Red, byte Alpha) Bgra(int blue, int green, int red, int alpha)
        => ((byte)blue, (byte)green, (byte)red, (byte)alpha);

    static (byte Blue, byte Green, byte Red, byte Alpha) Pixel(ReadOnlySpan<byte> frame, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (frame[offset], frame[offset + 1], frame[offset + 2], frame[offset + 3]);
    }

    static byte[] Render(int width, int height, int scale, TestCentering centering, Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixelOf, out ProxyGeometry geometry, out DisplayBounds display)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        context.DeviceContext.SetDpi(ProxyFrameRenderer.ReferenceDpi, ProxyFrameRenderer.ReferenceDpi);
        using var source = new TestVideoSource(context, width, height, 30, 1, 1, pixelOf, centering);
        source.Update(TimeSpan.Zero);

        display = DisplayBounds.Measure(context.DeviceContext, source.Output);
        geometry = ProxyGeometryCalculator.Calculate(display.PixelWidth, display.PixelHeight, scale);
        using var renderer = new ProxyFrameRenderer(context, source.Output, display, geometry);
        var frame = new byte[renderer.FrameByteCount];
        renderer.Render(frame);
        return frame;
    }

    [Fact]
    public void ASolidSourceIsCopiedExactly()
    {
        var frame = Render(64, 48, 100, TestCentering.Floor, (_, _) => Bgra(32, 160, 200, 255), out var geometry, out _);

        Assert.Equal(new ProxyGeometry(64, 48), geometry);
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
                Assert.Equal(((byte)32, (byte)160, (byte)200, (byte)255), Pixel(frame, 64, x, y));
        }
    }

    [Theory]
    [InlineData(TestCentering.Floor)]
    [InlineData(TestCentering.Exact)]
    public void AFullScaleCopyOfACheckerboardIsPixelExact(TestCentering centering)
    {
        var frame = Render(64, 32, 100, centering, (x, y) => (x + y) % 2 == 0 ? Bgra(255, 255, 255, 255) : Bgra(0, 0, 0, 255), out var geometry, out _);

        Assert.Equal(new ProxyGeometry(64, 32), geometry);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var expected = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                Assert.Equal((expected, expected, expected, (byte)255), Pixel(frame, 64, x, y));
            }
        }
    }

    static double Centroid(ReadOnlySpan<byte> frame, int width, int y)
    {
        var weighted = 0d;
        var total = 0d;
        for (var x = 0; x < width; x++)
        {
            double value = Pixel(frame, width, x, y).Green;
            weighted += (x + 0.5d) * value;
            total += value;
        }

        return weighted / total;
    }

    [Theory]
    [InlineData(100, 640, 360, TestCentering.Floor, 0)]
    [InlineData(50, 640, 360, TestCentering.Floor, 0)]
    [InlineData(50, 641, 361, TestCentering.Floor, 0)]
    [InlineData(50, 641, 361, TestCentering.Exact, 0)]
    [InlineData(50, 641, 361, TestCentering.Floor, 300)]
    [InlineData(50, 641, 361, TestCentering.Floor, 617)]
    [InlineData(25, 641, 361, TestCentering.Floor, 0)]
    [InlineData(25, 641, 361, TestCentering.Floor, 617)]
    [InlineData(10, 641, 361, TestCentering.Floor, 0)]
    [InlineData(10, 641, 361, TestCentering.Exact, 617)]
    [InlineData(10, 1919, 1079, TestCentering.Exact, 1895)]
    [InlineData(33, 1919, 1079, TestCentering.Floor, 900)]
    public void ABandLandsWhereTheBoundsMapIt(int scale, int width, int height, TestCentering centering, int bandStart)
    {
        const int bandWidth = 24;
        var frame = Render(width, height, scale, centering,
            (x, _) => x >= bandStart && x < bandStart + bandWidth ? Bgra(255, 255, 255, 255) : Bgra(0, 0, 0, 255), out var geometry, out var display);

        var expected = (bandStart + bandWidth / 2d + (centering == TestCentering.Floor ? MathF.Floor(-width / 2f) : -width / 2f) - display.Left) * geometry.Width / display.Width;
        var centroid = Centroid(frame, geometry.Width, geometry.Height / 2);

        Assert.True(Math.Abs(centroid - expected) <= 0.2d, $"centroid={centroid} expected={expected} display={display} geometry={geometry}");
    }

    [Theory]
    [InlineData(50, 641, 361, TestCentering.Floor)]
    [InlineData(50, 641, 361, TestCentering.Exact)]
    [InlineData(50, 640, 360, TestCentering.Floor)]
    [InlineData(25, 640, 360, TestCentering.Floor)]
    [InlineData(10, 1920, 1080, TestCentering.Floor)]
    public void AScaledBorderIsSymmetric(int scale, int width, int height, TestCentering centering)
    {
        var frame = Render(width, height, scale, centering,
            (x, y) => x == 0 || y == 0 || x == width - 1 || y == height - 1 ? Bgra(255, 255, 255, 255) : Bgra(0, 0, 0, 255), out var geometry, out _);

        var middleY = geometry.Height / 2;
        var middleX = geometry.Width / 2;
        var left = Pixel(frame, geometry.Width, 0, middleY).Green;
        var right = Pixel(frame, geometry.Width, geometry.Width - 1, middleY).Green;
        var top = Pixel(frame, geometry.Width, middleX, 0).Green;
        var bottom = Pixel(frame, geometry.Width, middleX, geometry.Height - 1).Green;

        Assert.True(Math.Abs(left - right) <= 4, $"left={left} right={right}");
        Assert.True(Math.Abs(top - bottom) <= 4, $"top={top} bottom={bottom}");
        Assert.True(left >= 255 * scale / 100 / 2, $"left={left}");
        Assert.Equal((byte)0, Pixel(frame, geometry.Width, middleX, middleY).Green);
    }

    [Theory]
    [InlineData(100, 1280, 720, 0)]
    [InlineData(100, 1281, 721, 0)]
    [InlineData(50, 641, 361, 1)]
    [InlineData(50, 640, 360, 1)]
    [InlineData(25, 1920, 1080, 2)]
    [InlineData(10, 1920, 1080, 3)]
    public void HalvesUntilTheRemainingScaleIsAtLeastAHalf(int scale, int width, int height, int expectedHalvings)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = TestVideoSource.Solid(context, width, height, 30, 1, 1);
        source.Update(TimeSpan.Zero);
        var display = DisplayBounds.Measure(context.DeviceContext, source.Output);
        var geometry = ProxyGeometryCalculator.Calculate(display.PixelWidth, display.PixelHeight, scale);
        using var renderer = new ProxyFrameRenderer(context, source.Output, display, geometry);

        Assert.Equal(expectedHalvings, renderer.HalvingCount);
    }

    [Fact]
    public void TheWholeFrameIsOpaqueForAnOpaqueSource()
    {
        var frame = Render(641, 361, 50, TestCentering.Exact, (_, _) => Bgra(10, 20, 30, 255), out var geometry, out _);

        Assert.True(FrameAlpha.IsOpaque(frame, geometry.Width, geometry.Height));
    }

    [Fact]
    public void ATransparentHoleIsDetected()
    {
        var frame = Render(640, 360, 50, TestCentering.Floor,
            (x, y) => x is >= 300 and < 340 && y is >= 160 and < 200 ? Bgra(0, 0, 0, 0) : Bgra(10, 20, 30, 255), out var geometry, out _);

        Assert.False(FrameAlpha.IsOpaque(frame, geometry.Width, geometry.Height));
    }

    [Fact]
    public void RenderingAfterDisposeIsRefused()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = TestVideoSource.Solid(context, 16, 16, 30, 1, 1);
        source.Update(TimeSpan.Zero);
        var display = DisplayBounds.Measure(context.DeviceContext, source.Output);
        var renderer = new ProxyFrameRenderer(context, source.Output, display, new ProxyGeometry(16, 16));
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => renderer.Render(new byte[renderer.FrameByteCount]));
    }

    [Fact]
    public void ATooSmallBufferIsRefused()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = TestVideoSource.Solid(context, 16, 16, 30, 1, 1);
        source.Update(TimeSpan.Zero);
        var display = DisplayBounds.Measure(context.DeviceContext, source.Output);
        using var renderer = new ProxyFrameRenderer(context, source.Output, display, new ProxyGeometry(16, 16));

        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(new byte[renderer.FrameByteCount - 1]));
    }
}
