using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Interop;

internal static partial class MfNativeMethods
{
    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint dwFlags);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSourceReaderFromURL(
        string pwszURL, IMFAttributes? pAttributes, out IMFSourceReader ppSourceReader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSinkWriterFromURL(
        string pwszOutputURL, nint pByteStream, IMFAttributes? pAttributes, out IMFSinkWriter ppSinkWriter);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(nint pvar);

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    internal const uint MF_VERSION = 0x00020070;
    internal const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    internal const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    internal const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    internal const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000001;
    internal const uint MF_SOURCE_READERF_ERROR = 0x00000002;

    internal static ulong PackSize(uint width, uint height) => ((ulong)width << 32) | height;
    internal static ulong PackRatio(uint numerator, uint denominator) => ((ulong)numerator << 32) | denominator;
}