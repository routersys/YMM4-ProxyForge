using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ProxyForge.Streaming;

internal sealed class ChunkAllocator : IDisposable
{
    private readonly int _chunkSize;
    private readonly ConcurrentBag<byte[]> _pool = [];
    private long _allocatedCount;
    private long _returnedCount;
    private int _disposed;

    internal int ChunkSize => _chunkSize;
    internal long ActiveCount => Volatile.Read(ref _allocatedCount) - Volatile.Read(ref _returnedCount);

    internal ChunkAllocator(int chunkSize = 65536)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 4096);
        _chunkSize = chunkSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte[] Rent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _allocatedCount);

        if (_pool.TryTake(out var buffer))
            return buffer;

        return ArrayPool<byte>.Shared.Rent(_chunkSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(byte[] buffer)
    {
        if (buffer is null)
            return;

        Interlocked.Increment(ref _returnedCount);

        if (Volatile.Read(ref _disposed) != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return;
        }

        _pool.Add(buffer);
    }

    internal void DrainPool()
    {
        while (_pool.TryTake(out var buffer))
            ArrayPool<byte>.Shared.Return(buffer);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DrainPool();
    }
}