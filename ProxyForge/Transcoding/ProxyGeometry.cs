namespace ProxyForge.Transcoding;

internal readonly record struct ProxyGeometry(uint Width, uint Height, float EffectiveScale);

internal static class ProxyGeometryCalculator
{
    internal const uint MinimumDimension = 2;

    internal static bool IsQuarterTurn(int rotationDegrees)
    {
        var normalized = rotationDegrees % 360;
        if (normalized < 0)
            normalized += 360;
        return normalized is 90 or 270;
    }

    internal static (uint Width, uint Height) GetDisplaySize(uint codedWidth, uint codedHeight, int rotationDegrees) =>
        IsQuarterTurn(rotationDegrees)
            ? (codedHeight, codedWidth)
            : (codedWidth, codedHeight);

    internal static uint AlignEven(uint value)
    {
        var aligned = value & ~1u;
        return aligned < MinimumDimension ? MinimumDimension : aligned;
    }

    internal static ProxyGeometry Calculate(uint displayWidth, uint displayHeight, float scale)
    {
        ArgumentOutOfRangeException.ThrowIfZero(displayWidth);
        ArgumentOutOfRangeException.ThrowIfZero(displayHeight);

        var clamped = float.IsNaN(scale) ? 1f : Math.Clamp(scale, 0f, 1f);
        var width = ClampDimension(ScaleDimension(displayWidth, clamped), displayWidth);
        var height = ClampDimension(ScaleDimension(displayHeight, clamped), displayHeight);

        return new ProxyGeometry(width, height, (float)width / displayWidth);
    }

    private static uint ScaleDimension(uint dimension, float scale) =>
        AlignEven((uint)Math.Round(dimension * (double)scale, MidpointRounding.AwayFromZero));

    private static uint ClampDimension(uint value, uint maximum)
    {
        var limit = AlignEven(maximum);
        return value > limit ? limit : value;
    }
}
