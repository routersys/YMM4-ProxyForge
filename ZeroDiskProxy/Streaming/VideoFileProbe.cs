using System.Runtime.InteropServices;
using ZeroDiskProxy.Interop;

namespace ZeroDiskProxy.Streaming;

internal readonly record struct VideoMetadata(
    uint Width,
    uint Height,
    double FrameRate,
    long DurationHns,
    bool IsValid
)
{
    internal TimeSpan Duration => new(DurationHns);
    internal static VideoMetadata Invalid => new(0, 0, 0, 0, false);
}

internal static class VideoFileProbe
{
    internal static VideoMetadata Probe(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return VideoMetadata.Invalid;

        IMFSourceReader? reader = null;
        var sessionAcquired = false;

        try
        {
            MfSession.AddRef();
            sessionAcquired = true;

            var hr = MfNativeMethods.MFCreateSourceReaderFromURL(filePath, null, out reader);
            if (HResultExtensions.Failed(hr) || reader is null)
                return VideoMetadata.Invalid;

            hr = reader.GetCurrentMediaType(
                MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var mediaType);
            if (HResultExtensions.Failed(hr) || mediaType is null)
                return VideoMetadata.Invalid;

            uint width = 0, height = 0;
            var fps = 30.0;

            try
            {
                if (HResultExtensions.Succeeded(
                        mediaType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, out var packed)))
                {
                    width = (uint)(packed >> 32);
                    height = (uint)(packed & 0xFFFFFFFF);
                }

                if (HResultExtensions.Succeeded(
                        mediaType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, out var packedRate)))
                {
                    var num = (uint)(packedRate >> 32);
                    var den = (uint)(packedRate & 0xFFFFFFFF);
                    if (num > 0 && den > 0)
                        fps = (double)num / den;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(mediaType);
            }

            var durationHns = QueryDuration(reader);

            if (width == 0) width = 1920;
            if (height == 0) height = 1080;

            return new VideoMetadata(width, height, fps, durationHns, true);
        }
        catch
        {
            return VideoMetadata.Invalid;
        }
        finally
        {
            if (reader is not null)
            {
                try { Marshal.ReleaseComObject(reader); }
                catch { }
            }

            if (sessionAcquired)
                MfSession.Release();
        }
    }

    private static long QueryDuration(IMFSourceReader reader)
    {
        var pvPtr = Marshal.AllocHGlobal(24);
        try
        {
            unsafe { new Span<byte>((void*)pvPtr, 24).Clear(); }

            var hr = reader.GetPresentationAttribute(
                MfNativeMethods.MF_SOURCE_READER_MEDIASOURCE,
                MfAttributeKeys.MF_PD_DURATION,
                pvPtr);

            if (HResultExtensions.Succeeded(hr))
            {
                var vt = Marshal.ReadInt16(pvPtr);
                if (vt is 20 or 21)
                    return Marshal.ReadInt64(pvPtr, 8);
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            MfNativeMethods.PropVariantClear(pvPtr);
            Marshal.FreeHGlobal(pvPtr);
        }
    }
}