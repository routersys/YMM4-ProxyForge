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
    private IMFSourceReader? _reader;
    private readonly ID2D1Bitmap _bitmap;
    private readonly AffineTransform2D _effect;
    private readonly ID2D1Image _output;
    private readonly uint _width;
    private readonly uint _height;
    private readonly double _fps;
    private readonly int _stride;
    private readonly TimeSpan _duration;
    private int _lastFrameIndex = -1;
    private bool _hasFrame;
    private long _lastDecodedHns = -1;
    private int _disposed;

    public TimeSpan Duration => _duration;
    public ID2D1Image Output => _output;

    private LightweightVideoFileSource(
        IMFSourceReader reader,
        ID2D1Bitmap bitmap,
        AffineTransform2D effect,
        uint width, uint height, double fps, int stride, TimeSpan duration)
    {
        _reader = reader;
        _bitmap = bitmap;
        _effect = effect;
        _output = effect.Output;
        _width = width;
        _height = height;
        _fps = fps;
        _stride = stride;
        _duration = duration;
    }

    internal static LightweightVideoFileSource? TryCreate(string filePath, IGraphicsDevicesAndContext devices)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        IMFAttributes? attrs = null;
        IMFMediaType? requestedType = null;
        IMFSourceReader? reader = null;

        try
        {
            MfSession.AddRef();

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateAttributes(out attrs, 4), "MFCreateAttributes");

            attrs.SetUINT32(MfAttributeKeys.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            attrs.SetUINT32(MfAttributeKeys.MF_LOW_LATENCY, 1);
            attrs.SetUINT32(MfAttributeKeys.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);

            var hr = MfNativeMethods.MFCreateSourceReaderFromURL(filePath, attrs, out reader);
            if (HResultExtensions.Failed(hr) || reader is null)
            {
                MfSession.Release();
                return null;
            }

            reader.SetStreamSelection(0xFFFFFFFE, false);
            reader.SetStreamSelection(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateMediaType(out requestedType), "MFCreateMediaType");

            requestedType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
            requestedType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_RGB32);

            hr = reader.SetCurrentMediaType(
                MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, requestedType);
            if (HResultExtensions.Failed(hr))
            {
                Marshal.ReleaseComObject(reader);
                MfSession.Release();
                return null;
            }

            hr = reader.GetCurrentMediaType(
                MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var actualType);
            if (HResultExtensions.Failed(hr) || actualType is null)
            {
                Marshal.ReleaseComObject(reader);
                MfSession.Release();
                return null;
            }

            uint width = 1920, height = 1080;
            var fps = 30.0;
            var stride = 0;

            try
            {
                if (HResultExtensions.Succeeded(
                        actualType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, out var packed)))
                {
                    width = (uint)(packed >> 32);
                    height = (uint)(packed & 0xFFFFFFFF);
                }

                if (HResultExtensions.Succeeded(
                        actualType.GetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, out var packedRate)))
                {
                    var num = (uint)(packedRate >> 32);
                    var den = (uint)(packedRate & 0xFFFFFFFF);
                    if (num > 0 && den > 0)
                        fps = (double)num / den;
                }

                if (HResultExtensions.Succeeded(
                        actualType.GetUINT32(MfAttributeKeys.MF_MT_DEFAULT_STRIDE, out var strideVal)))
                    stride = (int)strideVal;
            }
            finally
            {
                Marshal.ReleaseComObject(actualType);
            }

            if (width == 0) width = 1920;
            if (height == 0) height = 1080;
            if (stride == 0) stride = (int)(width * 4);
            if (stride < 0) stride = -stride;

            var duration = QueryDuration(reader);

            var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
            var bitmapProps = new BitmapProperties(pixelFormat);
            var bitmap = devices.DeviceContext.CreateBitmap(
                new SizeI((int)width, (int)height), bitmapProps);

            var effect = new AffineTransform2D(devices.DeviceContext);
            effect.SetInput(0, bitmap, true);
            effect.TransformMatrix = Matrix3x2.Identity;

            return new LightweightVideoFileSource(
                reader, bitmap, effect, width, height, fps, stride, duration);
        }
        catch
        {
            if (reader is not null)
            {
                try { Marshal.ReleaseComObject(reader); } catch { }
                MfSession.Release();
            }
            return null;
        }
        finally
        {
            if (attrs is not null)
                Marshal.ReleaseComObject(attrs);
            if (requestedType is not null)
                Marshal.ReleaseComObject(requestedType);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(TimeSpan time)
    {
        if (Volatile.Read(ref _disposed) != 0 || _reader is null)
            return;

        var frameIndex = GetFrameIndex(time);
        if (frameIndex == _lastFrameIndex && _hasFrame)
            return;

        var targetHns = time.Ticks;
        var frameDurationHns = _fps > 0 ? (long)(10_000_000.0 / _fps) : 333_333L;

        var needsSeek = !_hasFrame ||
                        targetHns < _lastDecodedHns ||
                        targetHns - _lastDecodedHns > frameDurationHns * 5;

        if (needsSeek)
        {
            using var pv = PropVariant.FromInt64(Math.Max(0, targetHns));
            _reader.SetCurrentPosition(Guid.Empty, in pv);
        }

        if (needsSeek)
        {
            ReadUntilTarget(targetHns, frameDurationHns);
        }
        else
        {
            ReadNextFrame();
        }

        _lastFrameIndex = frameIndex;
    }

    private void ReadUntilTarget(long targetHns, long frameDurationHns)
    {
        const int maxSkipFrames = 8;
        var attempts = 0;

        while (attempts < maxSkipFrames)
        {
            var result = ReadSampleAndRender(out var timestamp);
            if (result != ReadResult.Success)
                break;

            if (timestamp >= targetHns - frameDurationHns)
                break;

            attempts++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReadNextFrame()
    {
        ReadSampleAndRender(out _);
    }

    private ReadResult ReadSampleAndRender(out long timestamp)
    {
        timestamp = 0;

        if (_reader is null)
            return ReadResult.Error;

        var hr = _reader.ReadSample(
            MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            0, out _, out var flags, out timestamp, out var sample);

        if (HResultExtensions.Failed(hr))
            return ReadResult.Error;

        if ((flags & MfNativeMethods.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            if (sample is not null)
                Marshal.ReleaseComObject(sample);
            return ReadResult.EndOfStream;
        }

        if ((flags & MfNativeMethods.MF_SOURCE_READERF_ERROR) != 0)
        {
            if (sample is not null)
                Marshal.ReleaseComObject(sample);
            return ReadResult.Error;
        }

        if (sample is null)
            return ReadResult.Empty;

        try
        {
            RenderSampleToBitmap(sample);
            _hasFrame = true;
            _lastDecodedHns = timestamp;
            return ReadResult.Success;
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private void RenderSampleToBitmap(IMFSample sample)
    {
        var hr = sample.ConvertToContiguousBuffer(out var buffer);
        if (HResultExtensions.Failed(hr) || buffer is null)
            return;

        try
        {
            if (buffer is IMF2DBuffer buffer2D)
            {
                hr = buffer2D.Lock2D(out var scanline0, out var pitch);
                if (HResultExtensions.Succeeded(hr))
                {
                    try
                    {
                        _bitmap.CopyFromMemory(scanline0, Math.Abs(pitch));
                    }
                    finally
                    {
                        buffer2D.Unlock2D();
                    }
                    return;
                }
            }

            hr = buffer.Lock(out var ptr, out _, out var currentLength);
            if (HResultExtensions.Failed(hr))
                return;

            try
            {
                var pitch = _height > 0 && currentLength >= (int)_height * 4
                    ? currentLength / (int)_height
                    : _stride;
                _bitmap.CopyFromMemory(ptr, pitch);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
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

        var reader = Interlocked.Exchange(ref _reader, null);
        if (reader is not null)
        {
            try { Marshal.ReleaseComObject(reader); }
            catch { }
            MfSession.Release();
        }

        GC.SuppressFinalize(this);
    }

    private enum ReadResult : byte
    {
        Success,
        Empty,
        EndOfStream,
        Error
    }
}