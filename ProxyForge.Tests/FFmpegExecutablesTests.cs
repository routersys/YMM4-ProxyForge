using ProxyForge.Encoding;

namespace ProxyForge.Tests;

public sealed class FFmpegExecutablesTests
{
    [Fact]
    public void UnavailableReportsNotAvailable()
    {
        var executables = FFmpegExecutables.Unavailable;

        Assert.False(executables.IsAvailable);
        Assert.Equal(string.Empty, executables.FFmpegPath);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(@"C:\ffmpeg.exe", true)]
    public void IsAvailableRequiresAnExecutablePath(string ffmpegPath, bool expected)
        => Assert.Equal(expected, new FFmpegExecutables(ffmpegPath).IsAvailable);
}
