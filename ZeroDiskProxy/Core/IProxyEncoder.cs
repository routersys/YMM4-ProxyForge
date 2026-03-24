using ZeroDiskProxy.Progress;

namespace ZeroDiskProxy.Core;

internal interface IProxyEncoder : IDisposable
{
    Task<ProxyCacheEntry> EncodeAsync(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken);
}