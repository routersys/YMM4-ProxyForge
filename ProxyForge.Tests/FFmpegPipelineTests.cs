using ProxyForge.Core;
using ProxyForge.Plugin;
using ProxyForge.Transcoding;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FFmpegPipelineTests
{
    private const string SoftwareEncoder = FFmpegArguments.SoftwareVideoEncoder;

    private static void RequireFFmpeg()
    {
        if (!Ymm4TestEnvironment.IsAvailable)
            Assert.Skip("YMM4 bundled FFmpeg is not available on this machine.");
    }

    [Fact]
    public async Task EncodeAsync_ProducesProxyAtRequestedScale()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 1280, 720, 30, 1, 30);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        Assert.Equal(640u, entry.ProxyWidth);
        Assert.Equal(360u, entry.ProxyHeight);
        Assert.Equal(0.5f, entry.Scale);

        var proxyPath = entry.GetCurrentDiskPath();
        Assert.NotNull(proxyPath);
        Assert.StartsWith(workspace.Path, proxyPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new FileInfo(proxyPath).Length, entry.DataSize);

        Assert.Equal("640", await workspace.ReadStreamEntryAsync(proxyPath, "width"));
        Assert.Equal("360", await workspace.ReadStreamEntryAsync(proxyPath, "height"));
        Assert.Equal("yuv420p", await workspace.ReadStreamEntryAsync(proxyPath, "pix_fmt"));
        Assert.Equal("h264", await workspace.ReadStreamEntryAsync(proxyPath, "codec_name"));
    }

    [Fact]
    public async Task EncodeAsync_RendersEveryFrameOfTheSource()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 640, 360, 30, 1, 45);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        Assert.Equal("45", await workspace.ReadStreamEntryAsync(entry.GetCurrentDiskPath()!, "nb_frames"));
        Assert.Equal(46, workspace.Source!.UpdateCount);
    }

    [Fact]
    public async Task EncodeAsync_PreservesIntegerFrameRate()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 640, 360, 30, 1, 60);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        var proxyPath = entry.GetCurrentDiskPath()!;
        Assert.Equal("30/1", await workspace.ReadStreamEntryAsync(proxyPath, "r_frame_rate"));
        Assert.Equal("60", await workspace.ReadStreamEntryAsync(proxyPath, "nb_frames"));
        Assert.Equal(
            workspace.Source!.Duration.TotalSeconds,
            await workspace.ReadDurationSecondsAsync(proxyPath),
            2);
    }

    [Fact]
    public async Task EncodeAsync_PreservesNtscFrameRate()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 640, 360, 30000, 1001, 120);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        var proxyPath = entry.GetCurrentDiskPath()!;
        Assert.Equal("30000/1001", await workspace.ReadStreamEntryAsync(proxyPath, "r_frame_rate"));
        Assert.Equal("120", await workspace.ReadStreamEntryAsync(proxyPath, "nb_frames"));
        Assert.Equal(
            workspace.Source!.Duration.TotalSeconds,
            await workspace.ReadDurationSecondsAsync(proxyPath),
            3);
    }

    [Fact]
    public async Task EncodeAsync_PreservesSourceColour()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 640, 360, 30, 1, 10, blue: 32, green: 160, red: 200);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        var (blue, green, red) = await workspace.ReadCentrePixelAsync(entry.GetCurrentDiskPath()!, 320, 180);

        Assert.InRange(blue, 32 - 8, 32 + 8);
        Assert.InRange(green, 160 - 8, 160 + 8);
        Assert.InRange(red, 200 - 8, 200 + 8);
    }

    [Fact]
    public async Task EncodeAsync_UsesTheSourceDisplaySizeIncludingAspect()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 1080, 1920, 30, 1, 10);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        Assert.Equal(540u, entry.ProxyWidth);
        Assert.Equal(960u, entry.ProxyHeight);
    }

    [Fact]
    public async Task EncodeAsync_AlreadyCanceledToken_LeavesNoTemporaryFile()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        using var encoder = CreateEncoder(workspace, 640, 360, 30, 1, 10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, cancellation.Token));

        Assert.Empty(Directory.GetFiles(workspace.Path, "zdp_*.mp4"));
    }

    [Fact]
    public async Task EncodeAsync_WhenNoPluginAcceptsTheFile_Throws()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = new FFmpegProxyEncoder(
            workspace.Path,
            new EncoderConfig(false),
            Ymm4TestEnvironment.Executables,
            static (_, _) => null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken));

        Assert.Contains("video file source plugin", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(workspace.Path, "zdp_*.mp4"));
    }

    [Fact]
    public async Task EncodeAsync_RelativeInputPath_Throws()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 640, 360, 30, 1, 10);
        await Assert.ThrowsAsync<ArgumentException>(() => encoder.EncodeAsync(
            "relative.mp4", 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EncodeAsync_UnavailableExecutables_Throws()
    {
        using var workspace = new Workspace();

        using var encoder = new FFmpegProxyEncoder(
            workspace.Path,
            new EncoderConfig(false),
            FFmpegExecutables.Unavailable,
            static (_, _) => null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_CancellationKillsTheProcessPromptly()
    {
        RequireFFmpeg();
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
            null,
            ProcessPriorityClass.BelowNormal,
            cancellation.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCodeCapturesDiagnostics()
    {
        RequireFFmpeg();
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
            null,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotEqual(string.Empty, result.FirstDiagnosticLine(200));
    }

    [Fact]
    public async Task RunAsync_DecodesNonAsciiDiagnosticsAsUtf8()
    {
        RequireFFmpeg();
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
            null,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("壊れた動画ファイル.mp4", result.Diagnostics);
    }

    [Fact]
    public async Task RunAsync_InputWriterFailurePropagates()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => FFmpegProcessRunner.RunAsync(
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
            null,
            static (_, _) => throw new IOException("frame source exploded"),
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task ResolveAsync_HardwareDisabled_SelectsSoftwareEncoder()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        var encoder = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, false, 960, 540, workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(SoftwareEncoder, encoder);
    }

    [Fact]
    public async Task ResolveAsync_HardwareEnabled_SelectsAnEncoderThatActuallyRuns()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        var encoder = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, true, 320, 240, workspace.Path,
            TestContext.Current.CancellationToken);

        string[] permitted = [SoftwareEncoder, .. FFmpegEncoderCandidates.Hardware264.ToArray()];
        Assert.Contains(encoder, permitted);

        var repeated = await FFmpegEncoderSelector.ResolveAsync(
            Ymm4TestEnvironment.FFmpegPath, true, 320, 240, workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(encoder, repeated);
    }

    [Fact]
    public async Task EncodeAsync_HandlesFullHdSourceAtRealisticLength()
    {
        RequireFFmpeg();
        using var workspace = new Workspace();

        using var encoder = CreateEncoder(workspace, 1920, 1080, 30, 1, 300);
        using var entry = await encoder.EncodeAsync(
            workspace.SourcePath, 0.5f, 0.5f, 30, null, TestContext.Current.CancellationToken);

        Assert.Equal(960u, entry.ProxyWidth);
        Assert.Equal(540u, entry.ProxyHeight);
        Assert.Equal("300", await workspace.ReadStreamEntryAsync(entry.GetCurrentDiskPath()!, "nb_frames"));
    }

    private static FFmpegProxyEncoder CreateEncoder(
        Workspace workspace,
        int width,
        int height,
        int frameRateNumerator,
        int frameRateDenominator,
        int frameCount,
        byte blue = 64,
        byte green = 64,
        byte red = 64)
    {
        Ymm4VideoSourceFactory factory = (devices, _) =>
        {
            var source = new SolidColorVideoSource(
                devices, width, height, frameRateNumerator, frameRateDenominator, frameCount, blue, green, red);
            workspace.Source = source;
            return source;
        };

        return new FFmpegProxyEncoder(
            workspace.Path, new EncoderConfig(false), Ymm4TestEnvironment.Executables, factory);
    }

    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));

        internal string SourcePath => System.IO.Path.Combine(Path, "source.mp4");

        internal SolidColorVideoSource? Source { get; set; }

        internal Workspace() => Directory.CreateDirectory(Path);

        internal async Task<string> ReadStreamEntryAsync(string path, string entry)
        {
            var values = await ProbeAsync(path, "stream=" + entry);
            return values.Length > 0 ? values[0] : string.Empty;
        }

        internal async Task<double> ReadDurationSecondsAsync(string path)
        {
            var values = await ProbeAsync(path, "format=duration");
            return values.Length > 0
                ? double.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture)
                : 0d;
        }

        internal async Task<(byte Blue, byte Green, byte Red)> ReadCentrePixelAsync(string path, int width, int height)
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
                arguments.Add(path);
                arguments.Add("-frames:v");
                arguments.Add("1");
                arguments.Add("-f");
                arguments.Add("rawvideo");
                arguments.Add("-pix_fmt");
                arguments.Add("bgra");
                arguments.Add(rawPath);
            });

            var bytes = await File.ReadAllBytesAsync(rawPath);
            var offset = ((height / 2 * width) + (width / 2)) * 4;
            return (bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        }

        private async Task<string[]> ProbeAsync(string path, string showEntries)
        {
            var values = new List<string>();

            void Collect(ReadOnlySpan<char> line)
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty)
                    values.Add(new string(trimmed));
            }

            var result = await FFmpegProcessRunner.RunAsync(
                Ymm4TestEnvironment.FFprobePath,
                arguments =>
                {
                    arguments.Add("-hide_banner");
                    arguments.Add("-loglevel");
                    arguments.Add("error");
                    arguments.Add("-select_streams");
                    arguments.Add("v:0");
                    arguments.Add("-show_entries");
                    arguments.Add(showEntries);
                    arguments.Add("-of");
                    arguments.Add("csv=p=0");
                    arguments.Add("-i");
                    arguments.Add(path);
                },
                Path,
                Collect,
                null,
                ProcessPriorityClass.BelowNormal,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Diagnostics);
            return [.. values];
        }

        private async Task RunFFmpegAsync(Action<Collection<string>> argumentWriter)
        {
            var result = await FFmpegProcessRunner.RunAsync(
                Ymm4TestEnvironment.FFmpegPath,
                argumentWriter,
                Path,
                null,
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
            catch
            {
            }
        }
    }
}
