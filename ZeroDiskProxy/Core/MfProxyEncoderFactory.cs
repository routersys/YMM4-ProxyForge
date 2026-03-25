using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Streaming;

namespace ZeroDiskProxy.Core;

internal sealed class MfProxyEncoderFactory(
    MemoryBudget memoryBudget,
    string fallbackDirectory,
    Func<EncoderConfig> configProvider,
    ChunkAllocator chunkAllocator) : IProxyEncoderFactory
{
    public IProxyEncoder Create() =>
        new StreamingMfEncoder(memoryBudget, fallbackDirectory, configProvider(), chunkAllocator);
}