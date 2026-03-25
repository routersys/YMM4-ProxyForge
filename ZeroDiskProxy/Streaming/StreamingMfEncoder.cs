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
                inputPath, tempPath, proxyWidth, proxyHeight, fps, bitrate, gopSize,
                durationHns, progressItem, cancellationToken), CancellationToken.None);

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
        uint proxyWidth, uint proxyHeight, double fps,
        uint bitrate, int gopSize, long durationHns,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        IMFAttributes? readerAttrs = null;
        IMFAttributes? writerAttrs = null;
        IMFMediaType? requestedType = null;
        IMFMediaType? actualInputType = null;
        IMFMediaType? outputType = null;
        IMFAttributes? encoderParams = null;

        try
        {
            MfSession.AddRef();

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateAttributes(out readerAttrs, 4));
            readerAttrs.SetUINT32(MfAttributeKeys.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1u);
            readerAttrs.SetUINT32(MfAttributeKeys.MF_LOW_LATENCY, 1u);
            readerAttrs.SetUINT32(MfAttributeKeys.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, _config.EnableHardwareAcceleration ? 1u : 0u);

            HResultExtensions.ThrowOnFailure(
                MfNativeMethods.MFCreateSourceReaderFromURL(inputPath, readerAttrs, out reader));

            reader.SetStreamSelection(0xFFFFFFFE, false);
            reader.SetStreamSelection(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out requestedType));
            requestedType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
            requestedType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_NV12);
            requestedType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
            requestedType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_RATE, MfNativeMethods.PackRatio((uint)Math.Max(1, Math.Round(fps)), 1));
            requestedType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);

            var setHr = reader.SetCurrentMediaType(
                MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, requestedType);

            if (HResultExtensions.Failed(setHr))
            {
                Marshal.ReleaseComObject(requestedType);
                requestedType = null;
                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateMediaType(out requestedType));
                requestedType.SetGUID(MfAttributeKeys.MF_MT_MAJOR_TYPE, MfAttributeKeys.MFMediaType_Video);
                requestedType.SetGUID(MfAttributeKeys.MF_MT_SUBTYPE, MfAttributeKeys.MFVideoFormat_RGB32);
                requestedType.SetUINT64(MfAttributeKeys.MF_MT_FRAME_SIZE, MfNativeMethods.PackSize(proxyWidth, proxyHeight));
                requestedType.SetUINT32(MfAttributeKeys.MF_MT_INTERLACE_MODE, 2u);
                HResultExtensions.ThrowOnFailure(
                    reader.SetCurrentMediaType(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, requestedType));
            }

            HResultExtensions.ThrowOnFailure(
                reader.GetCurrentMediaType(MfNativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out actualInputType));

            HResultExtensions.ThrowOnFailure(MfNativeMethods.MFCreateAttributes(out writerAttrs, 4));
            writerAttrs.SetUINT32(MfAttributeKeys.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, _config.EnableHardwareAcceleration ? 1u : 0u);
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

            HResultExtensions.ThrowOnFailure(writer.SetInputMediaType(streamIndex, actualInputType, encoderParams));

            HResultExtensions.ThrowOnFailure(writer.BeginWriting());

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
                    HResultExtensions.ThrowOnFailure(writer.WriteSample(streamIndex, sample));

                    if (progressItem is not null && durationHns > 0 && applyProgress is not null)
                    {
                        var pct = Math.Min(99.0, (double)timestamp / durationHns * 100.0);
                        var centi = (long)(pct * 100);
                        if (centi - lastReportedCenti >= 100 || pct >= 99.0)
                        {
                            lastReportedCenti = centi;
                            progressItem.SetPendingProgress(pct);
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                applyProgress);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }

            HResultExtensions.ThrowOnFailure(writer.Finalize_());
        }
        finally
        {
            if (encoderParams is not null) { try { Marshal.ReleaseComObject(encoderParams); } catch { } }
            if (outputType is not null) { try { Marshal.ReleaseComObject(outputType); } catch { } }
            if (actualInputType is not null) { try { Marshal.ReleaseComObject(actualInputType); } catch { } }
            if (requestedType is not null) { try { Marshal.ReleaseComObject(requestedType); } catch { } }
            if (writer is not null) { try { Marshal.ReleaseComObject(writer); } catch { } }
            if (writerAttrs is not null) { try { Marshal.ReleaseComObject(writerAttrs); } catch { } }
            if (reader is not null) { try { Marshal.ReleaseComObject(reader); } catch { } }
            if (readerAttrs is not null) { try { Marshal.ReleaseComObject(readerAttrs); } catch { } }
            MfSession.Release();
        }
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

                    int totalRead = 0;
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