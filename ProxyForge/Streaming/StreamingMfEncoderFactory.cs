using ProxyForge.Core;
using ProxyForge.Interfaces;
using ProxyForge.Memory;

namespace ProxyForge.Streaming;

internal sealed class StreamingMfEncoderFactory(
    MemoryBudget memoryBudget,
    string fallbackDirectory,
    Func<EncoderConfig> configProvider,
    ChunkAllocator chunkAllocator) : IProxyEncoderFactory
{
    public IProxyEncoder Create() =>
        new StreamingMfEncoder(memoryBudget, fallbackDirectory, configProvider(), chunkAllocator);
}