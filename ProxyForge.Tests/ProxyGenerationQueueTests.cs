using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;

namespace ProxyForge.Tests;

public sealed class ProxyGenerationQueueTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly ProxyCache cache;
    readonly List<Exception> reported = [];
    ExportPhase phase = ExportPhase.Idle;
    ProxyEncodeOptions options = new(50, 30, false);

    public ProxyGenerationQueueTests()
    {
        Directory.CreateDirectory(root);
        cache = new ProxyCache(Path.Combine(root, "cache"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    SourceIdentity CreateSource(string name = "source.mp4")
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[100]);
        return SourceIdentity.Of(path)!.Value;
    }

    ProxyGenerationQueue CreateQueue(ProxyEncodeFunction encode)
        => new(cache, encode, () => options, () => phase, reported.Add)
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
            CancelledRetention = TimeSpan.FromMilliseconds(50),
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };

    static ProxyEncodeResult WriteProxy(ProxyEncodeRequest request, int length = 100)
    {
        File.WriteAllBytes(request.OutputPath, new byte[length]);
        return new ProxyEncodeResult(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30000, 1001), TimeSpan.FromSeconds(10), 300, length);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    static async Task<ProxyGenerationItem> EnqueueAsync(ProxyGenerationQueue queue, SourceIdentity source, int scale)
    {
        Assert.True(queue.TryEnqueue(source, scale));
        await WaitUntilAsync(() => queue.Items.Count == 1);
        return queue.Items[0];
    }

    [Fact]
    public async Task AJobEncodesIntoTheCacheAndAnnouncesTheEntry()
    {
        var source = CreateSource();
        ProxyEncodeRequest? seen = null;
        var queue = CreateQueue((request, progress, _) =>
        {
            seen = request;
            progress?.Report(0.5d);
            return Task.FromResult(WriteProxy(request));
        });
        var announced = new List<(SourceIdentity Source, int Scale, ProxyCacheEntry Entry)>();
        queue.Completed += (announcedSource, scale, entry) => announced.Add((announcedSource, scale, entry));
        options = new ProxyEncodeOptions(80, 15, true);

        Assert.True(queue.TryEnqueue(source, 50));
        Assert.True(queue.IsPending(source, 50));
        await queue.WhenIdleAsync();

        Assert.NotNull(seen);
        Assert.Equal(source.Path, seen.Value.SourcePath);
        Assert.Equal(50, seen.Value.Scale);
        Assert.Equal(80, seen.Value.BitrateScale);
        Assert.Equal(15, seen.Value.KeyFrameInterval);
        Assert.True(seen.Value.UsesHardwareEncoder);
        Assert.Equal(cache.DirectoryPath, seen.Value.WorkingDirectory);
        Assert.StartsWith(cache.DirectoryPath, seen.Value.OutputPath);
        Assert.EndsWith(".tmp", seen.Value.OutputPath);
        Assert.False(File.Exists(seen.Value.OutputPath));

        var found = cache.Find(source, 50);
        Assert.NotNull(found);
        Assert.Equal(960, found.ProxyWidth);
        Assert.Equal(30000, found.FrameRateNumerator);
        Assert.Equal(300, found.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, found.DurationTicks);
        var single = Assert.Single(announced);
        Assert.Equal(source, single.Source);
        Assert.Equal(50, single.Scale);
        Assert.Equal(found.Id, single.Entry.Id);
        Assert.False(queue.IsPending(source, 50));
        Assert.False(queue.HasFailed(source, 50));
    }

    [Fact]
    public async Task TheItemWalksThroughItsStatuses()
    {
        var source = CreateSource();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var queue = CreateQueue(async (request, progress, _) =>
        {
            started.SetResult();
            await release.Task;
            progress?.Report(0.25d);
            return WriteProxy(request);
        });

        queue.TryEnqueue(source, 50);
        await started.Task;
        var item = Assert.Single(queue.Items);
        Assert.Equal(source.Path, item.SourcePath);
        Assert.Equal("source.mp4", item.FileName);
        Assert.Equal(50, item.Scale);
        Assert.Equal(ProxyGenerationStatus.Generating, item.Status);
        Assert.Equal(0d, item.Progress);

        release.SetResult();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Completed, item.Status);
        Assert.Equal(1d, item.Progress);
        await WaitUntilAsync(() => queue.Items.Count == 0);
    }

    [Fact]
    public async Task ProgressReportsReachTheItem()
    {
        var source = CreateSource();
        var release = new TaskCompletionSource();
        var reported = new TaskCompletionSource();
        var queue = CreateQueue(async (request, progress, _) =>
        {
            progress?.Report(0.42d);
            reported.SetResult();
            await release.Task;
            return WriteProxy(request);
        });

        queue.TryEnqueue(source, 50);
        await reported.Task;
        await WaitUntilAsync(() => queue.Items.Count == 1 && queue.Items[0].Progress == 0.42d);

        release.SetResult();
        await queue.WhenIdleAsync();
    }

    [Fact]
    public async Task ThePendingJobIsNotEnqueuedTwice()
    {
        var source = CreateSource();
        var release = new TaskCompletionSource();
        var queue = CreateQueue(async (request, _, _) =>
        {
            await release.Task;
            return WriteProxy(request);
        });

        Assert.True(queue.TryEnqueue(source, 50));
        Assert.False(queue.TryEnqueue(source, 50));
        Assert.False(queue.TryEnqueue(source with { Path = source.Path.ToUpperInvariant() }, 50));
        Assert.True(queue.TryEnqueue(source, 25));

        release.SetResult();
        await queue.WhenIdleAsync();
    }

    [Fact]
    public async Task OnlyOneJobEncodesAtATime()
    {
        var first = CreateSource("a.mp4");
        var second = CreateSource("b.mp4");
        var running = 0;
        var overlap = false;
        var queue = CreateQueue(async (request, _, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlap = true;
            await Task.Delay(50, CancellationToken.None);
            Interlocked.Decrement(ref running);
            return WriteProxy(request);
        });

        queue.TryEnqueue(first, 50);
        queue.TryEnqueue(second, 50);
        await queue.WhenIdleAsync();

        Assert.False(overlap);
        Assert.NotNull(cache.Find(first, 50));
        Assert.NotNull(cache.Find(second, 50));
    }

    [Fact]
    public async Task AJobWaitsUntilTheExportHasEnded()
    {
        var source = CreateSource();
        var encoded = false;
        var queue = CreateQueue((request, _, _) =>
        {
            encoded = true;
            return Task.FromResult(WriteProxy(request));
        });
        phase = ExportPhase.Preparing;

        queue.TryEnqueue(source, 50);
        await Task.Delay(ProxyGenerationQueue.ExportPollInterval * 3, TestContext.Current.CancellationToken);
        Assert.False(encoded);
        Assert.Equal(ProxyGenerationStatus.Waiting, Assert.Single(queue.Items).Status);

        phase = ExportPhase.Idle;
        await queue.WhenIdleAsync();

        Assert.True(encoded);
    }

    [Fact]
    public async Task AFailedJobIsRememberedUntilForgotten()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.True(queue.HasFailed(source, 50));
        Assert.False(queue.HasFailed(source, 25));
        Assert.False(queue.TryEnqueue(source, 50));
        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.Equal(ProxyEncodeFailure.FFmpegFailed, item.Failure);
        Assert.Null(cache.Find(source, 50));

        queue.ForgetFailures();

        Assert.False(queue.HasFailed(source, 50));
        await WaitUntilAsync(() => queue.Items.Count == 0);
        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();
    }

    [Fact]
    public async Task ATransparentSourceIsSkippedInTheCache()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "alpha"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.True(cache.IsSkipped(source));
        Assert.Equal(ProxyEncodeFailure.Transparent, item.Failure);
    }

    [Fact]
    public async Task AnUnexpectedExceptionFailsTheJobWithoutAReason()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _) => throw new InvalidOperationException("unexpected"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.Null(item.Failure);
        Assert.True(queue.HasFailed(source, 50));
        Assert.False(cache.IsSkipped(source));
        Assert.IsType<InvalidOperationException>(Assert.Single(reported));
    }

    [Fact]
    public async Task CancelAllStopsARunningJob()
    {
        var source = CreateSource();
        var started = new TaskCompletionSource();
        var queue = CreateQueue(async (request, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return WriteProxy(request);
        });

        var item = await EnqueueAsync(queue, source, 50);
        await started.Task;
        queue.CancelAll();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Cancelled, item.Status);
        Assert.False(queue.HasFailed(source, 50));
        Assert.Null(cache.Find(source, 50));
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CancelAllStopsAWaitingJob()
    {
        var source = CreateSource();
        var queue = CreateQueue((request, _, _) => Task.FromResult(WriteProxy(request)));
        phase = ExportPhase.Exporting;

        var item = await EnqueueAsync(queue, source, 50);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        queue.CancelAll();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Cancelled, item.Status);
        Assert.Null(cache.Find(source, 50));
    }

    [Fact]
    public async Task ASourceThatChangedDuringTheEncodeIsRejected()
    {
        var source = CreateSource();
        var queue = CreateQueue((request, _, _) =>
        {
            File.WriteAllBytes(source.Path, new byte[200]);
            return Task.FromResult(WriteProxy(request));
        });

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyEncodeFailure.SourceChanged, item.Failure);
        Assert.Null(cache.Find(source, 50));
        Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task AJobWhoseTemporaryFileVanishedFailsWithoutAReason()
    {
        var source = CreateSource();
        var queue = CreateQueue((request, _, _) => Task.FromResult(new ProxyEncodeResult(new ProxyGeometry(2, 2), new DisplayBounds(0f, 0f, 4f, 4f), new FrameRate(30, 1), TimeSpan.FromSeconds(1), 30, 0)));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.True(queue.HasFailed(source, 50));
        Assert.IsType<FileNotFoundException>(Assert.Single(reported));
    }

    [Fact]
    public async Task ExpectedFailuresAreNotReported()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));

        await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Empty(reported);
    }
}
