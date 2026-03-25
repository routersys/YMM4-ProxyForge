using ZeroDiskProxy.Core;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Streaming;

internal sealed class StreamingMfEncoderFactory(
    MemoryBudget memoryBudget,
    string fallbackDirectory,
    Func<EncoderConfig> configProvider,
    ChunkAllocator chunkAllocator) : IProxyEncoderFactory
{
    public IProxyEncoder Create() =>
        new StreamingMfEncoder(memoryBudget, fallbackDirectory, configProvider(), chunkAllocator);
}