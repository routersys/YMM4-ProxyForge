using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Core;

internal sealed class MfProxyEncoderFactory(
    MemoryBudget memoryBudget,
    string fallbackDirectory,
    Func<EncoderConfig> configProvider) : IProxyEncoderFactory
{
    public IProxyEncoder Create() => new MfProxyEncoder(memoryBudget, fallbackDirectory, configProvider());
}