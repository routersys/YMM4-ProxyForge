using System.IO;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;

namespace ProxyForge.Encoding;

internal readonly record struct FFmpegExecutables(string FFmpegPath)
{
    public static FFmpegExecutables Unavailable => new(string.Empty);

    public bool IsAvailable => FFmpegPath.Length != 0;
}

internal static class FFmpegRuntime
{
    static readonly Lazy<FFmpegExecutables> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static FFmpegExecutables Executables => Resolved.Value;

    static FFmpegExecutables Resolve()
    {
        string path;
        try
        {
            path = GetFFmpegExePath();
        }
        catch (Exception exception) when (exception is TypeLoadException or MissingMemberException or FileNotFoundException)
        {
            Log.Default.Write("ProxyForge: YMM4 の ffmpeg の場所を取得できませんでした。", exception);
            return FFmpegExecutables.Unavailable;
        }

        if (!File.Exists(path))
        {
            Log.Default.Write($"ProxyForge: ffmpeg.exe が見つかりません。{path}");
            return FFmpegExecutables.Unavailable;
        }

        return new FFmpegExecutables(path);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string GetFFmpegExePath() => FFmpegResourceLocator.GetFFmpegExePath();
}
