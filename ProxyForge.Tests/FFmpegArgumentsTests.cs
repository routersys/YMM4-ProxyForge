using ProxyForge.Transcoding;
using System.Collections.ObjectModel;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FFmpegArgumentsTests
{
    [Fact]
    public void WriteEncode_EmitsDeterministicRawVideoPipeline()
    {
        var request = new FFmpegEncodeRequest(
            @"C:\cache\tmp\zdp_proxy.mp4",
            960,
            540,
            new FrameRate(30000, 1001),
            544_320,
            30,
            "h264_qsv");

        var arguments = new Collection<string>();
        FFmpegArguments.WriteEncode(arguments, in request);

        string[] expected =
        [
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", "960x540",
            "-r", "30000/1001",
            "-i", "-",
            "-an", "-sn", "-dn",
            "-c:v", "h264_qsv",
            "-b:v", "544320",
            "-g", "30",
            "-pix_fmt", "yuv420p",
            "-colorspace", "bt709",
            "-color_primaries", "bt709",
            "-color_trc", "bt709",
            "-color_range", "tv",
            "-f", "mp4",
            @"C:\cache\tmp\zdp_proxy.mp4",
        ];

        Assert.Equal(expected, arguments);
    }

    [Fact]
    public void WriteEncode_DoesNotDisableStandardInput()
    {
        var request = new FFmpegEncodeRequest(
            @"C:\cache\tmp\zdp_proxy.mp4", 320, 240, new FrameRate(30, 1), 100_000, 1, "h264");

        var arguments = new Collection<string>();
        FFmpegArguments.WriteEncode(arguments, in request);

        Assert.DoesNotContain("-nostdin", arguments);
    }

    [Fact]
    public void WriteEncode_PassesOutputPathWithoutQuoting()
    {
        var request = new FFmpegEncodeRequest(
            @"C:\cache tmp\a b'c,d.mp4", 320, 240, new FrameRate(30, 1), 100_000, 1, "h264");

        var arguments = new Collection<string>();
        FFmpegArguments.WriteEncode(arguments, in request);

        Assert.Contains(@"C:\cache tmp\a b'c,d.mp4", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains('"'));
    }

    [Fact]
    public void WriteEncoderProbe_MatchesSingleFrameNullMuxProbe()
    {
        var arguments = new Collection<string>();
        FFmpegArguments.WriteEncoderProbe(arguments, "h264_nvenc", 960, 540);

        string[] expected =
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=black:s=960x540:r=30",
            "-frames:v", "1",
            "-pix_fmt", "yuv420p",
            "-c:v", "h264_nvenc",
            "-f", "null",
            "-",
        ];

        Assert.Equal(expected, arguments);
    }

    [Theory]
    [InlineData(960u, 540u, "960x540")]
    [InlineData(2u, 2u, "2x2")]
    [InlineData(3840u, 2160u, "3840x2160")]
    public void BuildFrameSize_UsesInvariantNumbers(uint width, uint height, string expected) =>
        Assert.Equal(expected, FFmpegArguments.BuildFrameSize(width, height));

    [Theory]
    [InlineData(30, 1, "30/1")]
    [InlineData(30000, 1001, "30000/1001")]
    [InlineData(24, 1, "24/1")]
    public void BuildFrameRate_EmitsRationalForm(int numerator, int denominator, string expected) =>
        Assert.Equal(expected, FFmpegArguments.BuildFrameRate(new FrameRate(numerator, denominator)));

    [Fact]
    public void BuildColorSource_MatchesLavfiSyntax() =>
        Assert.Equal("color=c=black:s=1280x720:r=30", FFmpegArguments.BuildColorSource(1280, 720));

    [Fact]
    public void CalculateVideoBitrate_UsesPixelRateFormula() =>
        Assert.Equal(544_320L, FFmpegArguments.CalculateVideoBitrate(960, 540, 30.0, 0.5f));

    [Fact]
    public void CalculateVideoBitrate_ClampsToMinimum() =>
        Assert.Equal(100_000L, FFmpegArguments.CalculateVideoBitrate(2, 2, 1.0, 0.01f));

    [Fact]
    public void CalculateVideoBitrate_ClampsToMaximum() =>
        Assert.Equal(100_000_000L, FFmpegArguments.CalculateVideoBitrate(7680, 4320, 240.0, 2f));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CalculateVideoBitrate_UnknownFrameRateUsesDefault(double frameRate) =>
        Assert.Equal(
            FFmpegArguments.CalculateVideoBitrate(960, 540, FFmpegArguments.DefaultFrameRate, 0.5f),
            FFmpegArguments.CalculateVideoBitrate(960, 540, frameRate, 0.5f));

    [Fact]
    public void CalculateVideoBitrate_ClampsExcessiveFrameRate() =>
        Assert.Equal(
            FFmpegArguments.CalculateVideoBitrate(960, 540, 240.0, 0.5f),
            FFmpegArguments.CalculateVideoBitrate(960, 540, 100_000.0, 0.5f));

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void CalculateVideoBitrate_InvalidFactorFallsBackToUnity(float factor) =>
        Assert.Equal(
            FFmpegArguments.CalculateVideoBitrate(960, 540, 30.0, 1f),
            FFmpegArguments.CalculateVideoBitrate(960, 540, 30.0, factor));
}
