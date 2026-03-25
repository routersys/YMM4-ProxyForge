using ProxyForge.Interfaces;
using ProxyForge.Memory;

namespace ProxyForge.Core;

internal sealed class MfProxyEncoderFactory(
    MemoryBudget memoryBudget,
    string fallbackDirectory,
    Func<EncoderConfig> configProvider) : IProxyEncoderFactory
{
    public IProxyEncoder Create() =>
        new MfProxyEncoder(memoryBudget, fallbackDirectory, configProvider());
}