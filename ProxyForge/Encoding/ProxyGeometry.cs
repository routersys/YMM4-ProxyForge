namespace ProxyForge.Encoding;

internal readonly record struct ProxyGeometry(int Width, int Height);

internal static class ProxyGeometryCalculator
{
    public const int MinimumDimension = 2;
    public const int MaximumDimension = 16384;
    public const int MinimumScale = 10;
    public const int MaximumScale = 100;

    public static int AlignEven(int value)
    {
        var aligned = value & ~1;
        return aligned < MinimumDimension ? MinimumDimension : aligned;
    }

    public static ProxyGeometry Calculate(int displayWidth, int displayHeight, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(displayWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(displayHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayWidth, MaximumDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayHeight, MaximumDimension);

        var clamped = Math.Clamp(scale, MinimumScale, MaximumScale);
        return new ProxyGeometry(
            ScaleDimension(displayWidth, clamped),
            ScaleDimension(displayHeight, clamped));
    }

    static int ScaleDimension(int dimension, int scale)
        => AlignEven((int)Math.Round(dimension * (double)scale / MaximumScale, MidpointRounding.AwayFromZero));
}
