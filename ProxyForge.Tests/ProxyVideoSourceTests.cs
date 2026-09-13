using System.IO;
using System.Numerics;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Tests;

[Collection("Direct2D")]
public sealed class ProxyVideoSourceTests : IDisposable
{
    const int ChunkLength = 300;

    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly SourceFocus focus = new();
    readonly List<TestVideoSource> chunkSources = [];
    readonly List<string> openedPaths = [];
    int frameCount = 900;
    bool refuseChunks;
    bool refuseOriginal;
    bool transparent;

    public ProxyVideoSourceTests()
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
        var analysis = new ProxyAnalysis(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromTicks(frameCount * TimeSpan.TicksPerSecond / 30), frameCount);
        return Task.FromResult<IProxyEncodeSession>(new FakeEncodeSession(analysis, (_, _, output, _, _) =>
        {
            if (transparent)
                throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "alpha");

            File.WriteAllBytes(output, new byte[64]);
            return Task.FromResult(64L);
        }));
    }

    SourceIdentity CreateSource()
    {
        var path = Path.Combine(root, "source.mp4");
        File.WriteAllBytes(path, new byte[100]);
        return SourceIdentity.Of(path)!.Value;
    }

    ProxyCacheEntry Register(SourceIdentity source, params int[] chunks)
    {
        var entry = cache.Register(new ProxyCacheEntry
        {
            SourcePath = source.Path,
            SourceLength = source.Length,
            SourceWriteTimeTicks = source.WriteTimeTicks,
            Scale = 50,
            ProxyWidth = 960,
            ProxyHeight = 540,
            DisplayLeft = -960f,
            DisplayTop = -540f,
            DisplayWidth = 1920f,
            DisplayHeight = 1080f,
            FrameRateNumerator = 30,
            FrameRateDenominator = 1,
            DurationTicks = frameCount * TimeSpan.TicksPerSecond / 30,
            FrameCount = frameCount,
            ChunkLength = ChunkLength,
        });
        foreach (var chunk in chunks)
        {
            var temporary = cache.CreateTemporaryPath();
            File.WriteAllBytes(temporary, new byte[64]);
            entry = cache.AddChunk(entry.Id, chunk, temporary)!;
        }

        return entry;
    }

    IVideoFileSource? Factory(IGraphicsDevicesAndContext target, string path)
    {
        openedPaths.Add(path);
        if (path.StartsWith(cache.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            if (refuseChunks)
                return null;

            var chunk = TestVideoSource.Solid(target, 960, 540, 30, 1, ChunkLength);
            chunkSources.Add(chunk);
            return chunk;
        }

        return refuseOriginal ? null : TestVideoSource.Solid(target, 1920, 1080, 30, 1, frameCount);
    }

    ProxyVideoSource CreateWrapper(SourceIdentity source, ProxyCacheEntry? entry, TestVideoSource? original)
        => new(context, source, original, entry, new ProxyChunkLoader(context, source, 50, entry, cache, queue, Factory), Factory, focus);

    static TimeSpan FrameTimeOf(int frame) => new FrameRate(30, 1).GetSampleTime(frame);

    async Task UpdateUntilAsync(ProxyVideoSource wrapper, TimeSpan time, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            wrapper.Update(time);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    async Task GenerateAsync(SourceIdentity source)
    {
        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();
    }

    [Fact]
    public void AWrapperNeedsAnOriginalOrAnEntry()
    {
        var source = CreateSource();

        Assert.Throws<ArgumentException>(() => CreateWrapper(source, null, null));
    }

    [Fact]
    public void WithoutAnEntryTheOriginalAnswersEverything()
    {
        var source = CreateSource();
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        using var wrapper = CreateWrapper(source, null, original);

        Assert.Equal(TimeSpan.FromSeconds(2), wrapper.Duration);
        Assert.Equal(original.GetFrameIndex(TimeSpan.FromSeconds(1.5)), wrapper.GetFrameIndex(TimeSpan.FromSeconds(1.5)));
        Assert.NotNull(wrapper.Output);

        wrapper.Update(TimeSpan.FromSeconds(1));

        Assert.False(wrapper.IsProxy);
        Assert.Equal(1, original.UpdateCount);
        Assert.Equal(TimeSpan.FromSeconds(1), original.LastUpdateTime);
        Assert.Equal(30, focus.Get(source));
    }

    [Fact]
    public void AnEntryAnswersTheDurationAndTheFrameIndexWithoutTheOriginal()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        using var wrapper = CreateWrapper(source, entry, null);

        Assert.Equal(TimeSpan.FromSeconds(30), wrapper.Duration);
        Assert.Equal(FrameTime.TimeToFrame(TimeSpan.FromSeconds(7), 30, 1), wrapper.GetFrameIndex(TimeSpan.FromSeconds(7)));
        Assert.False(wrapper.HasOriginal);
        Assert.Empty(openedPaths);
    }

    [Fact]
    public void WithoutAnOriginalTheChunkIsOpenedSynchronously()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        using var wrapper = CreateWrapper(source, entry, null);

        wrapper.Update(FrameTimeOf(450));

        Assert.True(wrapper.IsProxy);
        Assert.Equal(1, wrapper.ShownChunk);
        Assert.False(wrapper.HasOriginal);
        Assert.EndsWith("000001.mp4", openedPaths[0]);
        Assert.Equal(FrameTimeOf(150), chunkSources[0].LastUpdateTime);
        Assert.Equal(450, focus.Get(source));
    }

    [Fact]
    public void AFrameInTheUpperHalfOfItsIntervalStaysInItsChunk()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        using var wrapper = CreateWrapper(source, entry, null);

        wrapper.Update(FrameTimeOf(299));

        Assert.Equal(0, wrapper.ShownChunk);
        Assert.Equal(FrameTimeOf(299), chunkSources[0].LastUpdateTime);
        Assert.Equal(299, focus.Get(source));
    }

    [Fact]
    public async Task WithAnOriginalTheChunkArrivesInTheBackground()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, frameCount);
        using var wrapper = CreateWrapper(source, entry, original);

        wrapper.Update(FrameTimeOf(450));
        Assert.False(wrapper.IsProxy);
        Assert.Equal(1, original.UpdateCount);

        await UpdateUntilAsync(wrapper, FrameTimeOf(450), () => wrapper.IsProxy);

        Assert.Equal(1, wrapper.ShownChunk);
        Assert.Contains(chunkSources, chunk => chunk.LastUpdateTime == FrameTimeOf(150));
    }

    [Fact]
    public async Task TheNextChunkIsPrefetchedAndFarChunksAreEvicted()
    {
        frameCount = 1500;
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2, 3, 4);
        using var wrapper = CreateWrapper(source, entry, null);

        wrapper.Update(FrameTimeOf(0));
        await UpdateUntilAsync(wrapper, FrameTimeOf(0), () => wrapper.OpenChunkCount == 2);
        Assert.Equal(0, wrapper.ShownChunk);

        wrapper.Update(FrameTimeOf(1350));
        await UpdateUntilAsync(wrapper, FrameTimeOf(1350), () => wrapper.OpenChunkCount == 1 && chunkSources.Count(chunk => chunk.IsDisposed) == 2);

        Assert.Equal(4, wrapper.ShownChunk);
    }

    [Fact]
    public async Task AMissingChunkFallsBackToTheOriginalUntilItIsGenerated()
    {
        var source = CreateSource();
        var entry = Register(source, 0);
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, frameCount);
        using var wrapper = CreateWrapper(source, entry, original);

        wrapper.Update(FrameTimeOf(450));
        Assert.False(wrapper.IsProxy);
        Assert.Equal(FrameTimeOf(450), original.LastUpdateTime);

        await GenerateAsync(source);
        await UpdateUntilAsync(wrapper, FrameTimeOf(450), () => wrapper.IsProxy);

        Assert.Equal(1, wrapper.ShownChunk);
    }

    [Fact]
    public async Task TheOriginalIsReleasedOnceEveryChunkExists()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1);
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, frameCount);
        using var wrapper = CreateWrapper(source, entry, original);
        await UpdateUntilAsync(wrapper, FrameTimeOf(0), () => wrapper.IsProxy);
        Assert.True(wrapper.HasOriginal);

        await GenerateAsync(source);
        wrapper.Update(FrameTimeOf(0));

        Assert.False(wrapper.HasOriginal);
        Assert.True(original.IsDisposed);
        Assert.True(wrapper.IsProxy);
    }

    [Fact]
    public async Task ADiscardedEntryReturnsToTheOriginal()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1);
        using var wrapper = CreateWrapper(source, entry, null);
        wrapper.Update(FrameTimeOf(0));
        Assert.True(wrapper.IsProxy);
        await UpdateUntilAsync(wrapper, FrameTimeOf(0), () => wrapper.OpenChunkCount == 2);

        transparent = true;
        await GenerateAsync(source);
        wrapper.Update(FrameTimeOf(0));

        Assert.False(wrapper.IsProxy);
        Assert.True(wrapper.HasOriginal);
        Assert.Equal(0, wrapper.OpenChunkCount);
        Assert.All(chunkSources, chunk => Assert.True(chunk.IsDisposed));
        Assert.Equal(TimeSpan.FromSeconds(30), wrapper.Duration);
        Assert.True(cache.IsSkipped(source));
    }

    [Fact]
    public void AChunkThatCannotBeOpenedIsForgottenAndTheOriginalIsOpened()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        using var wrapper = CreateWrapper(source, entry, null);
        refuseChunks = true;

        wrapper.Update(FrameTimeOf(0));

        Assert.False(wrapper.IsProxy);
        Assert.True(wrapper.HasOriginal);
        Assert.DoesNotContain(0, cache.Get(entry.Id)!.Chunks);
    }

    [Fact]
    public void WhenNothingCanBeOpenedTheOutputIsEmptyButUsable()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        using var wrapper = CreateWrapper(source, entry, null);
        refuseChunks = true;
        refuseOriginal = true;

        wrapper.Update(FrameTimeOf(0));
        wrapper.Update(FrameTimeOf(1));

        Assert.False(wrapper.IsProxy);
        Assert.False(wrapper.HasOriginal);
        Assert.Equal(1, openedPaths.Count(path => path == source.Path));
    }

    [Fact]
    public async Task DisposingReleasesEverythingAndRefusesUpdates()
    {
        var source = CreateSource();
        var entry = Register(source, 0, 1, 2);
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, frameCount);
        var wrapper = CreateWrapper(source, entry, original);
        await UpdateUntilAsync(wrapper, FrameTimeOf(0), () => wrapper.IsProxy);

        wrapper.Dispose();
        wrapper.Dispose();

        Assert.True(original.IsDisposed);
        Assert.All(chunkSources, chunk => Assert.True(chunk.IsDisposed));
        Assert.Throws<ObjectDisposedException>(() => wrapper.Update(TimeSpan.Zero));
    }

    [Fact]
    public void AlignMapsTheProxyRectangleOntoTheDisplayRectangle()
    {
        var transform = OpenedChunk.Align(new DisplayBounds(-480f, -270f, 960f, 540f), Layout());

        Assert.Equal(new Vector2(-960f, -540f), Vector2.Transform(new Vector2(-480f, -270f), transform));
        Assert.Equal(new Vector2(960f, 540f), Vector2.Transform(new Vector2(480f, 270f), transform));
        Assert.Equal(new Vector2(0f, 0f), Vector2.Transform(new Vector2(0f, 0f), transform));
    }

    [Fact]
    public void AlignHandlesAnOffCentreProxyAndUnevenScales()
    {
        var transform = OpenedChunk.Align(new DisplayBounds(-160f, -90f, 320f, 180f), Layout(-641f, -361f, 1282f, 722f));

        var topLeft = Vector2.Transform(new Vector2(-160f, -90f), transform);
        var bottomRight = Vector2.Transform(new Vector2(160f, 90f), transform);
        Assert.Equal(-641f, topLeft.X, 3);
        Assert.Equal(-361f, topLeft.Y, 3);
        Assert.Equal(641f, bottomRight.X, 3);
        Assert.Equal(361f, bottomRight.Y, 3);
    }

    [Fact]
    public void OpenReturnsNullWhenNoPluginOpensTheChunk()
        => Assert.Null(OpenedChunk.Open(context, "chunk.mp4", Layout(), static (_, _) => null));

    [Fact]
    public void OpenRejectsAnEntryWithAnInvalidRateBeforeOpeningAnything()
    {
        var entry = Layout();
        entry.FrameRateDenominator = 0;
        var created = false;

        var opened = OpenedChunk.Open(context, "chunk.mp4", entry, (target, _) =>
        {
            created = true;
            return TestVideoSource.Solid(target, 960, 540, 30, 1, 10);
        });

        Assert.Null(opened);
        Assert.False(created);
    }

    [Fact]
    public void OpenRejectsAnEntryWithoutADisplaySize()
    {
        var created = false;

        var opened = OpenedChunk.Open(context, "chunk.mp4", Layout(width: 0f), (target, _) =>
        {
            created = true;
            return TestVideoSource.Solid(target, 960, 540, 30, 1, 10);
        });

        Assert.Null(opened);
        Assert.False(created);
    }

    [Fact]
    public void OpenMeasuresTheChunkAndBuildsTheAlignment()
    {
        TestVideoSource? created = null;

        var opened = OpenedChunk.Open(context, "chunk.mp4", Layout(), (target, _) => created = TestVideoSource.Solid(target, 960, 540, 30000, 1001, 300));

        Assert.NotNull(opened);
        Assert.Same(created, opened.Source);
        Assert.Equal(1, created!.UpdateCount);
        Assert.Equal(new Vector2(-960f, -540f), Vector2.Transform(new Vector2(-480f, -270f), opened.Transform));
        Assert.Equal(new Vector2(960f, 540f), Vector2.Transform(new Vector2(480f, 270f), opened.Transform));

        opened.Dispose();

        Assert.True(created.IsDisposed);
    }

    [Fact]
    public void OpenDisposesTheSourceWhenTheFactoryProductThrows()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => OpenedChunk.Open(context, "chunk.mp4", Layout(), (_, _) => new ThrowingVideoSource()));

        Assert.Equal("update", thrown.Message);
        Assert.True(ThrowingVideoSource.LastDisposed);
    }

    static ProxyCacheEntry Layout(float left = -960f, float top = -540f, float width = 1920f, float height = 1080f) => new()
    {
        DisplayLeft = left,
        DisplayTop = top,
        DisplayWidth = width,
        DisplayHeight = height,
        FrameRateNumerator = 30000,
        FrameRateDenominator = 1001,
        DurationTicks = TimeSpan.FromSeconds(10).Ticks,
        FrameCount = 300,
        ChunkLength = 100,
    };

    sealed class ThrowingVideoSource : IVideoFileSource
    {
        public static bool LastDisposed { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public Vortice.Direct2D1.ID2D1Image Output => throw new InvalidOperationException("output");

        public void Update(TimeSpan time) => throw new InvalidOperationException("update");

        public int GetFrameIndex(TimeSpan time) => 0;

        public void Dispose() => LastDisposed = true;
    }
}
