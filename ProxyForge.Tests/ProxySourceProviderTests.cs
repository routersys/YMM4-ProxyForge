using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

[Collection("Direct2D")]
public sealed class ProxySourceProviderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly ProxyForgeSettings settings = new() { MinimumFileSizeMegabytes = 1, Scale = 50 };
    readonly List<string> opened = [];
    bool exporting;
    bool refuseAll;
    bool throwForProxies;
    int encoded;

    public ProxySourceProviderTests()
    {
        Directory.CreateDirectory(root);
        context = devices.CreateContext();
        cache = new ProxyCache(Path.Combine(root, "cache"));
        queue = new ProxyGenerationQueue(cache, Encode, () => new ProxyEncodeOptions(50, 30, false), () => ExportPhase.Idle, _ => { })
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
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

    Task<ProxyEncodeResult> Encode(ProxyEncodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        encoded++;
        File.WriteAllBytes(request.OutputPath, new byte[64]);
        return Task.FromResult(new ProxyEncodeResult(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromSeconds(2), 60, 64));
    }

    YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource? Factory(IGraphicsDevicesAndContext devices, string path)
    {
        opened.Add(path);
        if (refuseAll)
            return null;
        if (path.StartsWith(cache.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            if (throwForProxies)
                throw new InvalidOperationException("broken proxy");
            return TestVideoSource.Solid(devices, 960, 540, 30, 1, 60);
        }

        return TestVideoSource.Solid(devices, 1920, 1080, 30, 1, 60);
    }

    ProxySourceProvider CreateProvider() => new(cache, queue, () => exporting, () => settings, Factory);

    string CreateFile(string name = "source.mp4", int megabytes = 2)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[megabytes * 1024 * 1024]);
        return path;
    }

    SourceIdentity IdentityOf(string path) => SourceIdentity.Of(path)!.Value;

    void Seed(string path)
    {
        var temporary = cache.CreateTemporaryPath();
        File.WriteAllBytes(temporary, new byte[64]);
        var identity = IdentityOf(path);
        cache.Add(new ProxyCacheEntry
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
            DurationTicks = TimeSpan.FromSeconds(2.5).Ticks,
            FrameCount = 75,
        }, temporary);
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
        Seed(path);

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
    public void ACachedProxyIsOpenedWithTheStoredDuration()
    {
        var path = CreateFile();
        Seed(path);

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.True(proxy.IsProxy);
        Assert.Equal(TimeSpan.FromSeconds(2.5), proxy.Duration);
        Assert.EndsWith(ProxyCache.ProxyExtension, Assert.Single(opened));
        Assert.Equal(0, encoded);
    }

    [Fact]
    public async Task ACachedProxyThatCannotBeOpenedIsDroppedAndTheOriginalIsUsed()
    {
        var path = CreateFile();
        Seed(path);
        throwForProxies = true;

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(proxy.IsProxy);
        Assert.Equal(0, cache.Count);
        Assert.Equal(2, opened.Count);
        await queue.WhenIdleAsync();
        Assert.Equal(1, encoded);
    }

    [Fact]
    public async Task ANewFileIsWrappedAndQueued()
    {
        var path = CreateFile();

        using var source = CreateProvider().Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(proxy.IsProxy);
        Assert.Equal(TimeSpan.FromSeconds(2), proxy.Duration);
        Assert.Equal(path, Assert.Single(opened));
        await queue.WhenIdleAsync();
        Assert.Equal(1, encoded);
        Assert.NotNull(cache.Find(IdentityOf(path), 50));
    }

    [Fact]
    public async Task AFailedFileYieldsNothingAfterwards()
    {
        var path = CreateFile();
        refuseAll = true;
        var failing = new ProxyGenerationQueue(cache, (_, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"), () => new ProxyEncodeOptions(50, 30, false), () => ExportPhase.Idle, _ => { })
        {
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };
        failing.TryEnqueue(IdentityOf(path), 50);
        await failing.WhenIdleAsync();
        refuseAll = false;
        var provider = new ProxySourceProvider(cache, failing, () => exporting, () => settings, Factory);

        Assert.Null(provider.Create(context, path));
        Assert.Empty(opened);
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
    public void WithoutAutomaticGenerationACachedProxyIsStillUsed()
    {
        settings.GeneratesAutomatically = false;
        var path = CreateFile();
        Seed(path);

        using var source = CreateProvider().Create(context, path);

        Assert.True(Assert.IsType<ProxyVideoSource>(source).IsProxy);
    }

    [Fact]
    public async Task WithoutAutomaticGenerationAPendingFileIsStillWrapped()
    {
        var path = CreateFile();
        var identity = IdentityOf(path);
        var release = new TaskCompletionSource();
        var pending = new ProxyGenerationQueue(cache, async (request, _, _) =>
        {
            await release.Task;
            return await Encode(request, null, CancellationToken.None);
        }, () => new ProxyEncodeOptions(50, 30, false), () => ExportPhase.Idle, _ => { })
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
        };
        pending.TryEnqueue(identity, 50);
        settings.GeneratesAutomatically = false;
        var provider = new ProxySourceProvider(cache, pending, () => exporting, () => settings, Factory);

        using var source = provider.Create(context, path);

        var proxy = Assert.IsType<ProxyVideoSource>(source);
        Assert.False(proxy.IsProxy);
        release.SetResult();
        await pending.WhenIdleAsync();
        for (var attempt = 0; attempt < 500 && !proxy.IsProxy; attempt++)
        {
            proxy.Update(TimeSpan.Zero);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(proxy.IsProxy);
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
    public async Task TheWrapperSwitchesToTheProxyOnceItIsGenerated()
    {
        var path = CreateFile();

        using var source = CreateProvider().Create(context, path);
        var proxy = Assert.IsType<ProxyVideoSource>(source);
        await queue.WhenIdleAsync();
        for (var attempt = 0; attempt < 500 && !proxy.IsProxy; attempt++)
        {
            proxy.Update(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(proxy.IsProxy);
        Assert.Equal(TimeSpan.FromSeconds(2), proxy.Duration);
        Assert.Equal(2, opened.Count);
        Assert.EndsWith(ProxyCache.ProxyExtension, opened[1]);
    }
}
