using System.Diagnostics;
using System.IO;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheEntry : IDisposable
{
    private byte[]? _memoryData;
    private string? _diskPath;
    private long _cachedSize;
    private long _lastAccessedTicks = DateTime.UtcNow.Ticks;
    private readonly object _diskWriteLock = new();
    private int _disposed;

    internal string OriginalPath { get; }
    internal float Scale { get; }
    internal uint ProxyWidth { get; init; }
    internal uint ProxyHeight { get; init; }
    internal bool IsInMemory => Volatile.Read(ref _memoryData) is not null;
    internal bool IsValid => Volatile.Read(ref _disposed) == 0 && (Volatile.Read(ref _memoryData) is not null || Volatile.Read(ref _diskPath) is not null);
    internal long DataSize => Interlocked.Read(ref _cachedSize);
    internal DateTime CreatedAt { get; } = DateTime.UtcNow;
    internal DateTime LastAccessedAt => new(Interlocked.Read(ref _lastAccessedTicks));

    internal ProxyCacheEntry(string originalPath, float scale)
    {
        OriginalPath = originalPath;
        Scale = scale;
    }

    internal void SetMemoryData(byte[] data)
    {
        Interlocked.Exchange(ref _cachedSize, data.LongLength);
        Volatile.Write(ref _diskPath, null);
        Volatile.Write(ref _memoryData, data);
        UpdateLastAccess();
    }

    internal void SetDiskPath(string path, long size)
    {
        Interlocked.Exchange(ref _cachedSize, size);
        Volatile.Write(ref _memoryData, null);
        Volatile.Write(ref _diskPath, path);
        UpdateLastAccess();
    }

    internal Stream OpenReadStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        UpdateLastAccess();

        var mem = Volatile.Read(ref _memoryData);
        if (mem is not null)
            return new MemoryStream(mem, writable: false);

        var disk = Volatile.Read(ref _diskPath);
        if (disk is not null && File.Exists(disk))
            return new FileStream(disk, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

        throw new InvalidOperationException("Proxy data unavailable");
    }

    internal string GetOrCreateTempFilePath(string tempDirectory)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        UpdateLastAccess();

        var disk = Volatile.Read(ref _diskPath);
        if (disk is not null && File.Exists(disk))
            return disk;

        lock (_diskWriteLock)
        {
            disk = Volatile.Read(ref _diskPath);
            if (disk is not null && File.Exists(disk))
                return disk;

            var mem = Volatile.Read(ref _memoryData);
            if (mem is null)
                throw new InvalidOperationException("Proxy data unavailable");

            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, string.Concat("zdp_read_", Guid.NewGuid().ToString("N"), ".mp4"));
            File.WriteAllBytes(tempPath, mem);
            Volatile.Write(ref _diskPath, tempPath);
            return tempPath;
        }
    }

    internal CacheEntrySnapshot CreateSnapshot()
    {
        return new CacheEntrySnapshot(
            OriginalPath,
            Path.GetFileName(OriginalPath),
            Scale,
            ProxyWidth,
            ProxyHeight,
            IsInMemory,
            DataSize,
            CreatedAt,
            LastAccessedAt);
    }

    private void UpdateLastAccess() =>
        Interlocked.Exchange(ref _lastAccessedTicks, DateTime.UtcNow.Ticks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _memoryData, null);
        var disk = Interlocked.Exchange(ref _diskPath, null);
        if (disk is null)
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
    bool IsInMemory,
    long DataSize,
    DateTime CreatedAt,
    DateTime LastAccessedAt
);