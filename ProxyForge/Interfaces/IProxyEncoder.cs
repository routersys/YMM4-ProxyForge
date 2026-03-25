using ProxyForge.Core;
using ProxyForge.Progress;

namespace ProxyForge.Interfaces;

internal interface IProxyEncoder : IDisposable
{
    Task<ProxyCacheEntry> EncodeAsync(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken);
}