using ProxyForge.Transcoding;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FFmpegExecutablesTests
{
    [Fact]
    public void Unavailable_ReportsNotAvailable()
    {
        var executables = FFmpegExecutables.Unavailable;

        Assert.False(executables.IsAvailable);
        Assert.Equal(string.Empty, executables.FFmpegPath);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(@"C:\ffmpeg.exe", true)]
    public void IsAvailable_RequiresTheExecutablePath(string ffmpegPath, bool expected) =>
        Assert.Equal(expected, new FFmpegExecutables(ffmpegPath).IsAvailable);
}
