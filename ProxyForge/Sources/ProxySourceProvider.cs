using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal sealed class ProxySourceProvider(
    ProxyCache cache,
    ProxyGenerationQueue queue,
    Func<bool> isExporting,
    Func<ProxyForgeSettings> settings,
    VideoSourceFactory factory)
{
    public IVideoFileSource? Create(IGraphicsDevicesAndContext devices, string filePath)
    {
        var current = settings();
        if (!current.IsEnabled || isExporting())
            return null;

        var identity = SourceIdentity.Of(filePath);
        if (identity is not { } source || source.Length < current.MinimumFileSizeBytes || cache.IsSkipped(source))
            return null;

        var scale = current.Scale;
        var entry = cache.Find(source, scale);
        if (entry is not null)
        {
            var proxy = TryOpen(devices, entry);
            if (proxy is not null)
                return ProxyVideoSource.FromProxy(devices, proxy.Value, new TimeSpan(entry.DurationTicks));
        }

        if (queue.HasFailed(source, scale))
            return null;
        if (!current.GeneratesAutomatically && !queue.IsPending(source, scale))
            return null;

        var original = factory(devices, filePath);
        if (original is null)
            return null;

        if (current.GeneratesAutomatically)
            queue.TryEnqueue(source, scale);

        var wrapper = ProxyVideoSource.FromOriginal(devices, original);
        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, scale, cache, queue, factory));
        return wrapper;
    }

    OpenedProxy? TryOpen(IGraphicsDevicesAndContext devices, ProxyCacheEntry entry)
    {
        try
        {
            var opened = OpenedProxy.Open(devices, cache.GetFilePath(entry), entry, factory);
            if (opened is not null)
                return opened;
        }
        catch (Exception exception)
        {
            Log.Default.Write($"ProxyForge: キャッシュしたプロキシを開けませんでした。{entry.SourcePath}", exception);
        }

        cache.Remove(entry.Id);
        return null;
    }
}
