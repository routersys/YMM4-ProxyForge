namespace ProxyForge.Encoding;

internal static class FrameAlpha
{
    public const int BytesPerPixel = 4;
    public const int EdgeMargin = 2;
    public const byte Opaque = byte.MaxValue;

    public static bool IsOpaque(ReadOnlySpan<byte> frame, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.Length, checked(width * height * BytesPerPixel));

        var stride = width * BytesPerPixel;
        var top = Math.Min(EdgeMargin, height);
        var bottom = Math.Max(top, height - EdgeMargin);
        var left = Math.Min(EdgeMargin, width);
        var right = Math.Max(left, width - EdgeMargin);

        for (var y = top; y < bottom; y++)
        {
            var row = frame.Slice(y * stride, stride);
            for (var x = left; x < right; x++)
            {
                if (row[x * BytesPerPixel + 3] != Opaque)
                    return false;
            }
        }

        return true;
    }
}
