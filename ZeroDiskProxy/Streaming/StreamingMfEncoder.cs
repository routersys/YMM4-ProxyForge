using System.IO;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
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

            var proxyWidth = AlignTo16((uint)(origWidth * scale));
            var proxyHeight = AlignTo16((uint)(origHeight * scale));

            var inputFile = await StorageFile.GetFileFromPathAsync(inputPath);
            var outputFolder = await StorageFolder.GetFolderFromPathAsync(_fallbackDirectory);
            var outputFile = await outputFolder.CreateFileAsync(tempFileName, CreationCollisionOption.ReplaceExisting);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Video = VideoEncodingProperties.CreateH264();
            profile.Video.Width = proxyWidth;
            profile.Video.Height = proxyHeight;
            profile.Video.Bitrate = Math.Max((uint)(proxyWidth * proxyHeight * fps * 0.07 * bitrateFactor), 100_000u);
            profile.Video.FrameRate.Numerator = (uint)Math.Max(1, Math.Round(fps));
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Properties[MfGuids.MF_MT_MAX_KEYFRAME_SPACING] = (uint)Math.Max(1, gopSize);
            profile.Audio = null;

            cancellationToken.ThrowIfCancellationRequested();

            var transcoder = new MediaTranscoder
            {
                HardwareAccelerationEnabled = _config.EnableHardwareAcceleration,
                VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default
            };

            var prepResult = await transcoder.PrepareFileTranscodeAsync(inputFile, outputFile, profile);

            if (!prepResult.CanTranscode && _config.EnableHardwareAcceleration)
            {
                transcoder.HardwareAccelerationEnabled = false;
                prepResult = await transcoder.PrepareFileTranscodeAsync(inputFile, outputFile, profile);
            }

            if (!prepResult.CanTranscode)
                throw new InvalidOperationException(string.Concat("Transcode failed: ", prepResult.FailureReason.ToString()));

            cancellationToken.ThrowIfCancellationRequested();

            var transcodeOp = prepResult.TranscodeAsync();

            if (progressItem is not null)
            {
                var reporter = new TranscodeProgressReporter(progressItem);
                transcodeOp.Progress = reporter.Report;
            }

            using var reg = cancellationToken.Register(
                static state => ((IAsyncActionWithProgress<double>)state!).Cancel(),
                transcodeOp);
            await transcodeOp;

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

    private sealed class TranscodeProgressReporter(ProxyGenerationItem progressItem)
    {
        private readonly Action _dispatchAction = progressItem.ApplyPendingProgress;
        private long _lastReportedCenti;

        internal void Report(IAsyncActionWithProgress<double> _, double pct)
        {
            var p = Math.Min(99.0, pct);
            var centi = (long)(p * 100);
            var prev = Volatile.Read(ref _lastReportedCenti);
            if (centi - prev < 100 && p < 99.0)
                return;
            Volatile.Write(ref _lastReportedCenti, centi);
            progressItem.SetPendingProgress(p);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                _dispatchAction);
        }
    }
}