using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Transcoding;

internal sealed class ProxyFrameRenderer : IDisposable
{
    internal const float ReferenceDpi = 96f;

    private const int MaximumDimension = 16384;

    private readonly ID2D1DeviceContext6 _context;
    private readonly ID2D1Bitmap1 _target;
    private readonly ID2D1Bitmap1 _readback;
    private readonly AffineTransform2D _transform;
    private readonly ID2D1Image _transformOutput;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private int _disposed;

    internal int FrameByteCount => _stride * _height;

    internal ProxyFrameRenderer(
        IGraphicsDevicesAndContext devices,
        ID2D1Image source,
        ProxyGeometry geometry,
        uint displayWidth,
        uint displayHeight)
    {
        _context = devices.DeviceContext;
        _width = (int)geometry.Width;
        _height = (int)geometry.Height;
        _stride = _width * 4;

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var size = new SizeI(_width, _height);

        _target = _context.CreateBitmap(size, nint.Zero, _stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi, BitmapOptions.Target));
        _readback = _context.CreateBitmap(size, nint.Zero, _stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

        _transform = new AffineTransform2D(_context);
        _transform.SetInput(0, source, true);
        _transform.InterPolationMode = AffineTransform2DInterpolationMode.HighQualityCubic;
        _transform.TransformMatrix =
            Matrix3x2.CreateScale(_width / (float)displayWidth, _height / (float)displayHeight) *
            Matrix3x2.CreateTranslation(_width / 2f, _height / 2f);
        _transformOutput = _transform.Output;
    }

    internal static (uint Width, uint Height) MeasureDisplaySize(IGraphicsDevicesAndContext devices, ID2D1Image image)
    {
        var bounds = devices.DeviceContext.GetImageLocalBounds(image);
        var width = (int)Math.Round(bounds.Right - bounds.Left, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(bounds.Bottom - bounds.Top, MidpointRounding.AwayFromZero);

        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
            throw new InvalidOperationException("The video source reported an unusable image size.");

        return ((uint)width, (uint)height);
    }

    internal void Render(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _context.Target = _target;
        _context.BeginDraw();
        _context.Clear(new Color4(0f, 0f, 0f, 1f));
        _context.DrawImage(_transformOutput, InterpolationMode.Linear, CompositeMode.SourceOver);
        _context.EndDraw();
        _context.Target = null;

        _readback.CopyFromBitmap(_target);

        var mapped = _readback.Map(MapOptions.Read);
        try
        {
            unsafe
            {
                var scanline = (byte*)mapped.Bits;
                for (var y = 0; y < _height; y++)
                {
                    new ReadOnlySpan<byte>(scanline + (long)y * mapped.Pitch, _stride)
                        .CopyTo(destination.Slice(y * _stride, _stride));
                }
            }
        }
        finally
        {
            _readback.Unmap();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _transformOutput.Dispose();
        _transform.SetInput(0, null, true);
        _transform.Dispose();
        _readback.Dispose();
        _target.Dispose();
    }
}
