using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

public sealed class ProxyUpgradeLoaderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly List<TestVideoSource> created = [];
    bool refuse;

    public ProxyUpgradeLoaderTests()
    {
        Directory.CreateDirectory(root);
        context = devices.CreateContext();
        cache = new ProxyCache(Path.Combine(root, "cache"));
        queue = new ProxyGenerationQueue(cache, Encode, () => new ProxyEncodeOptions(50, 30, false), () => ExportPhase.Idle, _ => { })
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
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

    static Task<ProxyEncodeResult> Encode(ProxyEncodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        File.WriteAllBytes(request.OutputPath, new byte[64]);
        return Task.FromResult(new ProxyEncodeResult(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromSeconds(2), 60, 64));
    }

    YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource? Factory(IGraphicsDevicesAndContext devices, string path)
    {
        if (refuse)
            return null;

        var source = TestVideoSource.Solid(devices, 960, 540, 30, 1, 60);
        created.Add(source);
        return source;
    }

    SourceIdentity CreateSource(string name = "source.mp4")
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[100]);
        return SourceIdentity.Of(path)!.Value;
    }

    ProxyVideoSource CreateWrapper(out TestVideoSource original)
    {
        original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        return ProxyVideoSource.FromOriginal(context, original);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task AnExistingProxyIsLoadedAtOnce()
    {
        var source = CreateSource();
        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        var wrapper = CreateWrapper(out var original);

        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory));
        await WaitUntilAsync(() => created.Count == 1);
        await WaitUntilAsync(() =>
        {
            wrapper.Update(TimeSpan.Zero);
            return wrapper.IsProxy;
        });

        Assert.True(original.IsDisposed);
        wrapper.Dispose();
        Assert.True(created[0].IsDisposed);
    }

    [Fact]
    public async Task AProxyGeneratedLaterIsLoadedWhenTheQueueAnnouncesIt()
    {
        var source = CreateSource();
        var wrapper = CreateWrapper(out _);
        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(created);

        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        await WaitUntilAsync(() =>
        {
            wrapper.Update(TimeSpan.Zero);
            return wrapper.IsProxy;
        });

        wrapper.Dispose();
    }

    [Fact]
    public async Task AnAnnouncementForAnotherScaleIsIgnored()
    {
        var source = CreateSource();
        var wrapper = CreateWrapper(out _);
        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory));

        queue.TryEnqueue(source, 25);
        await queue.WhenIdleAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wrapper.Update(TimeSpan.Zero);

        Assert.False(wrapper.IsProxy);
        Assert.Empty(created);
        wrapper.Dispose();
    }

    [Fact]
    public async Task AnAnnouncementForAnotherFileIsIgnored()
    {
        var source = CreateSource();
        var other = CreateSource("other.mp4");
        var wrapper = CreateWrapper(out _);
        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory));

        queue.TryEnqueue(other, 50);
        await queue.WhenIdleAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wrapper.Update(TimeSpan.Zero);

        Assert.False(wrapper.IsProxy);
        Assert.Empty(created);
        wrapper.Dispose();
    }

    [Fact]
    public async Task AProxyNobodyCanOpenIsRemovedFromTheCache()
    {
        var source = CreateSource();
        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        refuse = true;
        var wrapper = CreateWrapper(out _);

        wrapper.AttachLoader(new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory));
        await WaitUntilAsync(() => cache.Count == 0);
        wrapper.Update(TimeSpan.Zero);

        Assert.False(wrapper.IsProxy);
        wrapper.Dispose();
    }

    [Fact]
    public async Task ADisposedWrapperNeverReceivesTheProxy()
    {
        var source = CreateSource();
        var wrapper = CreateWrapper(out _);
        var loader = new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory);
        wrapper.AttachLoader(loader);
        wrapper.Dispose();

        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(created);
    }

    [Fact]
    public async Task ALoaderDisposedWhileLoadingDisposesWhatItOpened()
    {
        var source = CreateSource();
        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        var wrapper = CreateWrapper(out _);
        var loader = new ProxyUpgradeLoader(wrapper, devices, source, 50, cache, queue, Factory);
        wrapper.Dispose();

        await WaitUntilAsync(() => created.Count == 1 && created[0].IsDisposed);
        loader.Dispose();
    }
}
