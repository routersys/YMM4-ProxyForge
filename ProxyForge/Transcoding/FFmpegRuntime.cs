using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;

namespace ProxyForge.Transcoding;

internal readonly record struct FFmpegExecutables(string FFmpegPath)
{
    internal static FFmpegExecutables Unavailable => new(string.Empty);

    internal bool IsAvailable => FFmpegPath.Length != 0;
}

internal static class FFmpegRuntime
{
    private const string FFmpegExecutableName = "ffmpeg.exe";

    private static readonly Lazy<FFmpegExecutables> Resolved =
        new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static FFmpegExecutables Executables => Resolved.Value;

    internal static FFmpegExecutables Require()
    {
        var executables = Resolved.Value;
        if (!executables.IsAvailable)
            throw new InvalidOperationException(
                "The FFmpeg executable bundled with YukkuriMovieMaker could not be located.");

        return executables;
    }

    private static FFmpegExecutables Resolve()
    {
        string directory;
        try
        {
            directory = GetYmm4FFmpegDirectory();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[FFmpegRuntime] FFmpeg directory lookup failed: ", ex.Message));
            return FFmpegExecutables.Unavailable;
        }

        if (string.IsNullOrEmpty(directory))
            return FFmpegExecutables.Unavailable;

        var ffmpegPath = Path.Combine(directory, FFmpegExecutableName);
        if (!File.Exists(ffmpegPath))
        {
            Debug.WriteLine(string.Concat("[FFmpegRuntime] ffmpeg.exe missing in ", directory));
            return FFmpegExecutables.Unavailable;
        }

        return new FFmpegExecutables(ffmpegPath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetYmm4FFmpegDirectory() =>
        FFmpegResourceLocator.GetFFmpegDirectory();
}
