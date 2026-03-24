using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Core;

internal sealed class MfProxyEncoderFactory : IProxyEncoderFactory
{
    private readonly MemoryBudget _memoryBudget;
    private readonly string _fallbackDirectory;

    internal MfProxyEncoderFactory(MemoryBudget memoryBudget, string fallbackDirectory)
    {
        _memoryBudget = memoryBudget;
        _fallbackDirectory = fallbackDirectory;
    }

    public IProxyEncoder Create()
    {
        return new MfProxyEncoder(_memoryBudget, _fallbackDirectory);
    }
}