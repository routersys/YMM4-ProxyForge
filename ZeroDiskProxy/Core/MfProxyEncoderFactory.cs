using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Core;

internal sealed class MfProxyEncoderFactory : IProxyEncoderFactory
{
    private readonly MemoryBudget _memoryBudget;
    private readonly string _fallbackDirectory;
    private readonly Func<EncoderConfig> _configProvider;

    internal MfProxyEncoderFactory(MemoryBudget memoryBudget, string fallbackDirectory, Func<EncoderConfig> configProvider)
    {
        _memoryBudget = memoryBudget;
        _fallbackDirectory = fallbackDirectory;
        _configProvider = configProvider;
    }

    public IProxyEncoder Create() => new MfProxyEncoder(_memoryBudget, _fallbackDirectory, _configProvider());
}