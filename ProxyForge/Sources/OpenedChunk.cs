using System.Numerics;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal sealed class OpenedChunk(IVideoFileSource source, Matrix3x2 transform) : IDisposable
{
    public IVideoFileSource Source { get; } = source;

    public Matrix3x2 Transform { get; } = transform;

    public static Matrix3x2 Align(DisplayBounds proxy, ProxyCacheEntry entry)
        => Matrix3x2.CreateTranslation(-proxy.Left, -proxy.Top)
            * Matrix3x2.CreateScale(entry.DisplayWidth / proxy.Width, entry.DisplayHeight / proxy.Height)
            * Matrix3x2.CreateTranslation(entry.DisplayLeft, entry.DisplayTop);

    public static OpenedChunk? Open(IGraphicsDevicesAndContext context, string chunkPath, ProxyCacheEntry entry, VideoSourceFactory factory)
    {
        var frameRate = new FrameRate(entry.FrameRateNumerator, entry.FrameRateDenominator);
        if (!frameRate.IsValid || entry.DisplayWidth <= 0f || entry.DisplayHeight <= 0f)
            return null;

        var source = factory(context, chunkPath);
        if (source is null)
            return null;

        try
        {
            source.Update(frameRate.GetSampleTime(0));
            var bounds = DisplayBounds.Measure(context.DeviceContext, source.Output);
            if (!bounds.IsUsable)
            {
                source.Dispose();
                return null;
            }

            return new OpenedChunk(source, Align(bounds, entry));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void Dispose() => Source.Dispose();
}
