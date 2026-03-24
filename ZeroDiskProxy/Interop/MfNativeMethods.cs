using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ZeroDiskProxy.Interop;

internal static partial class MfNativeMethods
{
    private const string MfPlatDll = "mfplat.dll";
    private const string MfReadWriteDll = "mfreadwrite.dll";

    internal const uint MF_VERSION = 0x00020070;

    [LibraryImport(MfPlatDll)]
    internal static partial int MFStartup(uint version, uint dwFlags = 0);

    [LibraryImport(MfPlatDll)]
    internal static partial int MFShutdown();

    [LibraryImport(MfPlatDll)]
    internal static partial int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);

    [LibraryImport(MfPlatDll)]
    internal static partial int MFCreateSample(out nint ppIMFSample);

    [LibraryImport(MfPlatDll)]
    internal static partial int MFCreateMediaType(out nint ppMFType);

    [LibraryImport(MfPlatDll)]
    internal static partial int MFCreateAttributes(out nint ppMFAttributes, uint cInitialSize);

    [LibraryImport(MfReadWriteDll, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MFCreateSourceReaderFromURL(
        string pwszURL, nint pAttributes, out nint ppSourceReader);

    [LibraryImport(MfReadWriteDll, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MFCreateSinkWriterFromURL(
        string? pwszOutputURL, nint pByteStream, nint pAttributes, out nint ppSinkWriter);

    [DllImport(MfPlatDll)]
    internal static extern int MFCreateMFByteStreamOnStream(
        IStream pStream, out nint ppByteStream);

    [LibraryImport(MfPlatDll, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MFCreateFile(
        uint accessMode, uint openMode, uint fFlags, string pwszFileURL, out nint ppIByteStream);

    internal static ulong PackSize(uint width, uint height)
        => ((ulong)width << 32) | height;

    internal static (uint Width, uint Height) UnpackSize(ulong packed)
        => ((uint)(packed >> 32), (uint)(packed & 0xFFFFFFFF));

    internal static ulong PackRatio(uint numerator, uint denominator)
        => ((ulong)numerator << 32) | denominator;
}
