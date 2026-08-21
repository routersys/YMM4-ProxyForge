using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Tests;

internal sealed class SolidColorVideoSource : IVideoFileSource
{
    private const float ReferenceDpi = 96f;

    private readonly ID2D1Bitmap1 _bitmap;
    private readonly AffineTransform2D _centering;
    private readonly ID2D1Image _output;
    private readonly int _frameRateNumerator;
    private readonly int _frameRateDenominator;

    internal int UpdateCount { get; private set; }

    public TimeSpan Duration { get; }

    public ID2D1Image Output => _output;

    internal SolidColorVideoSource(
        IGraphicsDevicesAndContext devices,
        int width,
        int height,
        int frameRateNumerator,
        int frameRateDenominator,
        int frameCount,
        byte blue,
        byte green,
        byte red)
    {
        _frameRateNumerator = frameRateNumerator;
        _frameRateDenominator = frameRateDenominator;
        Duration = TimeSpan.FromTicks(
            frameCount * TimeSpan.TicksPerSecond * frameRateDenominator / frameRateNumerator);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        _bitmap = devices.DeviceContext.CreateBitmap(
            new SizeI(width, height), nint.Zero, stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi, BitmapOptions.None));
        _bitmap.CopyFromMemory(pixels, stride);

        _centering = new AffineTransform2D(devices.DeviceContext);
        _centering.SetInput(0, _bitmap, true);
        _centering.TransformMatrix = Matrix3x2.CreateTranslation(
            MathF.Floor(-width / 2f), MathF.Floor(-height / 2f));
        _output = _centering.Output;
    }

    public void Update(TimeSpan time) => UpdateCount++;

    public int GetFrameIndex(TimeSpan time) =>
        (int)Math.Round(
            time.TotalSeconds * _frameRateNumerator / _frameRateDenominator,
            MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        _output.Dispose();
        _centering.SetInput(0, null, true);
        _centering.Dispose();
        _bitmap.Dispose();
    }
}
