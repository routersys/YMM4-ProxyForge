using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Sources;

internal sealed class ProxyUpgradeLoader : IDisposable
{
    const int Waiting = 0;
    const int Loading = 1;
    const int Finished = 2;

    readonly ProxyVideoSource owner;
    readonly IGraphicsDevices devices;
    readonly SourceIdentity source;
    readonly int scale;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly VideoSourceFactory factory;
    int state;

    public ProxyUpgradeLoader(
        ProxyVideoSource owner,
        IGraphicsDevices devices,
        SourceIdentity source,
        int scale,
        ProxyCache cache,
        ProxyGenerationQueue queue,
        VideoSourceFactory factory)
    {
        this.owner = owner;
        this.devices = devices;
        this.source = source;
        this.scale = scale;
        this.cache = cache;
        this.queue = queue;
        this.factory = factory;

        queue.Completed += OnCompleted;
        var existing = cache.Find(source, scale);
        if (existing is not null)
            Begin(existing);
    }

    void OnCompleted(SourceIdentity completedSource, int completedScale, ProxyCacheEntry entry)
    {
        if (completedScale == scale && completedSource == source)
            Begin(entry);
    }

    void Begin(ProxyCacheEntry entry)
    {
        if (Interlocked.CompareExchange(ref state, Loading, Waiting) != Waiting)
            return;

        queue.Completed -= OnCompleted;
        Task.Run(() => Load(entry));
    }

    void Load(ProxyCacheEntry entry)
    {
        IGraphicsDevicesAndContext? context = null;
        try
        {
            context = devices.CreateContext();
            var opened = OpenedProxy.Open(context, cache.GetFilePath(entry), entry, factory);
            if (opened is null)
            {
                context.Dispose();
                cache.Remove(entry.Id);
                Log.Default.Write($"ProxyForge: 生成したプロキシを開けなかったため取り除きました。{source.Path}");
                return;
            }

            var upgrade = new ProxyUpgrade(context, opened.Value);
            if (!owner.TryHandOver(upgrade))
                upgrade.Dispose();
        }
        catch (Exception exception)
        {
            context?.Dispose();
            Log.Default.Write($"ProxyForge: プロキシへの切り替えに失敗しました。{source.Path}", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref state, Finished) == Waiting)
            queue.Completed -= OnCompleted;
    }
}
