using System.IO;

namespace ProxyForge.Cache;

internal readonly record struct SourceIdentity(string Path, long Length, long WriteTimeTicks)
{
    public static SourceIdentity? Of(string path)
    {
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
            return null;

        return new SourceIdentity(fullPath, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    public bool Matches(string path, long length, long writeTimeTicks)
        => Length == length
            && WriteTimeTicks == writeTimeTicks
            && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

    public bool Equals(SourceIdentity other) => Matches(other.Path, other.Length, other.WriteTimeTicks);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), Length, WriteTimeTicks);
}
