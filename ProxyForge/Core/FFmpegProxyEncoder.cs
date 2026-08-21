using ProxyForge.Interfaces;
using ProxyForge.Plugin;
using ProxyForge.Progress;
using ProxyForge.Transcoding;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Core;

internal sealed class FFmpegProxyEncoder : IProxyEncoder
{
    private const int DiagnosticExcerptLength = 200;
    private const long ProgressReportStepCenti = 100L;

    private readonly string _fallbackDirectory;
    private readonly EncoderConfig _config;
    private readonly FFmpegExecutables _executables;
    private readonly Ymm4VideoSourceFactory _sourceFactory;
    private int _disposed;

    internal FFmpegProxyEncoder(
        string fallbackDirectory,
        EncoderConfig config,
        FFmpegExecutables executables,
        Ymm4VideoSourceFactory sourceFactory)
    {
        _fallbackDirectory = fallbackDirectory;
        _config = config;
        _executables = executables;
        _sourceFactory = sourceFactory;
    }

    public async Task<ProxyCacheEntry> EncodeAsync(
        string inputPath, float scale, float bitrateFactor, int gopSize,
        ProxyGenerationItem? progressItem, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateInputPath(inputPath);

        if (!_executables.IsAvailable)
            throw new InvalidOperationException("FFmpeg executables are unavailable.");

        Directory.CreateDirectory(_fallbackDirectory);

        var outputPath = Path.Combine(_fallbackDirectory, CreateTempFileName());
        ValidateOutputPath(outputPath);

        GraphicsDevices? devices = null;
        IGraphicsDevicesAndContext? context = null;
        IVideoFileSource? source = null;
        ProxyFrameRenderer? renderer = null;
        ProxyCacheEntry? entry = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            devices = new GraphicsDevices();
            context = devices.CreateContext();
            context.DeviceContext.SetDpi(ProxyFrameRenderer.ReferenceDpi, ProxyFrameRenderer.ReferenceDpi);

            source = _sourceFactory(context, inputPath)
                ?? throw new InvalidOperationException(
                    "No YukkuriMovieMaker video file source plugin was able to open the input file.");

            var duration = source.Duration;
            if (duration <= TimeSpan.Zero)
                throw new InvalidOperationException("The video source reported a zero duration.");

            var frameCount = source.GetFrameIndex(duration);
            if (frameCount <= 0)
                throw new InvalidOperationException("The video source reported no frames.");

            source.Update(FrameRate.GetSampleTime(0, frameCount, duration));

            var (displayWidth, displayHeight) = ProxyFrameRenderer.MeasureDisplaySize(context, source.Output);
            var geometry = ProxyGeometryCalculator.Calculate(displayWidth, displayHeight, scale);
            var frameRate = FrameRate.FromFrameCountAndDuration(frameCount, duration);

            renderer = new ProxyFrameRenderer(context, source.Output, geometry, displayWidth, displayHeight);

            var videoEncoder = await FFmpegEncoderSelector.ResolveAsync(
                _executables.FFmpegPath,
                _config.EnableHardwareAcceleration,
                geometry.Width,
                geometry.Height,
                _fallbackDirectory,
                cancellationToken).ConfigureAwait(false);

            var request = new FFmpegEncodeRequest(
                outputPath,
                geometry.Width,
                geometry.Height,
                frameRate,
                FFmpegArguments.CalculateVideoBitrate(geometry.Width, geometry.Height, frameRate.Value, bitrateFactor),
                Math.Max(1, gopSize),
                videoEncoder);

            var pump = new FramePump(source, renderer, frameCount, duration, progressItem);

            var result = await FFmpegProcessRunner.RunAsync(
                _executables.FFmpegPath,
                arguments => FFmpegArguments.WriteEncode(arguments, in request),
                _fallbackDirectory,
                null,
                pump.WriteAsync,
                ProcessPriorityClass.BelowNormal,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                Debug.WriteLine(string.Concat("[FFmpegProxyEncoder] ffmpeg failed: ", result.Diagnostics));
                throw new InvalidOperationException(string.Concat(
                    "ffmpeg exited with code ",
                    result.ExitCode.ToString(CultureInfo.InvariantCulture),
                    ": ",
                    result.FirstDiagnosticLine(DiagnosticExcerptLength)));
            }

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing a proxy file.");

            entry = new ProxyCacheEntry(inputPath, geometry.EffectiveScale)
            {
                ProxyWidth = geometry.Width,
                ProxyHeight = geometry.Height
            };
            entry.SetDiskPath(outputPath, outputInfo.Length);
            return entry;
        }
        catch
        {
            entry?.Dispose();
            TryDeleteFile(outputPath);
            throw;
        }
        finally
        {
            renderer?.Dispose();
            source?.Dispose();
            context?.Dispose();
            devices?.Dispose();
        }
    }

    private static string CreateTempFileName()
    {
        Span<char> buffer = stackalloc char[40];
        "zdp_".AsSpan().CopyTo(buffer);
        Guid.NewGuid().TryFormat(buffer[4..], out _, "N");
        ".mp4".AsSpan().CopyTo(buffer[36..]);
        return new string(buffer);
    }

    private static void ValidateInputPath(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath) || !Path.IsPathFullyQualified(inputPath))
            throw new ArgumentException("Input path must be a fully qualified absolute path.", nameof(inputPath));
    }

    private void ValidateOutputPath(string outputPath)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var fullDirectory = Path.GetFullPath(_fallbackDirectory);
        Span<char> directoryWithSeparator = stackalloc char[fullDirectory.Length + 1];
        fullDirectory.AsSpan().CopyTo(directoryWithSeparator);
        directoryWithSeparator[fullDirectory.Length] = Path.DirectorySeparatorChar;
        if (!fullOutput.AsSpan().StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output path is outside the designated temporary directory.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[FFmpegProxyEncoder] Delete failed: ", ex.Message));
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class FramePump(
        IVideoFileSource source,
        ProxyFrameRenderer renderer,
        int frameCount,
        TimeSpan duration,
        ProxyGenerationItem? progressItem)
    {
        private readonly Action? _dispatchAction =
            progressItem is null ? null : progressItem.ApplyPendingProgress;
        private long _lastReportedCenti;

        internal Task WriteAsync(Stream destination, CancellationToken cancellationToken) =>
            Task.Factory.StartNew(
                () => Write(destination, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        private void Write(Stream destination, CancellationToken cancellationToken)
        {
            var frameBytes = renderer.FrameByteCount;
            var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
            try
            {
                var frame = buffer.AsSpan(0, frameBytes);
                for (var index = 0; index < frameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    source.Update(FrameRate.GetSampleTime(index, frameCount, duration));
                    renderer.Render(frame);
                    destination.Write(frame);

                    ReportProgress(index + 1);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ReportProgress(int completedFrames)
        {
            if (progressItem is null || _dispatchAction is null)
                return;

            var percentage = Math.Min(99.0, completedFrames * 100.0 / frameCount);
            var centi = (long)(percentage * 100);
            if (centi - _lastReportedCenti < ProgressReportStepCenti && percentage < 99.0)
                return;

            _lastReportedCenti = centi;
            progressItem.SetPendingProgress(percentage);
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, _dispatchAction);
        }
    }
}
