using Vortice.Direct2D1;

namespace ProxyForge.Encoding;

internal readonly record struct DisplayBounds(float Left, float Top, float Width, float Height)
{
    public int PixelWidth => (int)Math.Round(Width, MidpointRounding.AwayFromZero);

    public int PixelHeight => (int)Math.Round(Height, MidpointRounding.AwayFromZero);

    public bool IsUsable =>
        float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Width) && float.IsFinite(Height)
        && PixelWidth >= 1 && PixelHeight >= 1
        && PixelWidth <= ProxyGeometryCalculator.MaximumDimension && PixelHeight <= ProxyGeometryCalculator.MaximumDimension;

    public static DisplayBounds Measure(ID2D1DeviceContext context, ID2D1Image image)
    {
        var bounds = context.GetImageLocalBounds(image);
        return new DisplayBounds(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
}
