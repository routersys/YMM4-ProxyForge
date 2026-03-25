using System.Buffers;

namespace ZeroDiskProxy.Memory;

internal static class BufferPool
{
    private const int MaxPoolableSize = 1024 * 1024;
    internal static byte[] Rent(int minimumLength) => ArrayPool<byte>.Shared.Rent(minimumLength);

    internal static void Return(byte[] buffer, bool clearArray = false)
    {
        if (buffer.Length <= MaxPoolableSize)
            ArrayPool<byte>.Shared.Return(buffer, clearArray);
    }
}

internal struct PooledBuffer : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    internal PooledBuffer(int length)
    {
        _length = length;
        _buffer = BufferPool.Rent(length);
    }

    internal readonly byte[] Array => _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    internal readonly int Length => _length;
    internal readonly Span<byte> Span => _buffer is not null ? _buffer.AsSpan(0, _length) : throw new ObjectDisposedException(nameof(PooledBuffer));

    public void Dispose()
    {
        var buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
            BufferPool.Return(buf);
    }
}