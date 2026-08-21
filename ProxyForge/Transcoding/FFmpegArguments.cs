using System.Collections.ObjectModel;
using System.Globalization;

namespace ProxyForge.Transcoding;

internal readonly record struct FFmpegEncodeRequest(
    string OutputPath,
    uint Width,
    uint Height,
    FrameRate FrameRate,
    long BitrateBitsPerSecond,
    int GopSize,
    string VideoEncoder);

internal static class FFmpegArguments
{
    internal const string SoftwareVideoEncoder = "h264";
    internal const string InputPixelFormat = "bgra";
    internal const string OutputPixelFormat = "yuv420p";
    internal const string ColorSpace = "bt709";

    private const double BitratePerPixelPerSecond = 0.07;
    private const long MinimumBitrate = 100_000L;
    private const long MaximumBitrate = 100_000_000L;
    private const double MinimumFrameRate = 1.0;
    private const double MaximumFrameRate = 240.0;

    internal const double DefaultFrameRate = 30.0;

    internal const string ProbeShowEntries =
        "stream=width,height,avg_frame_rate,r_frame_rate:stream_side_data=rotation:format=duration";

    internal static void WriteProbe(Collection<string> arguments, string inputPath)
    {
        arguments.Add("-hide_banner");
        arguments.Add("-loglevel");
        arguments.Add("error");
        arguments.Add("-select_streams");
        arguments.Add("v:0");
        arguments.Add("-show_entries");
        arguments.Add(ProbeShowEntries);
        arguments.Add("-of");
        arguments.Add("default");
        arguments.Add("-i");
        arguments.Add(inputPath);
    }

    internal static void WriteEncode(Collection<string> arguments, in FFmpegEncodeRequest request)
    {
        arguments.Add("-hide_banner");
        arguments.Add("-loglevel");
        arguments.Add("error");
        arguments.Add("-y");
        arguments.Add("-f");
        arguments.Add("rawvideo");
        arguments.Add("-pix_fmt");
        arguments.Add(InputPixelFormat);
        arguments.Add("-s");
        arguments.Add(BuildFrameSize(request.Width, request.Height));
        arguments.Add("-r");
        arguments.Add(BuildFrameRate(request.FrameRate));
        arguments.Add("-i");
        arguments.Add("-");
        arguments.Add("-an");
        arguments.Add("-sn");
        arguments.Add("-dn");
        arguments.Add("-c:v");
        arguments.Add(request.VideoEncoder);
        arguments.Add("-b:v");
        arguments.Add(request.BitrateBitsPerSecond.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-g");
        arguments.Add(request.GopSize.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-pix_fmt");
        arguments.Add(OutputPixelFormat);
        arguments.Add("-colorspace");
        arguments.Add(ColorSpace);
        arguments.Add("-color_primaries");
        arguments.Add(ColorSpace);
        arguments.Add("-color_trc");
        arguments.Add(ColorSpace);
        arguments.Add("-color_range");
        arguments.Add("tv");
        arguments.Add("-f");
        arguments.Add("mp4");
        arguments.Add(request.OutputPath);
    }

    internal static void WriteEncoderProbe(Collection<string> arguments, string encoder, uint width, uint height)
    {
        arguments.Add("-hide_banner");
        arguments.Add("-nostdin");
        arguments.Add("-loglevel");
        arguments.Add("error");
        arguments.Add("-f");
        arguments.Add("lavfi");
        arguments.Add("-i");
        arguments.Add(BuildColorSource(width, height));
        arguments.Add("-frames:v");
        arguments.Add("1");
        arguments.Add("-pix_fmt");
        arguments.Add(OutputPixelFormat);
        arguments.Add("-c:v");
        arguments.Add(encoder);
        arguments.Add("-f");
        arguments.Add("null");
        arguments.Add("-");
    }

    internal static string BuildFrameSize(uint width, uint height) =>
        string.Create(CultureInfo.InvariantCulture, stackalloc char[32], $"{width}x{height}");

    internal static string BuildFrameRate(FrameRate frameRate) =>
        string.Create(CultureInfo.InvariantCulture, stackalloc char[32],
            $"{frameRate.Numerator}/{frameRate.Denominator}");

    internal static string BuildColorSource(uint width, uint height) =>
        string.Create(CultureInfo.InvariantCulture, stackalloc char[64],
            $"color=c=black:s={width}x{height}:r=30");

    internal static long CalculateVideoBitrate(uint width, uint height, double frameRate, float bitrateFactor)
    {
        var rate = double.IsFinite(frameRate) && frameRate > 0
            ? Math.Clamp(frameRate, MinimumFrameRate, MaximumFrameRate)
            : DefaultFrameRate;

        var factor = float.IsFinite(bitrateFactor) && bitrateFactor > 0 ? bitrateFactor : 1f;
        var bitrate = (double)width * height * rate * BitratePerPixelPerSecond * factor;

        if (!double.IsFinite(bitrate))
            return MaximumBitrate;

        return (long)Math.Clamp(bitrate, MinimumBitrate, MaximumBitrate);
    }
}
