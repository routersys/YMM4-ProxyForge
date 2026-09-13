using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Encoding;

internal sealed class ProxyFrameRenderer : IDisposable
{
    public const float ReferenceDpi = 96f;

    readonly ID2D1DeviceContext6 context;
    readonly ID2D1Bitmap1 target;
    readonly ID2D1Bitmap1 readback;
    readonly List<ID2D1Effect> chain = [];
    readonly List<ID2D1Image> outputs = [];
    readonly int width;
    readonly int height;
    readonly int stride;
    bool disposed;

    public int FrameByteCount => stride * height;

    public int HalvingCount { get; }

    public ProxyFrameRenderer(IGraphicsDevicesAndContext devices, ID2D1Image source, DisplayBounds display, ProxyGeometry geometry)
    {
        context = devices.DeviceContext;
        width = geometry.Width;
        height = geometry.Height;
        stride = width * FrameAlpha.BytesPerPixel;

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var size = new SizeI(width, height);

        target = context.CreateBitmap(size, nint.Zero, stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi, BitmapOptions.Target));
        readback = context.CreateBitmap(size, nint.Zero, stride,
            new BitmapProperties1(pixelFormat, ReferenceDpi, ReferenceDpi,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

        var align = new AffineTransform2D(context)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.Linear,
            BorderMode = BorderMode.Hard,
            TransformMatrix = Matrix3x2.CreateTranslation(-display.Left, -display.Top),
        };
        var extend = new Border(context)
        {
            EdgeModeX = BorderEdgeMode.Clamp,
            EdgeModeY = BorderEdgeMode.Clamp,
        };
        var image = Append(extend, Append(align, source));

        var contentWidth = (double)display.Width;
        var contentHeight = (double)display.Height;
        while (contentWidth >= 2d * width && contentHeight >= 2d * height)
        {
            var halve = new Scale(context)
            {
                Value = new Vector2(0.5f, 0.5f),
                CenterPoint = Vector2.Zero,
                InterpolationMode = ScaleInterpolationMode.Linear,
                BorderMode = BorderMode.Hard,
            };
            image = Append(halve, image);
            contentWidth /= 2d;
            contentHeight /= 2d;
            HalvingCount++;
        }

        var fit = new AffineTransform2D(context)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.Cubic,
            BorderMode = BorderMode.Hard,
            TransformMatrix = Matrix3x2.CreateScale((float)(width / contentWidth), (float)(height / contentHeight)),
        };
        Append(fit, image);
    }

    ID2D1Image Append(ID2D1Effect effect, ID2D1Image input)
    {
        effect.SetInput(0, input, true);
        chain.Add(effect);
        var output = effect.Output;
        outputs.Add(output);
        return output;
    }

    public void Render(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, FrameByteCount);

        context.Target = target;
        context.BeginDraw();
        context.Clear(new Color4(0f, 0f, 0f, 0f));
        context.DrawImage(outputs[^1], InterpolationMode.Linear, CompositeMode.SourceOver);
        context.EndDraw();
        context.Target = null;

        readback.CopyFromBitmap(target);

        var mapped = readback.Map(MapOptions.Read);
        try
        {
            unsafe
            {
                var scanline = (byte*)mapped.Bits;
                for (var y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(scanline + (long)y * mapped.Pitch, stride)
                        .CopyTo(destination.Slice(y * stride, stride));
                }
            }
        }
        finally
        {
            readback.Unmap();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            outputs[index].Dispose();
            chain[index].SetInput(0, null, true);
            chain[index].Dispose();
        }

        readback.Dispose();
        target.Dispose();
    }
}
