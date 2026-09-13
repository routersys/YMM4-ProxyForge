using System.Globalization;

namespace ProxyForge.ViewModels;

internal static class ByteText
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        double value = bytes;
        var order = 0;
        while (value >= 1024d && order < Units.Length - 1)
        {
            value /= 1024d;
            order++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {Units[order]}");
    }
}
