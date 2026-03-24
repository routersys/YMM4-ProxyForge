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

        return string.Create(null, stackalloc char[32], $"{value:0.##} {s_units[order]}");
    }
}