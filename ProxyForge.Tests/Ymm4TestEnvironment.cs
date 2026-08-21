using ProxyForge.Transcoding;
using System.IO;
using System.Reflection;

namespace ProxyForge.Tests;

internal static class Ymm4TestEnvironment
{
    private static readonly string BundledFFmpegDirectory = ResolveDirectory();

    internal static bool IsAvailable => File.Exists(FFmpegPath) && File.Exists(FFprobePath);

    internal static string FFmpegPath => Path.Combine(BundledFFmpegDirectory, "ffmpeg.exe");

    internal static string FFprobePath => Path.Combine(BundledFFmpegDirectory, "ffprobe.exe");

    internal static FFmpegExecutables Executables => new(FFmpegPath);

    private static string ResolveDirectory()
    {
        var ymm4Directory = ReadYmm4Directory();
        return ymm4Directory.Length == 0
            ? string.Empty
            : Path.Combine(ymm4Directory, "resources", "bin", "x64", "ffmpeg");
    }

    private static string ReadYmm4Directory()
    {
        foreach (var attribute in typeof(Ymm4TestEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == "YMM4DirPath")
                return attribute.Value ?? string.Empty;
        }

        return string.Empty;
    }
}
