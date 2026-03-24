using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Interop;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Progress;

namespace ZeroDiskProxy.Core;

internal sealed class MfProxyEncoder : IProxyEncoder
{
    private readonly MemoryBudget _memoryBudget;
    private readonly string _fallbackDirectory;
    private readonly EncoderConfig _config;
    private int _disposed;

    internal MfProxyEncoder(MemoryBudget memoryBudget, string fallbackDirectory, EncoderConfig config)
    {
        _memoryBudget = memoryBudget;
        _fallbackDirectory = fallbackDirectory;
        _config = config;
    }

    public async Task<ProxyCacheEntry> EncodeAsync(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateInputPath(inputPath);

        Directory.CreateDirectory(_fallbackDirectory);
        var tempFileName = string.Concat("zdp_", Guid.NewGuid().ToString("N"), ".mp4");
        var tempPath = Path.Combine(_fallbackDirectory, tempFileName);
        ValidateTempPath(tempPath);

        ProxyCacheEntry? entry = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputFile = await StorageFile.GetFileFromPathAsync(inputPath);
            var outputFolder = await StorageFolder.GetFolderFromPathAsync(_fallbackDirectory);
            var outputFile = await outputFolder.CreateFileAsync(tempFileName, CreationCollisionOption.ReplaceExisting);

            var sourceProfile = await MediaEncodingProfile.CreateFromFileAsync(inputFile);
            uint origWidth = sourceProfile?.Video?.Width ?? 1920u;
            uint origHeight = sourceProfile?.Video?.Height ?? 1080u;
            if (origWidth == 0) origWidth = 1920;
            if (origHeight == 0) origHeight = 1080;

            double fps = 30.0;
            if (sourceProfile?.Video?.FrameRate is { Numerator: > 0, Denominator: > 0 } fr)
                fps = (double)fr.Numerator / fr.Denominator;

            var proxyWidth = AlignTo16((uint)(origWidth * scale));
            var proxyHeight = AlignTo16((uint)(origHeight * scale));

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
                long lastReportedCenti = 0;
                transcodeOp.Progress = (_, pct) =>
                {
                    var p = Math.Min(99.0, pct);
                    var centi = (long)(p * 100);
                    var prev = Volatile.Read(ref lastReportedCenti);
                    if (centi - prev < 100 && p < 99.0)
                        return;
                    Volatile.Write(ref lastReportedCenti, centi);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        () => progressItem.Progress = p);
                };
            }

            using var reg = cancellationToken.Register(
                static state => ((Windows.Foundation.IAsyncOperationWithProgress<TranscodeFailureReason, double>)state!).Cancel(),
                transcodeOp);
            await transcodeOp;

            float effectiveScale = (float)proxyWidth / origWidth;
            entry = new ProxyCacheEntry(inputPath, effectiveScale)
            {
                ProxyWidth = proxyWidth,
                ProxyHeight = proxyHeight
            };

            var fileSize = new FileInfo(tempPath).Length;
            if (_memoryBudget.TryAllocate(fileSize))
            {
                try
                {
                    var data = await File.ReadAllBytesAsync(tempPath, cancellationToken);
                    var delta = data.LongLength - fileSize;
                    if (delta > 0)
                        _memoryBudget.RecordAllocation(delta);
                    else if (delta < 0)
                        _memoryBudget.RecordDeallocation(-delta);

                    entry.SetMemoryData(data);
                    TryDeleteFile(tempPath);
                }
                catch
                {
                    _memoryBudget.RecordDeallocation(fileSize);
                    throw;
                }
            }
            else if (_config.EnableDiskFallback)
            {
                entry.SetDiskPath(tempPath, fileSize);
            }
            else
            {
                throw new InvalidOperationException("Insufficient memory for proxy and disk fallback is disabled.");
            }

            return entry;
        }
        catch
        {
            entry?.Dispose();
            TryDeleteFile(tempPath);
            throw;
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