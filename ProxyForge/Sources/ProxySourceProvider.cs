using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal sealed class ProxySourceProvider(
    ProxyCache cache,
    ProxyGenerationQueue queue,
    SourceFocus focus,
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
        if (entry is null && (queue.HasFailed(source, scale) || !current.GeneratesAutomatically && !queue.IsPending(source, scale)))
            return null;

        IVideoFileSource? original = null;
        if (entry is null)
        {
            original = factory(devices, filePath);
            if (original is null)
                return null;
        }

        if (current.GeneratesAutomatically && (entry is null || !entry.IsComplete))
            queue.TryEnqueue(source, scale);

        var loader = new ProxyChunkLoader(devices, source, scale, entry, cache, queue, factory);
        return new ProxyVideoSource(devices, source, original, entry, loader, factory, focus);
    }
}
