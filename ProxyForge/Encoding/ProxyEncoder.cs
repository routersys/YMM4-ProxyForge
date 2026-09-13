using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Encoding;

internal readonly record struct ProxyEncodeRequest(
    string SourcePath,
    string OutputPath,
    string WorkingDirectory,
    int Scale,
    int BitrateScale,
    int KeyFrameInterval,
    bool UsesHardwareEncoder);

internal sealed record ProxyEncodeResult(
    ProxyGeometry Geometry,
    DisplayBounds Display,
    FrameRate FrameRate,
    TimeSpan Duration,
    int FrameCount,
    long FileLength);

internal sealed class ProxyEncoder(FFmpegExecutables executables, VideoSourceFactory sourceFactory)
{
    const int DiagnosticExcerptLength = 200;

    public async Task<ProxyEncodeResult> EncodeAsync(ProxyEncodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        if (!executables.IsAvailable)
            throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegUnavailable, "The FFmpeg executable bundled with YukkuriMovieMaker could not be located.");

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.WorkingDirectory);

        GraphicsDevices? devices = null;
        IGraphicsDevicesAndContext? context = null;
        IVideoFileSource? source = null;
        ProxyFrameRenderer? renderer = null;

        try
        {
            devices = new GraphicsDevices();
            context = devices.CreateContext();
            context.DeviceContext.SetDpi(ProxyFrameRenderer.ReferenceDpi, ProxyFrameRenderer.ReferenceDpi);

            source = sourceFactory(context, request.SourcePath)
                ?? throw new ProxyEncodeException(ProxyEncodeFailure.SourceUnavailable, "No video file source plugin was able to open the input file.");

            var duration = source.Duration;
            if (duration <= TimeSpan.Zero)
                throw new ProxyEncodeException(ProxyEncodeFailure.NoFrames, "The video source reported a zero duration.");

            var frameCount = source.GetFrameIndex(duration);
            if (frameCount <= 0)
                throw new ProxyEncodeException(ProxyEncodeFailure.NoFrames, "The video source reported no frames.");

            var frameRate = FrameRateResolver.Resolve(source.GetFrameIndex, frameCount, duration);

            source.Update(frameRate.GetSampleTime(0));
            var display = DisplayBounds.Measure(context.DeviceContext, source.Output);
            if (!display.IsUsable)
                throw new ProxyEncodeException(ProxyEncodeFailure.UnusableSize, "The video source reported an unusable image size.");

            var geometry = ProxyGeometryCalculator.Calculate(display.PixelWidth, display.PixelHeight, request.Scale);
            renderer = new ProxyFrameRenderer(context, source.Output, display, geometry);

            var videoEncoder = await FFmpegEncoderSelector.ResolveAsync(
                executables.FFmpegPath,
                request.UsesHardwareEncoder,
                geometry.Width,
                geometry.Height,
                request.WorkingDirectory,
                cancellationToken).ConfigureAwait(false);

            var encodeRequest = new FFmpegEncodeRequest(
                request.OutputPath,
                geometry.Width,
                geometry.Height,
                frameRate,
                FFmpegArguments.CalculateVideoBitrate(geometry.Width, geometry.Height, frameRate.Value, request.BitrateScale),
                Math.Max(1, request.KeyFrameInterval),
                videoEncoder);

            var pump = new FramePump(source, renderer, frameRate, frameCount, geometry, progress);

            var result = await FFmpegProcessRunner.RunAsync(
                executables.FFmpegPath,
                arguments => FFmpegArguments.WriteEncode(arguments, in encodeRequest),
                request.WorkingDirectory,
                null,
                pump.WriteAsync,
                ProcessPriorityClass.BelowNormal,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                Log.Default.Write(string.Concat("ProxyForge: ffmpeg が失敗しました。", Environment.NewLine, result.Diagnostics));
                throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, string.Concat(
                    "ffmpeg exited with code ",
                    result.ExitCode.ToString(CultureInfo.InvariantCulture),
                    ": ",
                    result.FirstDiagnosticLine(DiagnosticExcerptLength)));
            }

            var output = new FileInfo(request.OutputPath);
            if (!output.Exists || output.Length == 0)
                throw new ProxyEncodeException(ProxyEncodeFailure.NoOutput, "ffmpeg completed without producing a proxy file.");

            return new ProxyEncodeResult(geometry, display, frameRate, duration, frameCount, output.Length);
        }
        catch
        {
            TryDelete(request.OutputPath);
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

    static void ValidateRequest(ProxyEncodeRequest request)
    {
        if (string.IsNullOrEmpty(request.SourcePath) || !Path.IsPathFullyQualified(request.SourcePath))
            throw new ArgumentException("The source path must be fully qualified.", nameof(request));

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkingDirectory)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(request.OutputPath).StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must be inside the working directory.", nameof(request));
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Default.Write($"ProxyForge: 途中まで書き出したファイルを削除できませんでした。{path}", exception);
        }
    }

    sealed class FramePump(
        IVideoFileSource source,
        ProxyFrameRenderer renderer,
        FrameRate frameRate,
        int frameCount,
        ProxyGeometry geometry,
        IProgress<double>? progress)
    {
        const int ProgressSteps = 100;

        int reportedStep = -1;

        public Task WriteAsync(Stream destination, CancellationToken cancellationToken)
            => Task.Factory.StartNew(
                () => Write(destination, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        void Write(Stream destination, CancellationToken cancellationToken)
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            var frameBytes = renderer.FrameByteCount;
            var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
            try
            {
                var frame = buffer.AsSpan(0, frameBytes);
                for (var index = 0; index < frameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    source.Update(frameRate.GetSampleTime(index));
                    renderer.Render(frame);
                    if (!FrameAlpha.IsOpaque(frame, geometry.Width, geometry.Height))
                        throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "The video contains transparent pixels.");

                    destination.Write(frame);
                    Report(index + 1);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        void Report(int completedFrames)
        {
            if (progress is null)
                return;

            var step = (int)((long)completedFrames * ProgressSteps / frameCount);
            if (step == reportedStep)
                return;

            reportedStep = step;
            progress.Report((double)completedFrames / frameCount);
        }
    }
}
