using System.Numerics;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal readonly record struct OpenedProxy(IVideoFileSource Source, Matrix3x2 Transform, FrameRate FrameRate)
{
    public static Matrix3x2 Align(DisplayBounds proxy, ProxyCacheEntry entry)
        => Matrix3x2.CreateTranslation(-proxy.Left, -proxy.Top)
            * Matrix3x2.CreateScale(entry.DisplayWidth / proxy.Width, entry.DisplayHeight / proxy.Height)
            * Matrix3x2.CreateTranslation(entry.DisplayLeft, entry.DisplayTop);

    public static OpenedProxy? Open(IGraphicsDevicesAndContext context, string proxyPath, ProxyCacheEntry entry, VideoSourceFactory factory)
    {
        var source = factory(context, proxyPath);
        if (source is null)
            return null;

        try
        {
            var frameRate = new FrameRate(entry.FrameRateNumerator, entry.FrameRateDenominator);
            if (!frameRate.IsValid || entry.DisplayWidth <= 0f || entry.DisplayHeight <= 0f)
            {
                source.Dispose();
                return null;
            }

            source.Update(frameRate.GetSampleTime(0));
            var bounds = DisplayBounds.Measure(context.DeviceContext, source.Output);
            if (!bounds.IsUsable)
            {
                source.Dispose();
                return null;
            }

            return new OpenedProxy(source, Align(bounds, entry), frameRate);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }
}

internal sealed class ProxyUpgrade(IGraphicsDevicesAndContext context, OpenedProxy proxy) : IDisposable
{
    public IGraphicsDevicesAndContext Context { get; } = context;

    public OpenedProxy Proxy { get; } = proxy;

    public void Dispose()
    {
        Proxy.Source.Dispose();
        Context.Dispose();
    }
}
