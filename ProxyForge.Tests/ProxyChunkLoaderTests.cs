using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Tests;

[Collection("Direct2D")]
public sealed class ProxyChunkLoaderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly SourceFocus focus = new();
    readonly List<TestVideoSource> created = [];
    readonly TaskCompletionSource gate = new();
    bool refuse;
    bool hold;
    bool transparent;

    public ProxyChunkLoaderTests()
    {
        Directory.CreateDirectory(root);
        context = devices.CreateContext();
        cache = new ProxyCache(Path.Combine(root, "cache"));
        queue = new ProxyGenerationQueue(cache, Open, () => new ProxyEncodeOptions(50, 30, 10, false), () => ExportPhase.Idle, focus, _ => { }, TestUiThread.Post)
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
        gate.TrySetResult();
        context.Dispose();
        devices.Dispose();
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    Task<IProxyEncodeSession> Open(ProxyEncodeRequest request, CancellationToken cancellationToken)
    {
        var analysis = new ProxyAnalysis(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromSeconds(30), 900);
        return Task.FromResult<IProxyEncodeSession>(new FakeEncodeSession(analysis, (_, _, output, _, _) =>
        {
            if (transparent)
                throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "alpha");

            File.WriteAllBytes(output, new byte[64]);
            return Task.FromResult(64L);
        }));
    }

    IVideoFileSource? Factory(IGraphicsDevicesAndContext target, string path)
    {
        if (hold)
            gate.Task.Wait();
        if (refuse)
            return null;

        var source = TestVideoSource.Solid(target, 960, 540, 30, 1, 300);
        created.Add(source);
        return source;
    }

    SourceIdentity CreateSource(string name = "source.mp4")
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[100]);
        return SourceIdentity.Of(path)!.Value;
    }

    ProxyCacheEntry Register(SourceIdentity source, int scale = 50, params int[] chunks)
    {
        var entry = cache.Register(new ProxyCacheEntry
        {
            SourcePath = source.Path,
            SourceLength = source.Length,
            SourceWriteTimeTicks = source.WriteTimeTicks,
            Scale = scale,
            ProxyWidth = 960,
            ProxyHeight = 540,
            DisplayLeft = -960f,
            DisplayTop = -540f,
            DisplayWidth = 1920f,
            DisplayHeight = 1080f,
            FrameRateNumerator = 30,
            FrameRateDenominator = 1,
            DurationTicks = TimeSpan.FromSeconds(30).Ticks,
            FrameCount = 900,
            ChunkLength = 300,
        });
        foreach (var chunk in chunks)
        {
            var temporary = cache.CreateTemporaryPath();
            File.WriteAllBytes(temporary, new byte[64]);
            entry = cache.AddChunk(entry.Id, chunk, temporary)!;
        }

        return entry;
    }

    ProxyChunkLoader CreateLoader(SourceIdentity source, ProxyCacheEntry? entry, int scale = 50)
        => new(context, source, scale, entry, cache, queue, Factory);

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    static async Task<IReadOnlyList<KeyValuePair<int, OpenedChunk>>> TakeAsync(ProxyChunkLoader loader)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var taken = loader.TakeLoaded();
            if (taken.Count > 0)
                return taken;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("nothing was loaded");
    }

    [Fact]
    public async Task ARequestedChunkIsOpenedInTheBackgroundOnce()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0, 1);
        using var loader = CreateLoader(source, entry);

        loader.Request(1);
        loader.Request(1);
        var taken = await TakeAsync(loader);

        var single = Assert.Single(taken);
        Assert.Equal(1, single.Key);
        Assert.Same(created.Single(), single.Value.Source);
        Assert.Empty(loader.TakeLoaded());
        Assert.False(loader.IsLoading);
        single.Value.Dispose();
    }

    [Fact]
    public async Task AChunkThatIsNotInTheEntryIsNotRequested()
    {
        var source = CreateSource();
        using var loader = CreateLoader(source, Register(source, 50, 0));

        loader.Request(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(created);
        Assert.Empty(loader.TakeLoaded());
        Assert.False(loader.IsLoading);
    }

    [Fact]
    public void WithoutAnEntryNothingIsAvailable()
    {
        var source = CreateSource();
        using var loader = CreateLoader(source, null);

        loader.Request(0);

        Assert.Null(loader.Entry);
        Assert.Null(loader.Open(context, 0));
        Assert.Empty(created);
    }

    [Fact]
    public void OpenReturnsTheChunkSynchronouslyOnTheGivenContext()
    {
        var source = CreateSource();
        using var loader = CreateLoader(source, Register(source, 50, 0));

        var opened = loader.Open(context, 0);

        Assert.NotNull(opened);
        Assert.Same(created.Single(), opened.Source);
        Assert.Null(loader.Open(context, 1));
        opened.Dispose();
    }

    [Fact]
    public void AChunkThatCannotBeOpenedIsForgotten()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0, 1);
        using var loader = CreateLoader(source, entry);
        refuse = true;

        Assert.Null(loader.Open(context, 0));

        Assert.Equal([1], cache.Get(entry.Id)!.Chunks);
        Assert.Equal([1], loader.Entry!.Chunks);
        Assert.False(File.Exists(cache.GetChunkPath(entry, 0)));
    }

    [Fact]
    public async Task ABackgroundFailureIsForgottenAsWell()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0, 1);
        using var loader = CreateLoader(source, entry);
        refuse = true;

        loader.Request(1);
        await WaitUntilAsync(() => !loader.IsLoading);

        Assert.Empty(loader.TakeLoaded());
        Assert.Equal([0], loader.Entry!.Chunks);
        Assert.Equal([0], cache.Get(entry.Id)!.Chunks);
    }

    [Fact]
    public async Task ChunksGeneratedLaterBecomeAvailable()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0);
        using var loader = CreateLoader(source, entry);

        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();

        Assert.Equal([0, 1, 2], loader.Entry!.Chunks);
        loader.Request(2);
        var taken = await TakeAsync(loader);
        Assert.Equal(2, Assert.Single(taken).Key);
        taken[0].Value.Dispose();
    }

    [Fact]
    public async Task AnAnnouncementForAnotherScaleOrFileIsIgnored()
    {
        var source = CreateSource();
        var other = CreateSource("other.mp4");
        Register(source, 25);
        Register(other, 50);
        using var loader = CreateLoader(source, null);

        Assert.True(queue.TryEnqueue(source, 25));
        Assert.True(queue.TryEnqueue(other, 50));
        await queue.WhenIdleAsync();

        Assert.Null(loader.Entry);
    }

    [Fact]
    public async Task ADiscardedEntryIsForgotten()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0);
        using var loader = CreateLoader(source, entry);
        transparent = true;

        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();

        Assert.Null(loader.Entry);
        Assert.Null(loader.Open(context, 0));
    }

    [Fact]
    public async Task DisposingDropsLoadedChunksAndIgnoresLateAnnouncements()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0, 1);
        var loader = CreateLoader(source, entry);
        loader.Request(0);
        await WaitUntilAsync(() => !loader.IsLoading);

        loader.Dispose();
        loader.Dispose();

        Assert.True(created.Single().IsDisposed);
        Assert.Empty(loader.TakeLoaded());
        loader.Request(1);
        Assert.False(loader.IsLoading);
        Assert.Null(loader.Open(context, 1));

        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();
        Assert.Equal([0, 1], loader.Entry!.Chunks);
    }

    [Fact]
    public async Task ALoadThatFinishesAfterDisposeIsDiscarded()
    {
        var source = CreateSource();
        var entry = Register(source, 50, 0);
        var loader = CreateLoader(source, entry);
        hold = true;
        loader.Request(0);
        await WaitUntilAsync(() => loader.IsLoading);

        loader.Dispose();
        gate.SetResult();
        await WaitUntilAsync(() => !loader.IsLoading);

        Assert.True(created.Single().IsDisposed);
        Assert.Empty(loader.TakeLoaded());
    }
}
