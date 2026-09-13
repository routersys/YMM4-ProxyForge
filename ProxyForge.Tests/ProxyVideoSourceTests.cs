using System.Numerics;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

public sealed class ProxyVideoSourceTests : IDisposable
{
    readonly GraphicsDevices devices = new();
    readonly IGraphicsDevicesAndContext context;

    public ProxyVideoSourceTests() => context = devices.CreateContext();

    public void Dispose()
    {
        context.Dispose();
        devices.Dispose();
    }

    sealed class Loader : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    static ProxyCacheEntry Entry(float left = -960f, float top = -540f, float width = 1920f, float height = 1080f) => new()
    {
        DisplayLeft = left,
        DisplayTop = top,
        DisplayWidth = width,
        DisplayHeight = height,
        FrameRateNumerator = 30000,
        FrameRateDenominator = 1001,
        DurationTicks = TimeSpan.FromSeconds(10).Ticks,
    };

    ProxyUpgrade CreateUpgrade(out TestVideoSource proxySource)
    {
        var upgradeContext = devices.CreateContext();
        proxySource = TestVideoSource.Solid(upgradeContext, 960, 540, 30000, 1001, 300);
        var opened = new OpenedProxy(proxySource, Matrix3x2.CreateScale(2f), new FrameRate(30000, 1001));
        return new ProxyUpgrade(upgradeContext, opened);
    }

    [Fact]
    public void AnOriginalBackedSourceDelegatesToTheOriginal()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        using var source = ProxyVideoSource.FromOriginal(context, original);

        Assert.Equal(TimeSpan.FromSeconds(2), source.Duration);
        Assert.Equal(original.GetFrameIndex(TimeSpan.FromSeconds(1.5)), source.GetFrameIndex(TimeSpan.FromSeconds(1.5)));
        Assert.False(source.IsProxy);
        Assert.NotNull(source.Output);

        source.Update(TimeSpan.FromSeconds(1));

        Assert.Equal(1, original.UpdateCount);
        Assert.Equal(TimeSpan.FromSeconds(1), original.LastUpdateTime);
    }

    [Fact]
    public void AProxyBackedSourceReportsTheStoredDurationAndRate()
    {
        var proxy = TestVideoSource.Solid(context, 960, 540, 30000, 1001, 300);
        var opened = new OpenedProxy(proxy, Matrix3x2.CreateScale(2f), new FrameRate(30000, 1001));
        using var source = ProxyVideoSource.FromProxy(context, opened, TimeSpan.FromSeconds(10.3));

        Assert.True(source.IsProxy);
        Assert.Equal(TimeSpan.FromSeconds(10.3), source.Duration);
        Assert.Equal(FrameTime.TimeToFrame(TimeSpan.FromSeconds(7), 30000, 1001), source.GetFrameIndex(TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void AHandedOverProxyIsAppliedOnTheNextUpdate()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        var source = ProxyVideoSource.FromOriginal(context, original);
        var loader = new Loader();
        source.AttachLoader(loader);
        var upgrade = CreateUpgrade(out var proxy);
        var output = source.Output;

        Assert.True(source.TryHandOver(upgrade));
        Assert.False(source.IsProxy);
        Assert.False(original.IsDisposed);

        source.Update(TimeSpan.FromSeconds(1));

        Assert.True(source.IsProxy);
        Assert.True(original.IsDisposed);
        Assert.True(loader.IsDisposed);
        Assert.Equal(1, proxy.UpdateCount);
        Assert.Equal(TimeSpan.FromSeconds(1), proxy.LastUpdateTime);
        Assert.Same(output, source.Output);
        Assert.Equal(TimeSpan.FromSeconds(2), source.Duration);
        Assert.Equal(FrameTime.TimeToFrame(TimeSpan.FromSeconds(1.5), 30000, 1001), source.GetFrameIndex(TimeSpan.FromSeconds(1.5)));

        source.Dispose();

        Assert.True(proxy.IsDisposed);
    }

    [Fact]
    public void ASecondHandOverIsRefusedWhileOneIsPending()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        using var source = ProxyVideoSource.FromOriginal(context, original);
        var first = CreateUpgrade(out _);
        var second = CreateUpgrade(out var secondProxy);

        Assert.True(source.TryHandOver(first));
        Assert.False(source.TryHandOver(second));

        second.Dispose();
        Assert.True(secondProxy.IsDisposed);
    }

    [Fact]
    public void AHandOverAfterTheSwapIsRefused()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        using var source = ProxyVideoSource.FromOriginal(context, original);
        source.TryHandOver(CreateUpgrade(out _));
        source.Update(TimeSpan.Zero);

        Assert.False(source.TryHandOver(CreateUpgrade(out _)));
    }

    [Fact]
    public void DisposingDropsAPendingProxyAndTheLoader()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        var source = ProxyVideoSource.FromOriginal(context, original);
        var loader = new Loader();
        source.AttachLoader(loader);
        var upgrade = CreateUpgrade(out var proxy);
        source.TryHandOver(upgrade);

        source.Dispose();

        Assert.True(original.IsDisposed);
        Assert.True(proxy.IsDisposed);
        Assert.True(loader.IsDisposed);
        Assert.False(source.TryHandOver(CreateUpgrade(out _)));
        Assert.Throws<ObjectDisposedException>(() => source.Update(TimeSpan.Zero));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        var source = ProxyVideoSource.FromOriginal(context, original);

        source.Dispose();
        source.Dispose();

        Assert.True(original.IsDisposed);
    }

    [Fact]
    public void ALoaderAttachedAfterDisposeIsDisposedAtOnce()
    {
        var original = TestVideoSource.Solid(context, 1920, 1080, 30, 1, 60);
        var source = ProxyVideoSource.FromOriginal(context, original);
        source.Dispose();
        var loader = new Loader();

        source.AttachLoader(loader);

        Assert.True(loader.IsDisposed);
    }

    [Fact]
    public void AlignMapsTheProxyRectangleOntoTheDisplayRectangle()
    {
        var transform = OpenedProxy.Align(new DisplayBounds(-480f, -270f, 960f, 540f), Entry());

        Assert.Equal(new Vector2(-960f, -540f), Vector2.Transform(new Vector2(-480f, -270f), transform));
        Assert.Equal(new Vector2(960f, 540f), Vector2.Transform(new Vector2(480f, 270f), transform));
        Assert.Equal(new Vector2(0f, 0f), Vector2.Transform(new Vector2(0f, 0f), transform));
    }

    [Fact]
    public void AlignHandlesAnOffCentreProxyAndUnevenScales()
    {
        var transform = OpenedProxy.Align(new DisplayBounds(-160f, -90f, 320f, 180f), Entry(-641f, -361f, 1282f, 722f));

        var topLeft = Vector2.Transform(new Vector2(-160f, -90f), transform);
        var bottomRight = Vector2.Transform(new Vector2(160f, 90f), transform);
        Assert.Equal(-641f, topLeft.X, 3);
        Assert.Equal(-361f, topLeft.Y, 3);
        Assert.Equal(641f, bottomRight.X, 3);
        Assert.Equal(361f, bottomRight.Y, 3);
    }

    [Fact]
    public void OpenReturnsNullWhenNoPluginOpensTheProxy()
        => Assert.Null(OpenedProxy.Open(context, "proxy.mp4", Entry(), static (_, _) => null));

    [Fact]
    public void OpenRejectsAnEntryWithAnInvalidRate()
    {
        TestVideoSource? created = null;
        var entry = Entry();
        entry.FrameRateDenominator = 0;

        var opened = OpenedProxy.Open(context, "proxy.mp4", entry, (devices, _) => created = TestVideoSource.Solid(devices, 960, 540, 30, 1, 10));

        Assert.Null(opened);
        Assert.True(created!.IsDisposed);
    }

    [Fact]
    public void OpenRejectsAnEntryWithoutADisplaySize()
    {
        TestVideoSource? created = null;

        var opened = OpenedProxy.Open(context, "proxy.mp4", Entry(width: 0f), (devices, _) => created = TestVideoSource.Solid(devices, 960, 540, 30, 1, 10));

        Assert.Null(opened);
        Assert.True(created!.IsDisposed);
    }

    [Fact]
    public void OpenMeasuresTheProxyAndBuildsTheAlignment()
    {
        TestVideoSource? created = null;

        var opened = OpenedProxy.Open(context, "proxy.mp4", Entry(), (devices, _) => created = TestVideoSource.Solid(devices, 960, 540, 30000, 1001, 300));

        Assert.NotNull(opened);
        Assert.Same(created, opened.Value.Source);
        Assert.Equal(new FrameRate(30000, 1001), opened.Value.FrameRate);
        Assert.Equal(1, created!.UpdateCount);
        Assert.Equal(new Vector2(-960f, -540f), Vector2.Transform(new Vector2(-480f, -270f), opened.Value.Transform));
        Assert.Equal(new Vector2(960f, 540f), Vector2.Transform(new Vector2(480f, 270f), opened.Value.Transform));
    }

    [Fact]
    public void OpenDisposesTheSourceWhenTheFactoryProductThrows()
    {
        var entry = Entry();
        var thrown = Assert.Throws<InvalidOperationException>(() => OpenedProxy.Open(context, "proxy.mp4", entry, (devices, _) => new ThrowingVideoSource()));

        Assert.Equal("update", thrown.Message);
        Assert.True(ThrowingVideoSource.LastDisposed);
    }

    sealed class ThrowingVideoSource : YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource
    {
        public static bool LastDisposed { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public Vortice.Direct2D1.ID2D1Image Output => throw new InvalidOperationException("output");

        public void Update(TimeSpan time) => throw new InvalidOperationException("update");

        public int GetFrameIndex(TimeSpan time) => 0;

        public void Dispose() => LastDisposed = true;
    }
}
