using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Tests;

public enum TestCentering
{
    Floor,
    Exact,
}

internal sealed class TestVideoSource : IVideoFileSource
{
    const float ReferenceDpi = 96f;

    readonly ID2D1Bitmap1 bitmap;
    readonly AffineTransform2D centering;
    readonly ID2D1Image output;
    readonly int frameRateNumerator;
    readonly int frameRateDenominator;

    public int UpdateCount { get; private set; }

    public TimeSpan Duration { get; }

    public ID2D1Image Output => output;

    public TestVideoSource(
        IGraphicsDevicesAndContext devices,
        int width,
        int height,
        int frameRateNumerator,
        int frameRateDenominator,
        int frameCount,
        Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixelOf,
        TestCentering centering = TestCentering.Floor)
    {
        this.frameRateNumerator = frameRateNumerator;
        this.frameRateDenominator = frameRateDenominator;
        Duration = TimeSpan.FromTicks(frameCount * TimeSpan.TicksPerSecond * frameRateDenominator / frameRateNumerator);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (blue, green, red, alpha) = pixelOf(x, y);
                var offset = y * stride + x * 4;
                pixels[offset] = Premultiply(blue, alpha);
                pixels[offset + 1] = Premultiply(green, alpha);
                pixels[offset + 2] = Premultiply(red, alpha);
                pixels[offset + 3] = alpha;
            }
        }

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        bitmap = devices.DeviceContext.CreateBitmap(
            new SizeI(width, height), nint.Zero, stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi, BitmapOptions.None));
        bitmap.CopyFromMemory(pixels, stride);

        this.centering = new AffineTransform2D(devices.DeviceContext);
        this.centering.SetInput(0, bitmap, true);
        this.centering.TransformMatrix = centering == TestCentering.Floor
            ? Matrix3x2.CreateTranslation(MathF.Floor(-width / 2f), MathF.Floor(-height / 2f))
            : Matrix3x2.CreateTranslation(-width / 2f, -height / 2f);
        output = this.centering.Output;
    }

    public static TestVideoSource Solid(
        IGraphicsDevicesAndContext devices,
        int width,
        int height,
        int frameRateNumerator,
        int frameRateDenominator,
        int frameCount,
        byte blue = 64,
        byte green = 64,
        byte red = 64)
        => new(devices, width, height, frameRateNumerator, frameRateDenominator, frameCount, (_, _) => (blue, green, red, byte.MaxValue));

    public void Update(TimeSpan time) => UpdateCount++;

    public int GetFrameIndex(TimeSpan time) => FrameTime.TimeToFrame(time, frameRateNumerator, frameRateDenominator);

    public void Dispose()
    {
        output.Dispose();
        centering.SetInput(0, null, true);
        centering.Dispose();
        bitmap.Dispose();
    }

    static byte Premultiply(byte value, byte alpha)
        => (byte)Math.Round(value * (double)alpha / byte.MaxValue, MidpointRounding.AwayFromZero);
}
