using System.Buffers;

namespace ProxyForge.Memory;

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
