using ProxyForge.Encoding;

namespace ProxyForge.Tests;

public sealed class FFmpegDiagnosticsTests
{
    [Fact]
    public void AppendJoinsLinesWithANewline()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("first");
        buffer.Append("second");

        Assert.Equal("first\nsecond", buffer.ToString());
    }

    [Fact]
    public void AppendSkipsBlankLines()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("");
        buffer.Append("   ");
        buffer.Append("real");
        buffer.Append("\t");

        Assert.Equal("real", buffer.ToString());
    }

    [Fact]
    public void AppendTrimsSurroundingWhitespace()
    {
        var buffer = new FFmpegDiagnosticsBuffer(4096);
        buffer.Append("  padded  ");

        Assert.Equal("padded", buffer.ToString());
    }

    [Fact]
    public void AppendNeverExceedsTheCapacity()
    {
        var buffer = new FFmpegDiagnosticsBuffer(16);
        for (var i = 0; i < 1000; i++)
            buffer.Append("0123456789");

        Assert.True(buffer.ToString().Length <= 16);
    }

    [Fact]
    public void AppendKeepsTheEarliestDiagnosticsWhenTruncating()
    {
        var buffer = new FFmpegDiagnosticsBuffer(16);
        buffer.Append("root cause here");
        buffer.Append("cascading noise");

        Assert.StartsWith("root cause here", buffer.ToString());
    }

    [Fact]
    public void AppendCutsALineThatOverflowsTheCapacity()
    {
        var buffer = new FFmpegDiagnosticsBuffer(8);
        buffer.Append("0123456789");

        Assert.Equal("01234567", buffer.ToString());
    }

    [Fact]
    public void FirstDiagnosticLineReturnsTheLeadingLine()
    {
        var result = new FFmpegProcessResult(1, "Unknown encoder 'h264_nvenc'\nError selecting an encoder");

        Assert.Equal("Unknown encoder 'h264_nvenc'", result.FirstDiagnosticLine(200));
    }

    [Fact]
    public void FirstDiagnosticLineTruncatesToTheRequestedLength()
    {
        var result = new FFmpegProcessResult(1, new string('x', 500));

        Assert.Equal(200, result.FirstDiagnosticLine(200).Length);
    }

    [Fact]
    public void FirstDiagnosticLineOfBlankDiagnosticsIsEmpty()
    {
        var result = new FFmpegProcessResult(1, "   \n  ");

        Assert.Equal(string.Empty, result.FirstDiagnosticLine(200));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void OnlyAZeroExitCodeSucceeds(int exitCode, bool expected)
        => Assert.Equal(expected, new FFmpegProcessResult(exitCode, string.Empty).IsSuccess);
}
