using ZeroDiskProxy.Interop;

namespace ZeroDiskProxy.Streaming;

internal static class MfSession
{
    private static int _refCount;
    private static readonly object Lock = new();

    internal static void AddRef()
    {
        lock (Lock)
        {
            if (Interlocked.Increment(ref _refCount) == 1)
                HResultExtensions.ThrowOnFailure(
                    MfNativeMethods.MFStartup(MfNativeMethods.MF_VERSION, 0), "MFStartup");
        }
    }

    internal static void Release()
    {
        lock (Lock)
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
                MfNativeMethods.MFShutdown();
        }
    }
}