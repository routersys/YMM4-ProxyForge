using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Tests;

[Collection("Direct2D")]
public sealed class ProxySourceProviderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly SourceFocus focus = new();
    readonly ProxyForgeSettings settings = new() { MinimumFileSizeMegabytes = 1, Scale = 50 };
    readonly List<string> opened = [];
    readonly TaskCompletionSource release = new();
    bool exporting;
    bool refuseAll;
    bool holdOpen;
    int encoded;

    public ProxySourceProviderTests()
    {
        Directory.CreateDirectory(root);
        context = devices.CreateContext();
        cache = new ProxyCache(Path.Combine(root, "cache"));
        queue = CreateQueue(Open);
    }

    public void Dispose()
    {
        release.TrySetResult();
        queue.CancelAll();
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

    ProxyGenerationQueue CreateQueue(ProxyEncodeSessionFactory open)
        => new(cache, open, () => new ProxyEncodeOptions(50, 30, 10, false), () => ExportPhase.Idle, focus, _ => { }, TestUiThread.Post)
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };

    async Task<IProxyEncodeSession> Open(ProxyEncodeRequest request, CancellationToken cancellationToken)
    {
        if (holdOpen)
            await release.Task;

        var analysis = new ProxyAnalysis(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromSeconds(20), 600);
        return new FakeEncodeSession(analysis, (_, _, output, _, _) =>
        {
            encoded++;
            File.WriteAllBytes(output, new byte[64]);
            return Task.FromResult(64L);
        });
    }

    IVideoFileSource? Factory(IGraphicsDevicesAndContext target, string path)
    {
        opened.Add(path);
        if (refuseAll)
            return null;
        if (path.StartsWith(cache.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            return TestVideoSource.Solid(target, 960, 540, 30, 1, 300);

        return TestVideoSource.Solid(target, 1920, 1080, 30, 1, 600);
    }

    ProxySourceProvider CreateProvider(ProxyGenerationQueue? generation = null)
        => new(cache, generation ?? queue, focus, () => exporting, () => settings, Factory);

    string CreateFile(string name = "source.mp4", int megabytes = 2)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[megabytes * 1024 * 1024]);
        return path;
    }

    static SourceIdentity IdentityOf(string path) => SourceIdentity.Of(path)!.Value;

    ProxyCacheEntry Seed(string path, params int[] chunks)
    {
        var identity = IdentityOf(path);
        var entry = cache.Register(new ProxyCacheEntry
        {
            SourcePath = identity.Path,
            SourceLength = identity.Length,
            SourceWriteTimeTicks = identity.WriteTimeTicks,
            Scale = 50,
            ProxyWidth = 960,
            ProxyHeight = 540,
            DisplayLeft = -960f,
            DisplayTop = -540f,
            DisplayWidth = 1920f,
            DisplayHeight = 1080f,
            FrameRateNumerator = 30,
            FrameRateDenominator = 1,
            DurationTicks = TimeSpan.FromSeconds(20).Ticks,
            FrameCount = 600,
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

    static async Task UpdateUntilAsync(ProxyVideoSource proxy, TimeSpan time, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            proxy.Update(time);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public void DisabledSettingsYieldNothing()
    {
        settings.IsEnabled = false;
        var path = CreateFile();

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Empty(opened);
        Assert.False(queue.IsPending(IdentityOf(path), 50));
    }

    [Fact]
    public void NothingIsProxiedWhileExporting()
    {
        exporting = true;
        var path = CreateFile();
        Seed(path, 0, 1);

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Empty(opened);
    }

    [Fact]
    public void AMissingFileYieldsNothing()
    {
        Assert.Null(CreateProvider().Create(context, Path.Combine(root, "missing.mp4")));
        Assert.Empty(opened);
    }

    [Fact]
    public void ASmallFileYieldsNothing()
    {
        settings.MinimumFileSizeMegabytes = 3;
        var path = CreateFile(megabytes: 2);

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Empty(opened);
        Assert.False(queue.IsPending(IdentityOf(path), 50));
    }

    [Fact]
    public void AFileExactlyAtTheThresholdIsProxied()
    {
        settings.MinimumFileSizeMegabytes = 2;
        var path = CreateFile(megabytes: 2);

        using var source = CreateProvider().Create(context, path);

        Assert.NotNull(source);
    }

    [Fact]
    public void ASkippedFileYieldsNothing()
    {
        var path = CreateFile();
        cache.AddSkip(IdentityOf(path), ProxyCacheSkipReason.Transparent);

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Empty(opened);
    }

    [Fact]
    public void ACompleteEntryIsUsedWithoutOpeningTheOriginalOrEncoding()
    {
        var path = CreateFile();
        Seed(path, 0, 1);

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(proxy.HasOriginal);
        Assert.Equal(TimeSpan.FromSeconds(20), proxy.Duration);
        Assert.Empty(opened);
        Assert.False(queue.IsPending(IdentityOf(path), 50));

        proxy.Update(TimeSpan.FromSeconds(12));

        Assert.True(proxy.IsProxy);
        Assert.Equal(1, proxy.ShownChunk);
        Assert.EndsWith("000001.mp4", Assert.Single(opened));
        Assert.Equal(0, encoded);
    }

    [Fact]
    public async Task APartialEntryIsUsedAndTheRestIsQueued()
    {
        var path = CreateFile();
        Seed(path, 0);

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(proxy.HasOriginal);
        Assert.True(queue.IsPending(IdentityOf(path), 50));
        await queue.WhenIdleAsync();
        Assert.Equal(1, encoded);
        Assert.True(cache.Find(IdentityOf(path), 50)!.IsComplete);
    }

    [Fact]
    public async Task ANewFileIsWrappedAroundTheOriginalAndQueued()
    {
        var path = CreateFile();

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.True(proxy.HasOriginal);
        Assert.False(proxy.IsProxy);
        Assert.Equal(TimeSpan.FromSeconds(20), proxy.Duration);
        Assert.Equal(path, Assert.Single(opened));
        await queue.WhenIdleAsync();
        Assert.Equal(2, encoded);
        Assert.True(cache.Find(IdentityOf(path), 50)!.IsComplete);
    }

    [Fact]
    public async Task AFailedFileYieldsNothingAfterwards()
    {
        var path = CreateFile();
        var failing = CreateQueue((_, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));
        failing.TryEnqueue(IdentityOf(path), 50);
        await failing.WhenIdleAsync();

        Assert.Null(CreateProvider(failing).Create(context, path));
        Assert.Empty(opened);
    }

    [Fact]
    public async Task AFailedFileStillUsesItsFinishedChunks()
    {
        var path = CreateFile();
        Seed(path, 0);
        var failing = CreateQueue((_, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));
        failing.TryEnqueue(IdentityOf(path), 50);
        await failing.WhenIdleAsync();

        using var source = CreateProvider(failing).Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(failing.IsPending(IdentityOf(path), 50));
        proxy.Update(TimeSpan.Zero);
        Assert.True(proxy.IsProxy);
    }

    [Fact]
    public void WithoutAutomaticGenerationAnUnknownFileYieldsNothing()
    {
        settings.GeneratesAutomatically = false;
        var path = CreateFile();

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Empty(opened);
        Assert.False(queue.IsPending(IdentityOf(path), 50));
    }

    [Fact]
    public void WithoutAutomaticGenerationAPartialEntryIsStillUsedButNotQueued()
    {
        settings.GeneratesAutomatically = false;
        var path = CreateFile();
        Seed(path, 0);

        using var source = CreateProvider().Create(context, path);

        Assert.IsType<ProxyVideoSource>(source);
        Assert.False(queue.IsPending(IdentityOf(path), 50));
    }

    [Fact]
    public async Task WithoutAutomaticGenerationAPendingFileIsStillWrapped()
    {
        var path = CreateFile();
        var identity = IdentityOf(path);
        holdOpen = true;
        queue.TryEnqueue(identity, 50);
        settings.GeneratesAutomatically = false;

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.True(proxy.HasOriginal);
        Assert.Equal(TimeSpan.FromSeconds(20), proxy.Duration);
        release.SetResult();
        await queue.WhenIdleAsync();
        await UpdateUntilAsync(proxy, TimeSpan.Zero, () => proxy.IsProxy);
    }

    [Fact]
    public void AFileNoPluginCanOpenYieldsNothingAndIsNotQueued()
    {
        refuseAll = true;
        var path = CreateFile();

        Assert.Null(CreateProvider().Create(context, path));
        Assert.Single(opened);
        Assert.False(queue.IsPending(IdentityOf(path), 50));
    }

    [Fact]
    public async Task TheWrapperSwitchesToTheChunksAsTheyAreGenerated()
    {
        var path = CreateFile();

        using var source = CreateProvider().Create(context, path);
        var proxy = Assert.IsType<ProxyVideoSource>(source);
        await queue.WhenIdleAsync();
        await UpdateUntilAsync(proxy, TimeSpan.FromSeconds(1), () => proxy.IsProxy);

        Assert.Equal(0, proxy.ShownChunk);
        Assert.Equal(TimeSpan.FromSeconds(20), proxy.Duration);
        Assert.Contains(opened, candidate => candidate.EndsWith("000000.mp4", StringComparison.Ordinal));
        await UpdateUntilAsync(proxy, TimeSpan.FromSeconds(1), () => !proxy.HasOriginal);
    }

    [Fact]
    public void TheFocusFollowsTheRequestedFrame()
    {
        var path = CreateFile();
        using var source = CreateProvider().Create(context, path);

        source!.Update(TimeSpan.FromSeconds(3));

        Assert.Equal(90, focus.Get(IdentityOf(path)));
    }
}
