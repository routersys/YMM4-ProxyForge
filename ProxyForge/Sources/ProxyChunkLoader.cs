using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Sources;

internal sealed class ProxyChunkLoader : IDisposable
{
    readonly Lock gate = new();
    readonly IGraphicsDevices devices;
    readonly SourceIdentity source;
    readonly int scale;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly VideoSourceFactory factory;
    readonly Dictionary<int, OpenedChunk> loaded = [];
    readonly HashSet<int> loading = [];
    ProxyCacheEntry? entry;
    IGraphicsDevicesAndContext? context;
    bool disposed;

    public ProxyChunkLoader(
        IGraphicsDevices devices,
        SourceIdentity source,
        int scale,
        ProxyCacheEntry? entry,
        ProxyCache cache,
        ProxyGenerationQueue queue,
        VideoSourceFactory factory)
    {
        this.devices = devices;
        this.source = source;
        this.scale = scale;
        this.entry = entry;
        this.cache = cache;
        this.queue = queue;
        this.factory = factory;

        queue.ChunkCompleted += OnChunkCompleted;
        queue.EntryDiscarded += OnEntryDiscarded;
    }

    public ProxyCacheEntry? Entry
    {
        get
        {
            using (gate.EnterScope())
                return entry;
        }
    }

    public bool IsLoading
    {
        get
        {
            using (gate.EnterScope())
                return loading.Count > 0;
        }
    }

    public void Request(int chunk)
    {
        ProxyCacheEntry current;
        using (gate.EnterScope())
        {
            if (disposed || entry is null || !entry.HasChunk(chunk) || loading.Contains(chunk) || loaded.ContainsKey(chunk))
                return;

            current = entry;
            loading.Add(chunk);
        }

        Task.Run(() => Load(current, chunk));
    }

    public OpenedChunk? Open(IGraphicsDevicesAndContext target, int chunk)
    {
        ProxyCacheEntry? current;
        using (gate.EnterScope())
            current = disposed ? null : entry;

        return current is not null && current.HasChunk(chunk) ? OpenCore(target, current, chunk) : null;
    }

    public IReadOnlyList<KeyValuePair<int, OpenedChunk>> TakeLoaded()
    {
        using (gate.EnterScope())
        {
            if (loaded.Count == 0)
                return [];

            var taken = loaded.ToArray();
            loaded.Clear();
            return taken;
        }
    }

    void Load(ProxyCacheEntry current, int chunk)
    {
        OpenedChunk? opened = null;
        try
        {
            IGraphicsDevicesAndContext target;
            using (gate.EnterScope())
            {
                if (disposed)
                    return;

                target = context ??= devices.CreateContext();
            }

            opened = OpenCore(target, current, chunk);
        }
        catch (Exception exception)
        {
            Log.Default.Write($"ProxyForge: プロキシのチャンクの読み込みに失敗しました。{source.Path} #{chunk}", exception);
        }
        finally
        {
            using (gate.EnterScope())
            {
                loading.Remove(chunk);
                if (disposed || opened is null)
                    opened?.Dispose();
                else
                    loaded[chunk] = opened;

                if (disposed && loading.Count == 0)
                    ReleaseContext();
            }
        }
    }

    OpenedChunk? OpenCore(IGraphicsDevicesAndContext target, ProxyCacheEntry current, int chunk)
    {
        OpenedChunk? opened = null;
        try
        {
            opened = OpenedChunk.Open(target, cache.GetChunkPath(current, chunk), current, factory);
        }
        catch (Exception exception)
        {
            Log.Default.Write($"ProxyForge: プロキシのチャンクを開けませんでした。{source.Path} #{chunk}", exception);
        }

        if (opened is not null)
            return opened;

        Log.Default.Write($"ProxyForge: 開けないチャンクを取り除きました。{source.Path} #{chunk}");
        var forgotten = cache.ForgetChunk(current.Id, chunk);
        using (gate.EnterScope())
        {
            if (entry is not null && entry.Id == current.Id)
                entry = forgotten;
        }

        return null;
    }

    void OnChunkCompleted(SourceIdentity completedSource, int completedScale, ProxyCacheEntry updated)
    {
        if (completedScale != scale || completedSource != source)
            return;

        using (gate.EnterScope())
        {
            if (!disposed)
                entry = updated;
        }
    }

    void OnEntryDiscarded(SourceIdentity discardedSource, int discardedScale)
    {
        if (discardedScale != scale || discardedSource != source)
            return;

        using (gate.EnterScope())
            entry = null;
    }

    void ReleaseContext()
    {
        context?.Dispose();
        context = null;
    }

    public void Dispose()
    {
        queue.ChunkCompleted -= OnChunkCompleted;
        queue.EntryDiscarded -= OnEntryDiscarded;

        using (gate.EnterScope())
        {
            if (disposed)
                return;

            disposed = true;
            foreach (var opened in loaded.Values)
                opened.Dispose();
            loaded.Clear();

            if (loading.Count == 0)
                ReleaseContext();
        }
    }
}
