namespace ZeroDiskProxy.Utilities;

internal static class ByteFormatter
{
    private static readonly string[] s_units = ["B", "KB", "MB", "GB", "TB"];

    internal static string Format(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        double value = bytes;
        int order = 0;
        while (value >= 1024.0 && order < s_units.Length - 1)
        {
            order++;
            value /= 1024.0;
        }

        Span<char> numBuf = stackalloc char[24];
        value.TryFormat(numBuf, out int numLen, "0.##");
        var unit = s_units[order];

        return string.Create(numLen + 1 + unit.Length, (value, order), static (span, state) =>
        {
            state.value.TryFormat(span, out int w, "0.##");
            span[w] = ' ';
            s_units[state.order].AsSpan().CopyTo(span[(w + 1)..]);
        });
    }
}