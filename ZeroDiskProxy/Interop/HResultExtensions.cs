using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Interop;

internal static class HResultExtensions
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    internal const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);

    internal static void ThrowOnFailure(int hr, string? context = null)
    {
        if (hr >= 0)
            return;

        var message = context is not null
            ? string.Concat("[ZeroDiskProxy] ", context, ": HRESULT 0x", hr.ToString("X8"))
            : string.Concat("[ZeroDiskProxy] HRESULT 0x", hr.ToString("X8"));

        throw hr switch
        {
            E_OUTOFMEMORY => new OutOfMemoryException(message),
            E_NOTIMPL => new NotImplementedException(message),
            _ => Marshal.GetExceptionForHR(hr) ?? new COMException(message, hr),
        };
    }

    internal static bool Succeeded(int hr) => hr >= 0;
    internal static bool Failed(int hr) => hr < 0;
}