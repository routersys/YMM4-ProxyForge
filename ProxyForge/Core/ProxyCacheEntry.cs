using System.Diagnostics;
using System.IO;

namespace ProxyForge.Core;

internal sealed class ProxyCacheEntry : IDisposable
{
    private string? _diskPath;
    private long _cachedSize;
    private long _lastAccessedTicks = DateTime.UtcNow.Ticks;
    private int _disposed;
    private int _persistentDiskEntry;

    internal string OriginalPath { get; }
    internal float Scale { get; }
    internal uint ProxyWidth { get; init; }
    internal uint ProxyHeight { get; init; }

    internal bool IsValid =>
        Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _diskPath) is not null;

    internal long DataSize => Interlocked.Read(ref _cachedSize);
    internal DateTime CreatedAt { get; } = DateTime.UtcNow;
    internal DateTime LastAccessedAt => new(Interlocked.Read(ref _lastAccessedTicks));

    internal ProxyCacheEntry(string originalPath, float scale)
    {
        OriginalPath = originalPath;
        Scale = scale;
    }

    internal void SetDiskPath(string path, long size)
    {
        Interlocked.Exchange(ref _cachedSize, size);
        Volatile.Write(ref _diskPath, path);
        UpdateLastAccess();
    }

    internal void MarkPersistent() =>
        Volatile.Write(ref _persistentDiskEntry, 1);

    internal string? GetCurrentDiskPath() =>
        Volatile.Read(ref _diskPath);

    internal void ReplaceDiskPath(string newPath)
    {
        Volatile.Write(ref _diskPath, newPath);
        MarkPersistent();
    }

    internal Stream OpenReadStream()
    {
        var path = GetExistingFilePath();
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    }

    internal string GetFilePath() => GetExistingFilePath();

    internal CacheEntrySnapshot CreateSnapshot() =>
        new(OriginalPath,
            Path.GetFileName(OriginalPath),
            Scale,
            ProxyWidth,
            ProxyHeight,
            DataSize,
            CreatedAt,
            LastAccessedAt);

    private string GetExistingFilePath()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var path = Volatile.Read(ref _diskPath);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException("Proxy data unavailable");

        UpdateLastAccess();
        return path;
    }

    private void UpdateLastAccess() =>
        Interlocked.Exchange(ref _lastAccessedTicks, DateTime.UtcNow.Ticks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var disk = Interlocked.Exchange(ref _diskPath, null);
        if (disk is null || Volatile.Read(ref _persistentDiskEntry) != 0)
            return;

        try
        {
            if (File.Exists(disk))
                File.Delete(disk);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ProxyCacheEntry] Delete failed: ", ex.Message));
        }
    }
}

internal readonly record struct CacheEntrySnapshot(
    string OriginalPath,
    string FileName,
    float Scale,
    uint ProxyWidth,
    uint ProxyHeight,
    long DataSize,
    DateTime CreatedAt,
    DateTime LastAccessedAt
);
