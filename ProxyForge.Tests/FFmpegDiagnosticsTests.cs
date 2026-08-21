using ProxyForge.Transcoding;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FFmpegDiagnosticsTests
{
    [Fact]
    public void Append_JoinsLinesWithNewline()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("first");
        buffer.Append("second");

        Assert.Equal("first\nsecond", buffer.ToString());
    }

    [Fact]
    public void Append_SkipsBlankLines()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("");
        buffer.Append("   ");
        buffer.Append("real");
        buffer.Append("\t");

        Assert.Equal("real", buffer.ToString());
    }

    [Fact]
    public void Append_TrimsSurroundingWhitespace()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("  padded  ");

        Assert.Equal("padded", buffer.ToString());
    }

    [Fact]
    public void Append_NeverExceedsCapacity()
    {
        var buffer = new FFmpegDiagnosticsBuffer(16);
        for (var i = 0; i < 1000; i++)
            buffer.Append("0123456789");

        Assert.True(buffer.ToString().Length <= 16);
    }

    [Fact]
    public void Append_KeepsEarliestDiagnosticsWhenTruncating()
    {
        var buffer = new FFmpegDiagnosticsBuffer(16);
        buffer.Append("root cause here");
        buffer.Append("cascading noise");

        Assert.StartsWith("root cause here", buffer.ToString());
    }

    [Fact]
    public void FirstDiagnosticLine_ReturnsLeadingLine()
    {
        var result = new FFmpegProcessResult(1, "Unknown encoder 'h264_nvenc'\nError selecting an encoder");

        Assert.Equal("Unknown encoder 'h264_nvenc'", result.FirstDiagnosticLine(200));
    }

    [Fact]
    public void FirstDiagnosticLine_TruncatesToRequestedLength()
    {
        var result = new FFmpegProcessResult(1, new string('x', 500));

        Assert.Equal(200, result.FirstDiagnosticLine(200).Length);
    }

    [Fact]
    public void FirstDiagnosticLine_EmptyDiagnosticsReturnsEmptyString()
    {
        var result = new FFmpegProcessResult(1, "   \n  ");

        Assert.Equal(string.Empty, result.FirstDiagnosticLine(200));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsSuccess_OnlyZeroExitCodeSucceeds(int exitCode, bool expected) =>
        Assert.Equal(expected, new FFmpegProcessResult(exitCode, string.Empty).IsSuccess);
}
