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

        Directory.CreateDirectory(_fallbackDirectory);

        Span<char> fileNameBuf = stackalloc char[40];
        "zdp_".AsSpan().CopyTo(fileNameBuf);
        Guid.NewGuid().TryFormat(fileNameBuf[4..], out _, "N");
        ".mp4".AsSpan().CopyTo(fileNameBuf[36..]);
        var tempFileName = new string(fileNameBuf);
        var tempPath = Path.Combine(_fallbackDirectory, tempFileName);
        ValidateTempPath(tempPath);

        ProxyCacheEntry? entry = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = VideoFileProbe.Probe(inputPath);
            var origWidth = metadata is { IsValid: true, Width: > 0 } ? metadata.Width : 1920u;
            var origHeight = metadata is { IsValid: true, Height: > 0 } ? metadata.Height : 1080u;
            var fps = metadata is { IsValid: true, FrameRate: > 0 } ? metadata.FrameRate : 30.0;
            var durationHns = metadata is { IsValid: true, DurationHns: > 0 } ? metadata.DurationHns : 0L;

            var proxyWidth = AlignTo16((uint)(origWidth * scale));
            var proxyHeight = AlignTo16((uint)(origHeight * scale));
            var bitrate = Math.Max((uint)(proxyWidth * proxyHeight * fps * 0.07 * bitrateFactor), 100_000u);

            await Task.Run(() => EncodeCore(
                inputPath, tempPath,
                origWidth, origHeight,
                proxyWidth, proxyHeight,
                fps, bitrate, gopSize,
                durationHns, progressItem, cancellationToken), CancellationToken.None).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            float effectiveScale = (float)proxyWidth / origWidth;
            entry = new ProxyCacheEntry(inputPath, effectiveScale, _memoryBudget.RecordDeallocation)
            {
                ProxyWidth = proxyWidth,
                ProxyHeight = proxyHeight
            };

            CaptureOutputChunked(tempPath, entry);
            return entry;
        }
        catch
        {
            entry?.Dispose();
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private void EncodeCore(
        string inputPath, string outputPath,
        uint origWidth, uint origHeight,
        uint proxyWidth, uint proxyHeight, double fps,
        uint bitrate, int gopSize, long durationHns,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        IMFAttributes? readerAttrs = null;
        IMFAttributes? writerAttrs = null;
        IMFMediaType? outputType = null;
        IMFAttributes? encoderParams = null;

        var needsResize = proxyWidth != origWidth || proxyHeight != origHeight;
        IMFMediaType? converterInputType = null;
        IMFMediaType? converterOutputType = null;
        nint transformPtr = nint.Zero;

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
                MfNativeMethods.MFCreateSinkWriterFromURL(outputPath, nint.Zero, writerAttrs, out writer));

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

            ProcessSamples(reader, writer, colorConverter, streamIndex, durationHns, progressItem, cancellationToken);

            HResultExtensions.ThrowOnFailure(writer.Finalize_());
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
            MfSession.Release();
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
        long durationHns, ProxyGenerationItem? progressItem,
        CancellationToken cancellationToken)
    {
        Action? applyProgress = progressItem is not null ? progressItem.ApplyPendingProgress : null;
        var lastReportedCenti = 0L;

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
                IMFSample? convertedSample = null;

                if (colorConverter is not null)
                {
                    convertedSample = ConvertSample(colorConverter, sample);
                    if (convertedSample is not null)
                    {
                        convertedSample.SetSampleTime(timestamp);
                        sample.GetSampleDuration(out var dur);
                        if (dur > 0)
                            convertedSample.SetSampleDuration(dur);
                        sampleToWrite = convertedSample;
                    }
                }

                try
                {
                    HResultExtensions.ThrowOnFailure(writer.WriteSample(streamIndex, sampleToWrite));
                }
                finally
                {
                    if (convertedSample is not null)
                        Marshal.ReleaseComObject(convertedSample);
                }

                ReportProgress(progressItem, applyProgress, durationHns, timestamp, ref lastReportedCenti);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
    }

    private static IMFSample? ConvertSample(IMFTransform converter, IMFSample inputSample)
    {
        var hr = converter.ProcessInput(0, inputSample, 0);
        if (HResultExtensions.Failed(hr))
            return null;

        HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateSample(out var outSample));

        inputSample.GetTotalLength(out var totalLen);
        var bufSize = Math.Max((int)totalLen, 1024);
        HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMemoryBuffer(bufSize, out var outBuffer));

        try
        {
            outSample.AddBuffer(outBuffer);

            var outputBuffer = new MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample = outSample,
                dwStatus = 0,
                pEvents = null
            };

            hr = converter.ProcessOutput(0, 1, ref outputBuffer, out _);

            if (HResultExtensions.Failed(hr))
            {
                Marshal.ReleaseComObject(outSample);
                return null;
            }

            return outSample;
        }
        catch
        {
            Marshal.ReleaseComObject(outSample);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(outBuffer);
        }
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

    private void CaptureOutputChunked(string outputPath, ProxyCacheEntry entry)
    {
        using var fileHandle = File.OpenHandle(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileSize = RandomAccess.GetLength(fileHandle);

        if (_memoryBudget.TryAllocate(fileSize))
        {
            byte[]? rented = null;
            try
            {
                var exactSize = (int)fileSize;
                rented = BufferPool.Rent(exactSize);
                var chunkBuf = _chunkAllocator.Rent();
                try
                {
                    using var readStream = new FileStream(
                        outputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        _chunkAllocator.ChunkSize, FileOptions.SequentialScan);

                    var totalRead = 0;
                    int read;
                    while ((read = readStream.Read(chunkBuf, 0, chunkBuf.Length)) > 0)
                    {
                        var toCopy = Math.Min(read, exactSize - totalRead);
                        if (toCopy <= 0)
                            break;
                        Buffer.BlockCopy(chunkBuf, 0, rented, totalRead, toCopy);
                        totalRead += toCopy;
                    }

                    var delta = (long)totalRead - fileSize;
                    if (delta > 0)
                        _memoryBudget.RecordAllocation(delta);
                    else if (delta < 0)
                        _memoryBudget.RecordDeallocation(-delta);

                    entry.SetMemoryData(rented, totalRead);
                    rented = null;
                    TryDeleteFile(outputPath);
                }
                finally
                {
                    _chunkAllocator.Return(chunkBuf);
                }
            }
            catch
            {
                if (rented is not null)
                    BufferPool.Return(rented);
                _memoryBudget.RecordDeallocation(fileSize);
                throw;
            }
        }
        else if (_config.EnableDiskFallback)
        {
            entry.SetDiskPath(outputPath, fileSize);
        }
        else
        {
            throw new InvalidOperationException("Insufficient memory for proxy and disk fallback is disabled.");
        }
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

    private void ValidateTempPath(string tempPath)
    {
        var fullTemp = Path.GetFullPath(tempPath);
        var fullDir = Path.GetFullPath(_fallbackDirectory);
        Span<char> dirWithSep = stackalloc char[fullDir.Length + 1];
        fullDir.AsSpan().CopyTo(dirWithSep);
        dirWithSep[fullDir.Length] = Path.DirectorySeparatorChar;
        if (!fullTemp.AsSpan().StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary path is outside the designated fallback directory.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}