using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;
using ZeroDiskProxy.Interop;

namespace ZeroDiskProxy.Streaming;

internal sealed class LightweightVideoFileSource : IVideoFileSource
{
    private readonly string _filePath;
    private readonly ID2D1Bitmap _bitmap;
    private readonly AffineTransform2D _effect;
    private readonly ID2D1Image _output;
    private readonly uint _width;
    private readonly uint _height;
    private readonly double _fps;
    private readonly TimeSpan _duration;
    private byte[]? _bgraBuffer;
    private int _lastFrameIndex = -1;
    private bool _hasFrame;
    private int _disposed;

    public TimeSpan Duration => _duration;
    public ID2D1Image Output => _output;

    private LightweightVideoFileSource(
        string filePath,
        ID2D1Bitmap bitmap,
        AffineTransform2D effect,
        uint width, uint height, double fps, TimeSpan duration)
    {
        _filePath = filePath;
        _bitmap = bitmap;
        _effect = effect;
        _output = effect.Output;
        _width = width;
        _height = height;
        _fps = fps;
        _duration = duration;
    }

    internal static LightweightVideoFileSource? TryCreate(string filePath, IGraphicsDevicesAndContext devices)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        IMFAttributes? attrs = null;
        IMFSourceReader? reader = null;

        try
        {
            MfSession.AddRef();

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
            attrs.SetUINT32(MfAttributeKeys.MF_LOW_LATENCY, 1);

            var hr = MfNativeMethods.MFCreateSourceReaderFromURL(filePath, attrs, out reader);
            if (HResultExtensions.Failed(hr) || reader is null)
            {
                MfSession.Release();
                return null;
            }

            reader.SetStreamSelection(0xFFFFFFFE, false);
            reader.SetStreamSelection(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            uint width = 1920, height = 1080;
            var fps = 30.0;

            hr = reader.GetCurrentMediaType(
                MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var nativeType);
            if (HResultExtensions.Succeeded(hr) && nativeType is not null)
            {
                try
                {
                    if (HResultExtensions.Succeeded(
                            nativeType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, out var packed)))
                    {
                        var w = (uint)(packed >> 32);
                        var h = (uint)(packed & 0xFFFFFFFF);
                        if (w > 0) width = w;
                        if (h > 0) height = h;
                    }

                    if (HResultExtensions.Succeeded(
                            nativeType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, out var packedRate)))
                    {
                        var num = (uint)(packedRate >> 32);
                        var den = (uint)(packedRate & 0xFFFFFFFF);
                        if (num > 0 && den > 0)
                            fps = (double)num / den;
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(nativeType);
                }
            }

            var duration = QueryDuration(reader);

            Marshal.FinalReleaseComObject(reader);
            reader = null;
            MfSession.Release();

            var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
            var bitmapProps = new BitmapProperties(pixelFormat);
            var bitmap = devices.DeviceContext.CreateBitmap(
                new SizeI((int)width, (int)height), bitmapProps);

            var effect = new AffineTransform2D(devices.DeviceContext);
            effect.SetInput(0, bitmap, true);
            effect.TransformMatrix = Matrix3x2.CreateTranslation(-(int)width / 2f, -(int)height / 2f);

            return new LightweightVideoFileSource(
                filePath, bitmap, effect, width, height, fps, duration);
        }
        catch
        {
            if (reader is not null)
            {
                try { Marshal.FinalReleaseComObject(reader); } catch { }
                MfSession.Release();
            }
            return null;
        }
        finally
        {
            if (attrs is not null)
                Marshal.FinalReleaseComObject(attrs);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(TimeSpan time)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var frameIndex = GetFrameIndex(time);
        if (frameIndex == _lastFrameIndex && _hasFrame)
            return;

        ExtractFrame(time);
        _lastFrameIndex = frameIndex;
    }

    private void ExtractFrame(TimeSpan time)
    {
        IMFAttributes? attrs = null;
        IMFSourceReader? reader = null;
        var sessionAcquired = false;

        try
        {
            MfSession.AddRef();
            sessionAcquired = true;

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateAttributes(out attrs, 2));
            attrs.SetUINT32(MfAttributeKeys.MF_LOW_LATENCY, 1);

            var hr = MfNativeMethods.MFCreateSourceReaderFromURL(_filePath, attrs, out reader);
            if (HResultExtensions.Failed(hr) || reader is null)
                return;

            reader.SetStreamSelection(0xFFFFFFFE, false);
            reader.SetStreamSelection(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            var isNv12 = false;
            IMFMediaType? outType = null;
            try
            {
                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out outType));
                outType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                outType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_NV12);

                hr = reader.SetCurrentMediaType(
                    MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, outType);

                if (HResultExtensions.Succeeded(hr))
                {
                    isNv12 = true;
                }
                else
                {
                    outType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_RGB32);
                    outType.SetUINT32(MfAttributeKeys.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                    reader.SetCurrentMediaType(
                        MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, outType);
                }
            }
            finally
            {
                if (outType is not null)
                    Marshal.FinalReleaseComObject(outType);
            }

            var targetHns = Math.Max(0, time.Ticks);
            using var pv = PropVariant.FromInt64(targetHns);
            reader.SetCurrentPosition(Guid.Empty, in pv);

            var frameDurationHns = _fps > 0 ? (long)(10_000_000.0 / _fps) : 333_333L;
            const int maxReads = 16;

            for (var i = 0; i < maxReads; i++)
            {
                hr = reader.ReadSample(
                    MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0, out _, out var flags, out var timestamp, out var sample);

                if (HResultExtensions.Failed(hr) ||
                    (flags & MfNativeMethods.MF_SOURCE_READERF_ENDOFSTREAM) != 0 ||
                    (flags & MfNativeMethods.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample is not null)
                        Marshal.FinalReleaseComObject(sample);
                    break;
                }

                if (sample is null)
                    continue;

                if (timestamp >= targetHns - frameDurationHns)
                {
                    if (isNv12)
                        RenderNv12Sample(sample);
                    else
                        RenderRgb32Sample(sample);
                    _hasFrame = true;
                    Marshal.FinalReleaseComObject(sample);
                    break;
                }

                Marshal.FinalReleaseComObject(sample);
            }
        }
        catch
        {
        }
        finally
        {
            if (reader is not null)
            {
                try { Marshal.FinalReleaseComObject(reader); } catch { }
            }
            if (attrs is not null)
            {
                try { Marshal.FinalReleaseComObject(attrs); } catch { }
            }
            if (sessionAcquired)
                MfSession.Release();
        }
    }

    private unsafe void RenderNv12Sample(IMFSample sample)
    {
        sample.GetBufferByIndex(0, out var buffer);
        if (buffer is null)
            return;

        try
        {
            var hr = buffer.Lock(out var dataPtr, out _, out var currentLength);
            if (HResultExtensions.Failed(hr))
                return;

            try
            {
                var w = (int)_width;
                var h = (int)_height;
                if (w <= 0 || h <= 0 || currentLength <= 0)
                    return;

                var alignedH = currentLength * 2 / (w * 3);
                if (alignedH < h)
                    alignedH = h;

                var bgraSize = w * h * 4;
                var buf = _bgraBuffer;
                if (buf is null || buf.Length < bgraSize)
                    _bgraBuffer = buf = new byte[bgraSize];

                var bgraStride = w * 4;

                fixed (byte* bgraBase = buf)
                {
                    byte* yPlane = (byte*)dataPtr;
                    byte* uvPlane = yPlane + w * alignedH;

                    for (var y = 0; y < h; y++)
                    {
                        byte* yRow = yPlane + y * w;
                        byte* uvRow = uvPlane + (y >> 1) * w;
                        byte* dst = bgraBase + y * bgraStride;

                        for (var x = 0; x < w; x++)
                        {
                            var yv = yRow[x];
                            var u = uvRow[x & ~1];
                            var v = uvRow[(x & ~1) + 1];

                            var c = yv - 16;
                            var d = u - 128;
                            var e = v - 128;

                            var r = (298 * c + 409 * e + 128) >> 8;
                            var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                            var b = (298 * c + 516 * d + 128) >> 8;

                            dst[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                            dst[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                            dst[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                            dst[3] = 255;
                            dst += 4;
                        }
                    }

                    _bitmap.CopyFromMemory((nint)bgraBase, bgraStride);
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(buffer);
        }
    }

    private void RenderRgb32Sample(IMFSample sample)
    {
        sample.GetBufferByIndex(0, out var buffer);
        if (buffer is null)
            return;

        try
        {
            var minPitch = (int)_width * 4;

            if (buffer is IMF2DBuffer buf2D)
            {
                var hr = buf2D.Lock2D(out var scanline, out var pitch);
                if (HResultExtensions.Succeeded(hr))
                {
                    try
                    {
                        if (Math.Abs(pitch) >= minPitch)
                            _bitmap.CopyFromMemory(scanline, Math.Abs(pitch));
                    }
                    finally
                    {
                        buf2D.Unlock2D();
                    }
                    return;
                }
            }

            {
                var hr = buffer.Lock(out var ptr, out _, out var currentLength);
                if (HResultExtensions.Failed(hr))
                    return;

                try
                {
                    if (currentLength >= minPitch * (int)_height)
                        _bitmap.CopyFromMemory(ptr, minPitch);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(buffer);
        }
    }

    public int GetFrameIndex(TimeSpan time) =>
        _fps > 0 ? (int)(time.TotalSeconds * _fps) : 0;

    private static TimeSpan QueryDuration(IMFSourceReader reader)
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
                    return new TimeSpan(Marshal.ReadInt64(pvPtr, 8));
            }

            return TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
        finally
        {
            MfNativeMethods.PropVariantClear(pvPtr);
            Marshal.FreeHGlobal(pvPtr);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _output.Dispose();
        _effect.SetInput(0, null, true);
        _effect.Dispose();
        _bitmap.Dispose();
        _bgraBuffer = null;

        GC.SuppressFinalize(this);
    }
}