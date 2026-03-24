using System.Diagnostics;
using System.IO;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheEntry : IDisposable
{
    private byte[]? _memoryData;
    private string? _diskPath;
    private int _disposed;

    internal string OriginalPath { get; }
    internal float Scale { get; }
    internal uint ProxyWidth { get; init; }
    internal uint ProxyHeight { get; init; }
    internal bool IsInMemory => Volatile.Read(ref _memoryData) is not null;
    internal bool IsValid => Volatile.Read(ref _disposed) == 0 && (Volatile.Read(ref _memoryData) is not null || Volatile.Read(ref _diskPath) is not null);
    internal long DataSize => Volatile.Read(ref _memoryData)?.Length ?? GetDiskFileSize();
    internal DateTime CreatedAt { get; } = DateTime.UtcNow;
    internal DateTime LastAccessedAt { get; private set; } = DateTime.UtcNow;

    internal ProxyCacheEntry(string originalPath, float scale)
    {
        OriginalPath = originalPath;
        Scale = scale;
    }

    internal void SetMemoryData(byte[] data)
    {
        Volatile.Write(ref _memoryData, data);
        Volatile.Write(ref _diskPath, null);
        LastAccessedAt = DateTime.UtcNow;
    }

    internal void SetDiskPath(string path)
    {
        Volatile.Write(ref _diskPath, path);
        Volatile.Write(ref _memoryData, null);
        LastAccessedAt = DateTime.UtcNow;
    }

    internal Stream OpenReadStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        LastAccessedAt = DateTime.UtcNow;

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
        LastAccessedAt = DateTime.UtcNow;

        var disk = Volatile.Read(ref _diskPath);
        if (disk is not null && File.Exists(disk))
            return disk;

        var mem = Volatile.Read(ref _memoryData);
        if (mem is null)
            throw new InvalidOperationException("Proxy data unavailable");

        Directory.CreateDirectory(tempDirectory);
        var tempPath = string.Concat(tempDirectory, Path.DirectorySeparatorChar.ToString(), "zdp_read_", Guid.NewGuid().ToString("N"), ".mp4");
        File.WriteAllBytes(tempPath, mem);
        Volatile.Write(ref _diskPath, tempPath);
        return tempPath;
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

    private long GetDiskFileSize()
    {
        try
        {
            var disk = Volatile.Read(ref _diskPath);
            if (disk is null)
                return 0;
            var info = new FileInfo(disk);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

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
    DateTime LastAccessedAt);
