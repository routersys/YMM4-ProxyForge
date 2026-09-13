using System.IO;
using ProxyForge.Encoding;

namespace ProxyForge.Tests;

internal static class Ymm4TestEnvironment
{
    static readonly string BundledFFmpegDirectory = Ymm4AssemblyResolver.IsConfigured
        ? Path.Combine(Ymm4AssemblyResolver.Directory, "resources", "bin", "x64", "ffmpeg")
        : string.Empty;

    public static bool IsAvailable => BundledFFmpegDirectory.Length != 0 && File.Exists(FFmpegPath) && File.Exists(FFprobePath);

    public static string FFmpegPath => Path.Combine(BundledFFmpegDirectory, "ffmpeg.exe");

    public static string FFprobePath => Path.Combine(BundledFFmpegDirectory, "ffprobe.exe");

    public static FFmpegExecutables Executables => new(FFmpegPath);

    public static void Require()
    {
        if (!IsAvailable)
            Assert.Skip("YMM4 bundled FFmpeg is not available on this machine.");
    }
}
