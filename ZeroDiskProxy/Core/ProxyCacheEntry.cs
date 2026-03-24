using System.Diagnostics;
using System.IO;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheEntry : IDisposable
{
    private byte[]? _memoryData;
    private int _memoryDataLength;
    private string? _diskPath;
    private long _cachedSize;
    private long _lastAccessedTicks = DateTime.UtcNow.Ticks;
    private readonly object _diskWriteLock = new();
    private int _disposed;
    private int _streamCount;

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

    internal void SetMemoryData(byte[] pooledBuffer, int length)
    {
        Interlocked.Exchange(ref _cachedSize, (long)length);
        Volatile.Write(ref _memoryDataLength, length);
        Volatile.Write(ref _diskPath, null);
        Volatile.Write(ref _memoryData, pooledBuffer);
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
        {
            Interlocked.Increment(ref _streamCount);
            if (Volatile.Read(ref _disposed) != 0)
            {
                OnStreamClosed();
                throw new ObjectDisposedException(nameof(ProxyCacheEntry));
            }
            var len = Volatile.Read(ref _memoryDataLength);
            return new PooledMemoryStream(mem, len, this);
        }

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

            Span<char> nameBuf = stackalloc char[45];
            "zdp_read_".AsSpan().CopyTo(nameBuf);
            Guid.NewGuid().TryFormat(nameBuf[9..], out _, "N");
            ".mp4".AsSpan().CopyTo(nameBuf[41..]);
            var tempPath = Path.Combine(tempDirectory, new string(nameBuf));

            using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            fs.Write(mem, 0, Volatile.Read(ref _memoryDataLength));

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

    private void OnStreamClosed()
    {
        if (Interlocked.Decrement(ref _streamCount) == 0)
            TryReturnBuffer();
    }

    private void TryReturnBuffer()
    {
        if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _streamCount) == 0)
        {
            var buf = Interlocked.Exchange(ref _memoryData, null);
            if (buf is not null)
                BufferPool.Return(buf);
        }
    }

    private void UpdateLastAccess() =>
        Interlocked.Exchange(ref _lastAccessedTicks, DateTime.UtcNow.Ticks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        TryReturnBuffer();

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

    private sealed class PooledMemoryStream : Stream
    {
        private readonly byte[] _buffer;
        private readonly int _length;
        private readonly ProxyCacheEntry _owner;
        private int _position;
        private int _disposed;

        internal PooledMemoryStream(byte[] buffer, int length, ProxyCacheEntry owner)
        {
            _buffer = buffer;
            _length = length;
            _owner = owner;
        }

        public override bool CanRead => _disposed == 0;
        public override bool CanSeek => _disposed == 0;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = checked((int)value);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = _length - _position;
            if (available <= 0) return 0;
            var toRead = Math.Min(count, available);
            Buffer.BlockCopy(_buffer, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var available = _length - _position;
            if (available <= 0) return 0;
            var toRead = Math.Min(buffer.Length, available);
            _buffer.AsSpan(_position, toRead).CopyTo(buffer);
            _position += toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<int>(cancellationToken);
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            _position = (int)Math.Clamp(newPos, 0L, (long)_length);
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
                _owner.OnStreamClosed();
            base.Dispose(disposing);
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