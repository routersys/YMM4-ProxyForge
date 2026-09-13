using System.Collections.ObjectModel;
using System.Globalization;
using ProxyForge.Sources;

namespace ProxyForge.Encoding;

internal readonly record struct FFmpegEncodeRequest(
    string OutputPath,
    int Width,
    int Height,
    FrameRate FrameRate,
    long BitrateBitsPerSecond,
    int KeyFrameInterval,
    string VideoEncoder);

internal static class FFmpegArguments
{
    public const string SoftwareVideoEncoder = "h264";
    public const string InputPixelFormat = "bgra";
    public const string OutputPixelFormat = "yuv420p";
    public const string ColorSpace = "bt709";
    public const double DefaultFrameRate = 30d;

    const double BitratePerPixelPerSecond = 0.07;
    const long MinimumBitrate = 100_000L;
    const long MaximumBitrate = 100_000_000L;
    const double MinimumFrameRate = 1d;
    const double MaximumFrameRate = 240d;

    public static void WriteEncode(Collection<string> arguments, in FFmpegEncodeRequest request)
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
        arguments.Add(request.KeyFrameInterval.ToString(CultureInfo.InvariantCulture));
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

    public static void WriteEncoderProbe(Collection<string> arguments, string encoder, int width, int height)
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

    public static string BuildFrameSize(int width, int height)
        => string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    public static string BuildFrameRate(FrameRate frameRate)
        => string.Create(CultureInfo.InvariantCulture, $"{frameRate.Numerator}/{frameRate.Denominator}");

    public static string BuildColorSource(int width, int height)
        => string.Create(CultureInfo.InvariantCulture, $"color=c=black:s={width}x{height}:r=30");

    public static long CalculateVideoBitrate(int width, int height, double frameRate, int bitrateScale)
    {
        var rate = double.IsFinite(frameRate) && frameRate > 0d
            ? Math.Clamp(frameRate, MinimumFrameRate, MaximumFrameRate)
            : DefaultFrameRate;
        var scale = bitrateScale > 0 ? bitrateScale / 100d : 1d;
        var bitrate = (double)width * height * rate * BitratePerPixelPerSecond * scale;

        if (!double.IsFinite(bitrate))
            return MaximumBitrate;

        return (long)Math.Clamp(bitrate, MinimumBitrate, MaximumBitrate);
    }
}
