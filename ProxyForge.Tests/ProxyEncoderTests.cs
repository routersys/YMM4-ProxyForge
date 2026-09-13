using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ProxyForge.Encoding;
using ProxyForge.Sources;

namespace ProxyForge.Tests;

[Collection("Direct2D")]
public sealed class ProxyEncoderTests
{
    const string SoftwareEncoder = FFmpegArguments.SoftwareVideoEncoder;

    [Fact]
    public async Task EncodesAProxyAtTheRequestedScale()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(1280, 720, 30, 1, 30);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new ProxyGeometry(640, 360), result.Analysis.Geometry);
        Assert.Equal(new FrameRate(30, 1), result.Analysis.FrameRate);
        Assert.Equal(30, result.Analysis.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Analysis.Duration);
        Assert.Equal(new FileInfo(workspace.OutputPath).Length, result.FileLength);
        Assert.Equal("640", await workspace.ReadStreamEntryAsync("width"));
        Assert.Equal("360", await workspace.ReadStreamEntryAsync("height"));
        Assert.Equal("yuv420p", await workspace.ReadStreamEntryAsync("pix_fmt"));
        Assert.Equal("h264", await workspace.ReadStreamEntryAsync("codec_name"));
    }

    [Fact]
    public async Task MeasuresTheBoundsAsDirect2DRasterisesThem()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(1281, 721, 30, 1, 2, centering: TestCentering.Exact);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new DisplayBounds(-641f, -361f, 1282f, 722f), result.Analysis.Display);
        Assert.Equal(new ProxyGeometry(640, 360), result.Analysis.Geometry);
    }

    [Fact]
    public async Task RendersEveryFrameOfTheSourceOnce()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 45);
        await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal("45", await workspace.ReadStreamEntryAsync("nb_frames"));
        Assert.Equal(46, workspace.Source!.UpdateCount);
    }

    [Fact]
    public async Task ReportsProgressInAscendingSteps()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var reports = new List<double>();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 250);
        await workspace.EncodeAllAsync(encoder, workspace.Request(50), new SynchronousProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.InRange(reports.Count, 100, 101);
        Assert.Equal(1d, reports[^1]);
        Assert.Equal(reports.OrderBy(x => x), reports);
        Assert.Equal(reports.Distinct(), reports);
    }

    [Fact]
    public async Task PreservesAnIntegerFrameRate()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 60);
        await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal("30/1", await workspace.ReadStreamEntryAsync("r_frame_rate"));
        Assert.Equal("60", await workspace.ReadStreamEntryAsync("nb_frames"));
        Assert.Equal(workspace.Source!.Duration.TotalSeconds, await workspace.ReadDurationSecondsAsync(), 2);
    }

    [Fact]
    public async Task PreservesTheNtscFrameRateExactly()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30000, 1001, 120);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new FrameRate(30000, 1001), result.Analysis.FrameRate);
        Assert.Equal("30000/1001", await workspace.ReadStreamEntryAsync("r_frame_rate"));
        Assert.Equal("120", await workspace.ReadStreamEntryAsync("nb_frames"));
        Assert.Equal(workspace.Source!.Duration.TotalSeconds, await workspace.ReadDurationSecondsAsync(), 3);
    }

    [Fact]
    public async Task PreservesTheSourceColour()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10, blue: 32, green: 160, red: 200);
        await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        var (blue, green, red) = await workspace.ReadPixelAsync(320, 180, 160, 90);

        Assert.InRange(blue, 32 - 8, 32 + 8);
        Assert.InRange(green, 160 - 8, 160 + 8);
        Assert.InRange(red, 200 - 8, 200 + 8);
    }

    [Fact]
    public async Task KeepsTheLeftAndRightHalvesWhereTheyBelong()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 4, pixelOf: (x, _) => x < 320 ? ((byte)220, (byte)20, (byte)20, byte.MaxValue) : ((byte)20, (byte)20, (byte)220, byte.MaxValue));
        await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        var left = await workspace.ReadPixelAsync(320, 180, 40, 90);
        var right = await workspace.ReadPixelAsync(320, 180, 280, 90);

        Assert.True(left.Blue > 180 && left.Red < 60, $"left={left}");
        Assert.True(right.Red > 180 && right.Blue < 60, $"right={right}");
    }

    [Theory]
    [InlineData(TestCentering.Floor)]
    [InlineData(TestCentering.Exact)]
    public async Task AlignsAnOddSizedSourceSymmetrically(TestCentering centering)
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(641, 361, 30, 1, 2, centering: centering,
            pixelOf: (x, y) => x == 0 || y == 0 || x == 640 || y == 360 ? ((byte)255, (byte)255, (byte)255, byte.MaxValue) : ((byte)0, (byte)0, (byte)0, byte.MaxValue));
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new ProxyGeometry(320, 180), result.Analysis.Geometry);
        var leftEdge = await workspace.ReadPixelAsync(320, 180, 0, 90);
        var rightEdge = await workspace.ReadPixelAsync(320, 180, 319, 90);
        var topEdge = await workspace.ReadPixelAsync(320, 180, 160, 0);
        var bottomEdge = await workspace.ReadPixelAsync(320, 180, 160, 179);
        var centre = await workspace.ReadPixelAsync(320, 180, 160, 90);

        Assert.True(Math.Abs(leftEdge.Green - rightEdge.Green) <= 24, $"left={leftEdge} right={rightEdge} top={topEdge} bottom={bottomEdge} centre={centre}");
        Assert.True(Math.Abs(topEdge.Green - bottomEdge.Green) <= 24, $"left={leftEdge} right={rightEdge} top={topEdge} bottom={bottomEdge} centre={centre}");
        Assert.True(leftEdge.Green > 60, $"leftEdge={leftEdge}");
        Assert.True(centre.Green < 24, $"centre={centre}");
    }

    [Fact]
    public async Task APortraitSourceProducesAPortraitProxy()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(1080, 1920, 30, 1, 10);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new ProxyGeometry(540, 960), result.Analysis.Geometry);
    }

    [Fact]
    public async Task ATransparentSourceIsRefusedAndLeavesNoFile()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10,
            pixelOf: (x, y) => x is >= 300 and < 340 && y is >= 160 and < 200 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)64, (byte)64, (byte)64, byte.MaxValue));

        var exception = await Assert.ThrowsAsync<ProxyEncodeException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken));

        Assert.Equal(ProxyEncodeFailure.Transparent, exception.Failure);
        Assert.False(File.Exists(workspace.OutputPath));
    }

    [Fact]
    public async Task ASoftEdgeIsNotMistakenForTransparency()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(641, 361, 30, 1, 2, centering: TestCentering.Exact);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Analysis.FrameCount);
        Assert.True(File.Exists(workspace.OutputPath));
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenLeavesNoFile()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), null, cancellation.Token));
        Assert.False(File.Exists(workspace.OutputPath));
    }

    [Fact]
    public async Task CancellationDuringTheEncodeLeavesNoFile()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        using var cancellation = new CancellationTokenSource();

        var encoder = workspace.CreateEncoder(1920, 1080, 30, 1, 3000);
        var progress = new SynchronousProgress(fraction =>
        {
            if (fraction >= 0.05d)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), progress, cancellation.Token));
        Assert.False(File.Exists(workspace.OutputPath));
    }

    [Fact]
    public async Task ASourceNobodyCanOpenIsReported()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = new ProxyEncoder(Ymm4TestEnvironment.Executables, static (_, _) => null);

        var exception = await Assert.ThrowsAsync<ProxyEncodeException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken));

        Assert.Equal(ProxyEncodeFailure.SourceUnavailable, exception.Failure);
        Assert.False(File.Exists(workspace.OutputPath));
    }

    [Fact]
    public async Task ASourceWithoutFramesIsReported()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 0);

        var exception = await Assert.ThrowsAsync<ProxyEncodeException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken));

        Assert.Equal(ProxyEncodeFailure.NoFrames, exception.Failure);
    }

    [Fact]
    public async Task ARelativeSourcePathIsRejected()
    {
        using var workspace = new Workspace();
        var encoder = new ProxyEncoder(Ymm4TestEnvironment.Executables, static (_, _) => null);

        await Assert.ThrowsAsync<ArgumentException>(() => encoder.OpenAsync(workspace.Request(50) with { SourcePath = "relative.mp4" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnOutputOutsideTheWorkingDirectoryIsRejected()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10);
        using var session = await encoder.OpenAsync(workspace.Request(50), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => session.EncodeAsync(0, 10, Path.Combine(Path.GetTempPath(), "escaped.mp4"), null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)]
    [InlineData(5, 6)]
    public async Task AFrameRangeOutsideTheSourceIsRejected(int firstFrame, int frameCount)
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10);
        using var session = await encoder.OpenAsync(workspace.Request(50), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EncodeAsync(firstFrame, frameCount, workspace.OutputPath, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADisposedSessionRefusesToEncode()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 10);
        var session = await encoder.OpenAsync(workspace.Request(50), TestContext.Current.CancellationToken);
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.EncodeAsync(0, 10, workspace.OutputPath, null, TestContext.Current.CancellationToken));
        Assert.True(workspace.Source!.IsDisposed);
    }

    [Fact]
    public async Task ASessionEncodesTheRequestedFrameRangeOnly()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 30,
            framePixelOf: (frame, _, _) => ((byte)(frame * 8), (byte)(frame * 8), (byte)(frame * 8), byte.MaxValue));
        using var session = await encoder.OpenAsync(workspace.Request(50), TestContext.Current.CancellationToken);
        workspace.Source!.RequestedFrames.Clear();

        var length = await session.EncodeAsync(10, 5, workspace.OutputPath, null, TestContext.Current.CancellationToken);

        Assert.Equal(new FileInfo(workspace.OutputPath).Length, length);
        Assert.Equal("5", await workspace.ReadStreamEntryAsync("nb_frames"));
        Assert.Equal([10, 11, 12, 13, 14], workspace.Source.RequestedFrames);
        var (blue, _, _) = await workspace.ReadPixelAsync(320, 180, 160, 90);
        Assert.InRange(blue, 80 - 8, 80 + 8);
    }

    [Fact]
    public async Task ASessionEncodesSeveralChunksFromOneSource()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        var encoder = workspace.CreateEncoder(640, 360, 30, 1, 20);
        using var session = await encoder.OpenAsync(workspace.Request(50), TestContext.Current.CancellationToken);
        var second = Path.Combine(workspace.Path, "second.mp4");

        await session.EncodeAsync(0, 10, workspace.OutputPath, null, TestContext.Current.CancellationToken);
        await session.EncodeAsync(10, 10, second, null, TestContext.Current.CancellationToken);

        Assert.Equal("10", await workspace.ReadStreamEntryAsync("nb_frames"));
        Assert.True(new FileInfo(second).Length > 0);
        Assert.Equal(21, workspace.Source!.UpdateCount);
    }

    [Fact]
    public async Task MissingExecutablesAreReported()
    {
        using var workspace = new Workspace();
        var encoder = new ProxyEncoder(FFmpegExecutables.Unavailable, static (_, _) => null);

        var exception = await Assert.ThrowsAsync<ProxyEncodeException>(() => workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken));

        Assert.Equal(ProxyEncodeFailure.FFmpegUnavailable, exception.Failure);
    }

    [Fact]
    public async Task AFullHdSourceOfRealisticLengthEncodes()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = workspace.CreateEncoder(1920, 1080, 30, 1, 300);
        var result = await workspace.EncodeAllAsync(encoder, workspace.Request(50), null, TestContext.Current.CancellationToken);

        Assert.Equal(new ProxyGeometry(960, 540), result.Analysis.Geometry);
        Assert.Equal("300", await workspace.ReadStreamEntryAsync("nb_frames"));
    }

    [Fact]
    public async Task RunAsyncKillsTheProcessPromptlyOnCancellation()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FFmpegProcessRunner.RunAsync(
            Ymm4TestEnvironment.FFmpegPath,
            static arguments =>
            {
                arguments.Add("-hide_banner");
                arguments.Add("-nostdin");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-f");
                arguments.Add("lavfi");
                arguments.Add("-i");
                arguments.Add("testsrc2=size=1920x1080:rate=30:duration=600");
                arguments.Add("-c:v");
                arguments.Add(SoftwareEncoder);
                arguments.Add("-f");
                arguments.Add("null");
                arguments.Add("-");
            },
            workspace.Path,
            null,
            ProcessPriorityClass.BelowNormal,
            cancellation.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsyncCapturesDiagnosticsOfAFailure()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var result = await FFmpegProcessRunner.RunAsync(
            Ymm4TestEnvironment.FFmpegPath,
            static arguments =>
            {
                arguments.Add("-hide_banner");
                arguments.Add("-nostdin");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-i");
                arguments.Add("nonexistent-input-file.mp4");
                arguments.Add("-f");
                arguments.Add("null");
                arguments.Add("-");
            },
            workspace.Path,
            null,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotEqual(string.Empty, result.FirstDiagnosticLine(200));
        Assert.DoesNotContain('\r', result.Diagnostics);
    }

    [Fact]
    public async Task RunAsyncDecodesNonAsciiDiagnosticsAsUtf8()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var broken = Path.Combine(workspace.Path, "壊れた動画ファイル.mp4");
        await File.WriteAllTextAsync(broken, "not a video", TestContext.Current.CancellationToken);

        var result = await FFmpegProcessRunner.RunAsync(
            Ymm4TestEnvironment.FFmpegPath,
            arguments =>
            {
                arguments.Add("-hide_banner");
                arguments.Add("-nostdin");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-i");
                arguments.Add(broken);
                arguments.Add("-f");
                arguments.Add("null");
                arguments.Add("-");
            },
            workspace.Path,
            null,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("壊れた動画ファイル.mp4", result.Diagnostics);
    }

    [Fact]
    public async Task RunAsyncWrapsAnIoFailureOfTheInputWriterWithTheDiagnostics()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunRawInput(workspace, static (_, _) => throw new IOException("frame source exploded")));

        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task RunAsyncRethrowsOtherFailuresOfTheInputWriterUnchanged()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var failure = await Assert.ThrowsAsync<ProxyEncodeException>(() => RunRawInput(workspace, static (_, _) => throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "transparent")));

        Assert.Equal(ProxyEncodeFailure.Transparent, failure.Failure);
    }

    [Fact]
    public async Task RunAsyncReportsAMissingExecutable()
    {
        using var workspace = new Workspace();

        await Assert.ThrowsAsync<InvalidOperationException>(() => FFmpegProcessRunner.RunAsync(
            Path.Combine(workspace.Path, "missing-ffmpeg.exe"),
            static arguments => arguments.Add("-version"),
            workspace.Path,
            null,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsyncWithHardwareDisabledSelectsTheSoftwareEncoder()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, false, 960, 540, workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(SoftwareEncoder, encoder);
    }

    [Fact]
    public async Task ResolveAsyncWithHardwareEnabledSelectsAnEncoderThatRuns()
    {
        Ymm4TestEnvironment.Require();
        using var workspace = new Workspace();

        var encoder = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, true, 320, 240, workspace.Path, TestContext.Current.CancellationToken);

        string[] permitted = [SoftwareEncoder, .. FFmpegEncoderCandidates.Hardware264.ToArray()];
        Assert.Contains(encoder, permitted);

        var repeated = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, true, 320, 240, workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(encoder, repeated);
    }

    static Task<FFmpegProcessResult> RunRawInput(Workspace workspace, FFmpegInputWriter writer)
        => FFmpegProcessRunner.RunAsync(
            Ymm4TestEnvironment.FFmpegPath,
            static arguments =>
            {
                arguments.Add("-hide_banner");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-f");
                arguments.Add("rawvideo");
                arguments.Add("-pix_fmt");
                arguments.Add("bgra");
                arguments.Add("-s");
                arguments.Add("32x32");
                arguments.Add("-r");
                arguments.Add("30/1");
                arguments.Add("-i");
                arguments.Add("-");
                arguments.Add("-f");
                arguments.Add("null");
                arguments.Add("-");
            },
            workspace.Path,
            writer,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

    sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));

        public string SourcePath => System.IO.Path.Combine(Path, "source.mp4");

        public string OutputPath => System.IO.Path.Combine(Path, "proxy.mp4");

        public TestVideoSource? Source { get; private set; }

        public Workspace() => Directory.CreateDirectory(Path);

        public ProxyEncodeRequest Request(int scale) => new(SourcePath, Path, scale, 50, 30, false);

        public async Task<(ProxyAnalysis Analysis, long FileLength)> EncodeAllAsync(ProxyEncoder encoder, ProxyEncodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            using var session = await encoder.OpenAsync(request, cancellationToken);
            var length = await session.EncodeAsync(0, session.Analysis.FrameCount, OutputPath, progress, cancellationToken);
            return (session.Analysis, length);
        }

        public ProxyEncoder CreateEncoder(
            int width,
            int height,
            int frameRateNumerator,
            int frameRateDenominator,
            int frameCount,
            byte blue = 64,
            byte green = 64,
            byte red = 64,
            TestCentering centering = TestCentering.Floor,
            Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)>? pixelOf = null,
            Func<int, int, int, (byte Blue, byte Green, byte Red, byte Alpha)>? framePixelOf = null)
        {
            var pixel = pixelOf ?? ((_, _) => (blue, green, red, byte.MaxValue));
            return new ProxyEncoder(Ymm4TestEnvironment.Executables, (devices, _) =>
            {
                var source = new TestVideoSource(devices, width, height, frameRateNumerator, frameRateDenominator, frameCount, pixel, centering, framePixelOf);
                Source = source;
                return source;
            });
        }

        public async Task<string> ReadStreamEntryAsync(string entry)
        {
            var values = await ProbeAsync("stream=" + entry);
            return values.Length > 0 ? values[0] : string.Empty;
        }

        public async Task<double> ReadDurationSecondsAsync()
        {
            var values = await ProbeAsync("format=duration");
            return values.Length > 0 ? double.Parse(values[0], CultureInfo.InvariantCulture) : 0d;
        }

        public async Task<(byte Blue, byte Green, byte Red)> ReadPixelAsync(int width, int height, int x, int y)
        {
            var rawPath = System.IO.Path.Combine(Path, "frame.raw");
            await RunFFmpegAsync(arguments =>
            {
                arguments.Add("-hide_banner");
                arguments.Add("-nostdin");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-y");
                arguments.Add("-i");
                arguments.Add(OutputPath);
                arguments.Add("-frames:v");
                arguments.Add("1");
                arguments.Add("-f");
                arguments.Add("rawvideo");
                arguments.Add("-pix_fmt");
                arguments.Add("bgra");
                arguments.Add(rawPath);
            });

            var bytes = await File.ReadAllBytesAsync(rawPath, TestContext.Current.CancellationToken);
            Assert.Equal(width * height * 4, bytes.Length);
            var offset = (y * width + x) * 4;
            return (bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        }

        async Task<string[]> ProbeAsync(string showEntries)
        {
            var startInfo = new ProcessStartInfo(Ymm4TestEnvironment.FFprobePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path,
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-select_streams", "v:0", "-show_entries", showEntries, "-of", "csv=p=0", "-i", OutputPath })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        async Task RunFFmpegAsync(Action<Collection<string>> argumentWriter)
        {
            var result = await FFmpegProcessRunner.RunAsync(
                Ymm4TestEnvironment.FFmpegPath,
                argumentWriter,
                Path,
                null,
                ProcessPriorityClass.BelowNormal,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Diagnostics);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
