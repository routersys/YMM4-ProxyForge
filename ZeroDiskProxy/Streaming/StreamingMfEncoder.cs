using System.IO;
using System.Runtime.InteropServices;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Interop;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Progress;

namespace ZeroDiskProxy.Streaming;

internal sealed class StreamingMfEncoder : IProxyEncoder
{
    private readonly MemoryBudget _memoryBudget;
    private readonly string _fallbackDirectory;
    private readonly EncoderConfig _config;
    private readonly ChunkAllocator _chunkAllocator;
    private int _disposed;

    internal StreamingMfEncoder(
        MemoryBudget memoryBudget,
        string fallbackDirectory,
        EncoderConfig config,
        ChunkAllocator chunkAllocator)
    {
        _memoryBudget = memoryBudget;
        _fallbackDirectory = fallbackDirectory;
        _config = config;
        _chunkAllocator = chunkAllocator;
    }

    public async Task<ProxyCacheEntry> EncodeAsync(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateInputPath(inputPath);

        ProxyCacheEntry? entry = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await Task.Run(() => EncodeCoreInMemory(
                inputPath, scale, bitrateFactor, gopSize,
                progressItem, cancellationToken), CancellationToken.None).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            entry = new ProxyCacheEntry(inputPath, result.EffectiveScale, _memoryBudget.RecordDeallocation)
            {
                ProxyWidth = result.ProxyWidth,
                ProxyHeight = result.ProxyHeight
            };

            if (result.Data is not null)
            {
                _memoryBudget.RecordAllocation(result.DataLength);
                entry.SetMemoryData(result.Data, result.DataLength);
            }
            else if (result.DiskPath is not null)
            {
                var fi = new FileInfo(result.DiskPath);
                entry.SetDiskPath(result.DiskPath, fi.Exists ? fi.Length : 0);
            }

            return entry;
        }
        catch
        {
            entry?.Dispose();
            throw;
        }
    }

    private readonly record struct EncodeResult(
        uint ProxyWidth, uint ProxyHeight, float EffectiveScale,
        byte[]? Data, int DataLength, string? DiskPath);

    private EncodeResult EncodeCoreInMemory(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        IMFAttributes? readerAttrs = null;
        IMFAttributes? writerAttrs = null;
        IMFMediaType? outputType = null;
        IMFAttributes? encoderParams = null;
        IMFMediaType? converterInputType = null;
        IMFMediaType? converterOutputType = null;
        nint transformPtr = nint.Zero;
        nint comStreamPtr = nint.Zero;
        nint byteStreamPtr = nint.Zero;

        try
        {
            MfSession.AddRef();

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateAttributes(out readerAttrs, 4));
            readerAttrs.SetUINT32(MfAttributeKeys.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1u);
            readerAttrs.SetUINT32(MfAttributeKeys.MF_LOW_LATENCY, 1u);
            if (_config.EnableHardwareAcceleration)
                readerAttrs.SetUINT32(MfAttributeKeys.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateSourceReaderFromURL(inputPath, readerAttrs, out reader));

            reader.SetStreamSelection(0xFFFFFFFE, false);
            reader.SetStreamSelection(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            ExtractMetadata(reader, out var origWidth, out var origHeight, out var fps, out var durationHns);

            var proxyWidth = AlignTo16((uint)(origWidth * scale));
            var proxyHeight = AlignTo16((uint)(origHeight * scale));
            var bitrate = Math.Max((uint)(proxyWidth * proxyHeight * fps * 0.07 * bitrateFactor), 100_000u);

            var readerOutputType = ConfigureReaderOutput(reader, proxyWidth, proxyHeight, origWidth, origHeight, fps);
            Guid readerSubtype;
            try
            {
                readerOutputType.GetGUID(MfAttributeKeys.MF_MT_SUBTYPE, out readerSubtype);
            }
            finally
            {
                Marshal.ReleaseComObject(readerOutputType);
            }

            var sinkInputSubtype = readerSubtype;

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateAttributes(out writerAttrs, 4));
            if (_config.EnableHardwareAcceleration)
                writerAttrs.SetUINT32(MfAttributeKeys.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
            writerAttrs.SetUINT32(MfAttributeKeys.MF_SINK_WRITER_DISABLE_THROTTLING, 1u);

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.CreateStreamOnHGlobal(nint.Zero, true, out comStreamPtr));
            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateMFByteStreamOnStream(comStreamPtr, out byteStreamPtr));

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateSinkWriterFromURL("output.mp4", byteStreamPtr, writerAttrs, out writer));

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out outputType));
            outputType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
            outputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_H264);
            outputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
            outputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
            outputType.SetUINT32(MfAttributeKeys.MF_MT_AVG_BITRATE, bitrate);
            outputType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);
            outputType.SetUINT32(MfAttributeKeys.MF_MT_MPEG2_PROFILE, 77u);

            HResultExtensions.ThrowOnFailure(writer.AddStream(outputType, out var streamIndex));

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateAttributes(out encoderParams, 2));
            encoderParams.SetUINT32(MfGuids.MF_MT_MAX_KEYFRAME_SPACING, (uint)Math.Max(1, gopSize));

            IMFMediaType sinkInputType;
            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out sinkInputType));
            try
            {
                sinkInputType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                sinkInputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, sinkInputSubtype);
                sinkInputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
                sinkInputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
                sinkInputType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);

                var sinkHr = writer.SetInputMediaType(streamIndex, sinkInputType, encoderParams);

                if (HResultExtensions.Failed(sinkHr) && !readerSubtype.Equals(MfAttributeKeys.MFVideoFormat_NV12))
                {
                    sinkInputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_NV12);
                    sinkHr = writer.SetInputMediaType(streamIndex, sinkInputType, encoderParams);
                    if (HResultExtensions.Succeeded(sinkHr))
                        sinkInputSubtype = MfAttributeKeys.MFVideoFormat_NV12;
                }

                if (HResultExtensions.Failed(sinkHr) && !readerSubtype.Equals(MfAttributeKeys.MFVideoFormat_RGB32))
                {
                    sinkInputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_RGB32);
                    sinkHr = writer.SetInputMediaType(streamIndex, sinkInputType, encoderParams);
                    if (HResultExtensions.Succeeded(sinkHr))
                        sinkInputSubtype = MfAttributeKeys.MFVideoFormat_RGB32;
                }

                HResultExtensions.ThrowOnFailure(sinkHr, "SetInputMediaType on SinkWriter");
            }
            finally
            {
                Marshal.ReleaseComObject(sinkInputType);
            }

            var needsColorConvert = !readerSubtype.Equals(sinkInputSubtype);
            IMFTransform? colorConverter = null;

            if (needsColorConvert)
            {
                HResultExtensions.ThrowOnFailure(
                    MfNativeMethods.CoCreateInstance(
                        MfGuids.CLSID_CColorConvertDMO,
                        nint.Zero, 1u | 4u,
                        MfGuids.IID_IMFTransform,
                        out transformPtr),
                    "CoCreateInstance CColorConvertDMO");

                colorConverter = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);

                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out converterInputType));
                converterInputType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                converterInputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, readerSubtype);
                converterInputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
                converterInputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
                converterInputType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);
                HResultExtensions.ThrowOnFailure(colorConverter.SetInputType(0, converterInputType, 0), "SetInputType on converter");

                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out converterOutputType));
                converterOutputType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                converterOutputType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, sinkInputSubtype);
                converterOutputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
                converterOutputType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
                converterOutputType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);
                HResultExtensions.ThrowOnFailure(colorConverter.SetOutputType(0, converterOutputType, 0), "SetOutputType on converter");

                HResultExtensions.ThrowOnFailure(colorConverter.ProcessMessage(0u, nint.Zero), "ProcessMessage MFT_MESSAGE_NOTIFY_BEGIN_STREAMING");
            }

            HResultExtensions.ThrowOnFailure(writer.BeginWriting());

            ProcessSamples(reader, writer, colorConverter, streamIndex, proxyWidth, proxyHeight, durationHns, progressItem, cancellationToken);

            HResultExtensions.ThrowOnFailure(writer.Finalize_());

            if (writer is not null) { try { Marshal.ReleaseComObject(writer); } catch { } writer = null; }

            float effectiveScale = (float)proxyWidth / origWidth;

            var data = ExtractStreamData(comStreamPtr);
            if (data is not null)
                return new EncodeResult(proxyWidth, proxyHeight, effectiveScale, data.Value.buffer, data.Value.length, null);

            if (_config.EnableDiskFallback)
            {
                Directory.CreateDirectory(_fallbackDirectory);
                var diskPath = WriteFallbackFile(comStreamPtr);
                return new EncodeResult(proxyWidth, proxyHeight, effectiveScale, null, 0, diskPath);
            }

            throw new InvalidOperationException("Insufficient memory for proxy and disk fallback is disabled.");
        }
        finally
        {
            if (converterOutputType is not null) { try { Marshal.ReleaseComObject(converterOutputType); } catch { } }
            if (converterInputType is not null) { try { Marshal.ReleaseComObject(converterInputType); } catch { } }
            if (transformPtr != nint.Zero) { try { Marshal.Release(transformPtr); } catch { } }
            if (encoderParams is not null) { try { Marshal.ReleaseComObject(encoderParams); } catch { } }
            if (outputType is not null) { try { Marshal.ReleaseComObject(outputType); } catch { } }
            if (writer is not null) { try { Marshal.ReleaseComObject(writer); } catch { } }
            if (writerAttrs is not null) { try { Marshal.ReleaseComObject(writerAttrs); } catch { } }
            if (reader is not null) { try { Marshal.ReleaseComObject(reader); } catch { } }
            if (readerAttrs is not null) { try { Marshal.ReleaseComObject(readerAttrs); } catch { } }
            if (byteStreamPtr != nint.Zero) { try { Marshal.Release(byteStreamPtr); } catch { } }
            if (comStreamPtr != nint.Zero) { try { Marshal.Release(comStreamPtr); } catch { } }
            MfSession.Release();
        }
    }

    private static (byte[] buffer, int length)? ExtractStreamData(nint comStreamPtr)
    {
        if (comStreamPtr == nint.Zero)
            return null;

        try
        {
            var stream = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(comStreamPtr);
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stream.Stat(out stat, 1);
            var size = (int)stat.cbSize;

            if (size <= 0)
                return null;

            stream.Seek(0, 0, nint.Zero);

            var buffer = BufferPool.Rent(size);
            stream.Read(buffer, size, nint.Zero);
            return (buffer, size);
        }
        catch
        {
            return null;
        }
    }

    private string WriteFallbackFile(nint comStreamPtr)
    {
        Span<char> fileNameBuf = stackalloc char[40];
        "zdp_".AsSpan().CopyTo(fileNameBuf);
        Guid.NewGuid().TryFormat(fileNameBuf[4..], out _, "N");
        ".mp4".AsSpan().CopyTo(fileNameBuf[36..]);
        var path = Path.Combine(_fallbackDirectory, new string(fileNameBuf));

        var stream = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(comStreamPtr);
        System.Runtime.InteropServices.ComTypes.STATSTG stat;
        stream.Stat(out stat, 1);
        var size = (int)stat.cbSize;

        stream.Seek(0, 0, nint.Zero);

        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
        var chunk = new byte[65536];
        var remaining = size;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, chunk.Length);
            stream.Read(chunk, toRead, nint.Zero);
            fs.Write(chunk, 0, toRead);
            remaining -= toRead;
        }

        return path;
    }

    private static void ExtractMetadata(
        IMFSourceReader reader,
        out uint width, out uint height, out double fps, out long durationHns)
    {
        width = 1920;
        height = 1080;
        fps = 30.0;
        durationHns = 0;

        var hr = reader.GetCurrentMediaType(
            MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var nativeType);
        if (HResultExtensions.Failed(hr) || nativeType is null)
            return;

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
            Marshal.ReleaseComObject(nativeType);
        }

        durationHns = QueryDuration(reader);
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

    private static IMFMediaType ConfigureReaderOutput(
        IMFSourceReader reader,
        uint proxyWidth, uint proxyHeight,
        uint origWidth, uint origHeight,
        double fps)
    {
        var candidates = new[]
        {
            (Subtype: MfAttributeKeys.MFVideoFormat_NV12, Width: proxyWidth, Height: proxyHeight),
            (Subtype: MfAttributeKeys.MFVideoFormat_RGB32, Width: proxyWidth, Height: proxyHeight),
            (Subtype: MfAttributeKeys.MFVideoFormat_NV12, Width: origWidth, Height: origHeight),
            (Subtype: MfAttributeKeys.MFVideoFormat_RGB32, Width: origWidth, Height: origHeight),
        };

        foreach (var (subtype, width, height) in candidates)
        {
            IMFMediaType? attempt = null;
            try
            {
                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out attempt));
                attempt.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                attempt.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, subtype);
                attempt.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(width, height));
                attempt.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE,
                    MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
                attempt.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);

                var hr = reader.SetCurrentMediaType(
                    MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, attempt);

                if (HResultExtensions.Succeeded(hr))
                {
                    reader.GetCurrentMediaType(
                        MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var actual);
                    return actual;
                }

                Marshal.ReleaseComObject(attempt);
                attempt = null;
            }
            catch
            {
                if (attempt is not null)
                    Marshal.ReleaseComObject(attempt);
            }
        }

        throw new InvalidOperationException(
            "Failed to configure source reader output. None of the candidate media types were accepted.");
    }

    private static void ProcessSamples(
        IMFSourceReader reader, IMFSinkWriter writer,
        IMFTransform? colorConverter, uint streamIndex,
        uint proxyWidth, uint proxyHeight,
        long durationHns, ProxyGenerationItem? progressItem,
        CancellationToken cancellationToken)
    {
        Action? applyProgress = progressItem is not null ? progressItem.ApplyPendingProgress : null;
        var lastReportedCenti = 0L;

        IMFSample? reusableSample = null;
        IMFMediaBuffer? reusableBuffer = null;

        if (colorConverter is not null)
        {
            var bufSize = (int)(proxyWidth * proxyHeight * 4);
            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateSample(out var sample));
            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMemoryBuffer(bufSize, out var buffer));
            sample.AddBuffer(buffer);
            reusableSample = sample;
            reusableBuffer = buffer;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hr = reader.ReadSample(
                    MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0, out _, out var flags, out var timestamp, out var sample);

                if (HResultExtensions.Failed(hr))
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    HResultExtensions.ThrowOnFailure(hr);
                }

                if ((flags & MfNativeMethods.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    throw new InvalidOperationException("Source reader error during encode.");
                }

                if ((flags & MfNativeMethods.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    break;
                }

                if (sample is null)
                    continue;

                try
                {
                    var sampleToWrite = sample;

                    if (colorConverter is not null && reusableSample is not null && reusableBuffer is not null)
                    {
                        var converted = ConvertSampleReusable(colorConverter, sample, reusableSample, reusableBuffer);
                        if (converted)
                        {
                            reusableSample.SetSampleTime(timestamp);
                            sample.GetSampleDuration(out var dur);
                            if (dur > 0)
                                reusableSample.SetSampleDuration(dur);
                            sampleToWrite = reusableSample;
                        }
                    }

                    HResultExtensions.ThrowOnFailure(writer.WriteSample(streamIndex, sampleToWrite));

                    ReportProgress(progressItem, applyProgress, durationHns, timestamp, ref lastReportedCenti);
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        finally
        {
            if (reusableBuffer is not null)
                Marshal.ReleaseComObject(reusableBuffer);
            if (reusableSample is not null)
                Marshal.ReleaseComObject(reusableSample);
        }
    }

    private static bool ConvertSampleReusable(
        IMFTransform converter, IMFSample inputSample,
        IMFSample outputSample, IMFMediaBuffer outputBuffer)
    {
        var hr = converter.ProcessInput(0, inputSample, 0);
        if (HResultExtensions.Failed(hr))
            return false;

        outputBuffer.SetCurrentLength(0);

        var outputBuf = new MFT_OUTPUT_DATA_BUFFER
        {
            dwStreamID = 0,
            pSample = outputSample,
            dwStatus = 0,
            pEvents = null
        };

        hr = converter.ProcessOutput(0, 1, ref outputBuf, out _);

        return HResultExtensions.Succeeded(hr);
    }

    private static void ReportProgress(
        ProxyGenerationItem? progressItem, Action? applyProgress,
        long durationHns, long timestamp, ref long lastReportedCenti)
    {
        if (progressItem is null || durationHns <= 0 || applyProgress is null)
            return;

        var pct = Math.Min(99.0, (double)timestamp / durationHns * 100.0);
        var centi = (long)(pct * 100);
        if (centi - lastReportedCenti < 100 && pct < 99.0)
            return;

        lastReportedCenti = centi;
        progressItem.SetPendingProgress(pct);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            applyProgress);
    }

    internal static uint AlignTo16(uint value)
    {
        var aligned = (value + 15u) & ~15u;
        return aligned < 16u ? 16u : aligned;
    }

    private static void ValidateInputPath(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath) || !Path.IsPathFullyQualified(inputPath))
            throw new ArgumentException("Input path must be a fully qualified absolute path.", nameof(inputPath));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}