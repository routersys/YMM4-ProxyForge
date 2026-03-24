using System.Buffers;

namespace ZeroDiskProxy.Memory;

internal static class BufferPool
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(16 * 1024 * 1024, 16);

    internal static byte[] Rent(int minimumLength) => Pool.Rent(minimumLength);
    internal static void Return(byte[] buffer, bool clearArray = false) => Pool.Return(buffer, clearArray);
}

internal readonly struct PooledBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _length;

    internal PooledBuffer(int length)
    {
        _length = length;
        _buffer = BufferPool.Rent(length);
    }

    internal byte[] Array => _buffer;
    internal int Length => _length;
    internal Span<byte> Span => _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        if (_buffer is not null)
            BufferPool.Return(_buffer);
    }
}
