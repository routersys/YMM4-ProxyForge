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
    readonly SourceFocus focus = new();
    ExportPhase phase = ExportPhase.Idle;
    ProxyEncodeOptions options = new(50, 30, 10, false);

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

    ProxyGenerationQueue CreateQueue(ProxyEncodeSessionFactory open)
        => new(cache, open, () => options, () => phase, focus, reported.Add, TestUiThread.Post)
        {
            CompletedRetention = TimeSpan.FromMilliseconds(50),
            CancelledRetention = TimeSpan.FromMilliseconds(50),
            FailedRetention = TimeSpan.FromMilliseconds(50),
        };

    ProxyGenerationQueue CreateQueue(Func<int, int, string, IProgress<double>?, CancellationToken, Task<long>> encode, int frameCount = 300, Action<ProxyEncodeRequest>? onOpen = null)
        => CreateQueue((request, _) =>
        {
            onOpen?.Invoke(request);
            return Task.FromResult<IProxyEncodeSession>(new FakeEncodeSession(Analysis(frameCount), encode));
        });

    static ProxyAnalysis Analysis(int frameCount = 300)
        => new(new ProxyGeometry(960, 540), new DisplayBounds(-960f, -540f, 1920f, 1080f), new FrameRate(30, 1), TimeSpan.FromTicks(frameCount * TimeSpan.TicksPerSecond / 30), frameCount);

    static Task<long> WriteChunk(string outputPath, int length = 100)
    {
        File.WriteAllBytes(outputPath, new byte[length]);
        return Task.FromResult((long)length);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !TestUiThread.Read(condition); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(TestUiThread.Read(condition));
    }

    static async Task<ProxyGenerationItem> EnqueueAsync(ProxyGenerationQueue queue, SourceIdentity source, int scale)
    {
        Assert.True(queue.TryEnqueue(source, scale));
        await WaitUntilAsync(() => queue.Items.Count == 1);
        return TestUiThread.Read(() => queue.Items[0]);
    }

    [Theory]
    [InlineData(30, 1, 10, 300)]
    [InlineData(30000, 1001, 10, 300)]
    [InlineData(24, 1, 1, 24)]
    [InlineData(1, 1000, 10, 1)]
    public void TheChunkLengthIsTheFrameRateTimesTheSeconds(int numerator, int denominator, int seconds, int expected)
        => Assert.Equal(expected, ProxyChunkPlan.LengthFor(new FrameRate(numerator, denominator), seconds));

    [Theory]
    [InlineData(new int[0], 4, null, 0)]
    [InlineData(new[] { 0 }, 4, null, 1)]
    [InlineData(new int[0], 4, 250, 2)]
    [InlineData(new[] { 2, 3 }, 4, 250, 0)]
    [InlineData(new[] { 0, 2, 3 }, 4, 250, 1)]
    [InlineData(new[] { 0, 1, 2, 3 }, 4, 250, null)]
    [InlineData(new int[0], 4, 100_000, 3)]
    [InlineData(new int[0], 0, 0, null)]
    public void TheNextChunkStartsAtTheFocusAndWrapsAround(int[] completed, int chunkCount, int? focusFrame, int? expected)
        => Assert.Equal(expected, ProxyChunkPlan.Next(completed, chunkCount, 100, focusFrame));

    [Fact]
    public async Task AJobEncodesEveryChunkIntoTheCacheAndAnnouncesEachOne()
    {
        var source = CreateSource();
        ProxyEncodeRequest? seen = null;
        var ranges = new List<(int First, int Count)>();
        var queue = CreateQueue((first, count, output, progress, _) =>
        {
            ranges.Add((first, count));
            progress?.Report(0.5d);
            return WriteChunk(output);
        }, 250, request => seen = request);
        var announced = new List<(SourceIdentity Source, int Scale, ProxyCacheEntry Entry)>();
        queue.ChunkCompleted += (announcedSource, scale, entry) => announced.Add((announcedSource, scale, entry.Clone()));
        options = new ProxyEncodeOptions(80, 15, 4, true);

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
        Assert.Equal([(0, 120), (120, 120), (240, 10)], ranges);

        var found = cache.Find(source, 50);
        Assert.NotNull(found);
        Assert.Equal(960, found.ProxyWidth);
        Assert.Equal(30, found.FrameRateNumerator);
        Assert.Equal(250, found.FrameCount);
        Assert.Equal(120, found.ChunkLength);
        Assert.True(found.IsComplete);
        Assert.Equal(300L, found.FileLength);
        Assert.Equal(3, announced.Count);
        Assert.All(announced, item => Assert.Equal((source, 50, found.Id), (item.Source, item.Scale, item.Entry.Id)));
        Assert.Equal([1, 2, 3], announced.Select(item => item.Entry.Chunks.Count));
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
        Assert.False(queue.IsPending(source, 50));
        Assert.False(queue.HasFailed(source, 50));
    }

    [Fact]
    public async Task TheItemWalksThroughItsStatusesAndCountsChunks()
    {
        var source = CreateSource();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var progressed = new List<double>();
        var queue = CreateQueue(async (_, _, output, progress, _) =>
        {
            started.TrySetResult();
            await release.Task;
            progress?.Report(0.5d);
            return await WriteChunk(output);
        });
        options = new ProxyEncodeOptions(50, 30, 5, false);

        queue.TryEnqueue(source, 50);
        await started.Task;
        var item = TestUiThread.Read(() => Assert.Single(queue.Items));
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProxyGenerationItem.Progress))
                progressed.Add(item.Progress);
        };
        Assert.Equal(source.Path, item.SourcePath);
        Assert.Equal("source.mp4", item.FileName);
        Assert.Equal(50, item.Scale);
        Assert.Equal(ProxyGenerationStatus.Generating, item.Status);
        Assert.Equal(0d, item.Progress);

        release.SetResult();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Completed, item.Status);
        Assert.Equal(1d, item.Progress);
        Assert.Equal(progressed.OrderBy(value => value), progressed);
        Assert.Contains(0.25d, progressed);
        Assert.Contains(0.5d, progressed);
        await WaitUntilAsync(() => queue.Items.Count == 0);
    }

    [Fact]
    public async Task ChunksStartAtTheFocusAndFinishTheRest()
    {
        var source = CreateSource();
        var ranges = new List<int>();
        var queue = CreateQueue((first, _, output, _, _) =>
        {
            ranges.Add(first);
            return WriteChunk(output);
        }, 1000);
        focus.Report(source, 650);

        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal([600, 900, 0, 300], ranges);
        Assert.True(cache.Find(source, 50)!.IsComplete);
    }

    [Fact]
    public async Task AJobResumesAPartialEntryAndSkipsExistingChunks()
    {
        var source = CreateSource();
        var ranges = new List<int>();
        var queue = CreateQueue((first, _, output, _, _) =>
        {
            ranges.Add(first);
            return WriteChunk(output);
        }, 1000);
        var registered = cache.Register(new ProxyCacheEntry
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
            DurationTicks = 1000 * TimeSpan.TicksPerSecond / 30,
            FrameCount = 1000,
            ChunkLength = 300,
        });
        var temporary = cache.CreateTemporaryPath();
        File.WriteAllBytes(temporary, new byte[10]);
        cache.AddChunk(registered.Id, 1, temporary);

        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal([0, 600, 900], ranges);
        var found = cache.Find(source, 50)!;
        Assert.Equal(registered.Id, found.Id);
        Assert.True(found.IsComplete);
    }

    [Fact]
    public async Task ACompleteEntryFinishesWithoutEncoding()
    {
        var source = CreateSource();
        var encoded = 0;
        var queue = CreateQueue((_, _, output, _, _) =>
        {
            encoded++;
            return WriteChunk(output);
        }, 100);
        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();
        Assert.Equal(1, encoded);
        await WaitUntilAsync(() => queue.Items.Count == 0);

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(1, encoded);
        Assert.Equal(ProxyGenerationStatus.Completed, item.Status);
        Assert.Equal(1d, item.Progress);
    }

    [Fact]
    public async Task ThePendingJobIsNotEnqueuedTwice()
    {
        var source = CreateSource();
        var release = new TaskCompletionSource();
        var queue = CreateQueue(async (_, _, output, _, _) =>
        {
            await release.Task;
            return await WriteChunk(output);
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
        var queue = CreateQueue(async (_, _, output, _, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlap = true;
            await Task.Delay(20, CancellationToken.None);
            Interlocked.Decrement(ref running);
            return await WriteChunk(output);
        });

        queue.TryEnqueue(first, 50);
        queue.TryEnqueue(second, 50);
        await queue.WhenIdleAsync();

        Assert.False(overlap);
        Assert.True(cache.Find(first, 50)!.IsComplete);
        Assert.True(cache.Find(second, 50)!.IsComplete);
    }

    [Fact]
    public async Task AJobWaitsUntilTheExportHasEnded()
    {
        var source = CreateSource();
        var encoded = false;
        var queue = CreateQueue((_, _, output, _, _) =>
        {
            encoded = true;
            return WriteChunk(output);
        });
        phase = ExportPhase.Preparing;

        queue.TryEnqueue(source, 50);
        await Task.Delay(ProxyGenerationQueue.ExportPollInterval * 3, TestContext.Current.CancellationToken);
        Assert.False(encoded);
        Assert.Equal(ProxyGenerationStatus.Waiting, TestUiThread.Read(() => Assert.Single(queue.Items).Status));

        phase = ExportPhase.Idle;
        await queue.WhenIdleAsync();

        Assert.True(encoded);
    }

    [Fact]
    public async Task AnExportThatStartsBetweenChunksPausesTheJob()
    {
        var source = CreateSource();
        var encoded = 0;
        var queue = CreateQueue((_, _, output, _, _) =>
        {
            if (++encoded == 1)
                phase = ExportPhase.Exporting;
            return WriteChunk(output);
        });
        options = new ProxyEncodeOptions(50, 30, 4, false);

        queue.TryEnqueue(source, 50);
        await Task.Delay(ProxyGenerationQueue.ExportPollInterval * 3, TestContext.Current.CancellationToken);
        Assert.Equal(1, encoded);

        phase = ExportPhase.Idle;
        await queue.WhenIdleAsync();

        Assert.Equal(3, encoded);
    }

    [Fact]
    public async Task AFailedJobIsRememberedUntilForgotten()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.True(queue.HasFailed(source, 50));
        Assert.False(queue.HasFailed(source, 25));
        Assert.False(queue.TryEnqueue(source, 50));
        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.Equal(ProxyEncodeFailure.FFmpegFailed, item.Failure);
        Assert.Empty(cache.Find(source, 50)!.Chunks);

        queue.ForgetFailures();

        Assert.False(queue.HasFailed(source, 50));
        await WaitUntilAsync(() => queue.Items.Count == 0);
        Assert.True(queue.TryEnqueue(source, 50));
        await queue.WhenIdleAsync();
    }

    [Fact]
    public async Task AFailureToOpenTheSourceIsReportedAsSuch()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _) => throw new ProxyEncodeException(ProxyEncodeFailure.SourceUnavailable, "no plugin"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyEncodeFailure.SourceUnavailable, item.Failure);
        Assert.Null(cache.Find(source, 50));
    }

    [Fact]
    public async Task ATransparentSourceIsSkippedAndItsChunksAreDiscarded()
    {
        var source = CreateSource();
        var encoded = 0;
        var queue = CreateQueue((_, _, output, _, _) =>
        {
            if (++encoded == 2)
                throw new ProxyEncodeException(ProxyEncodeFailure.Transparent, "alpha");
            return WriteChunk(output);
        });
        var discarded = new List<(SourceIdentity Source, int Scale)>();
        queue.EntryDiscarded += (discardedSource, scale) => discarded.Add((discardedSource, scale));
        options = new ProxyEncodeOptions(50, 30, 4, false);

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.True(cache.IsSkipped(source));
        Assert.Equal(ProxyEncodeFailure.Transparent, item.Failure);
        Assert.Null(cache.Find(source, 50));
        Assert.Equal(0, cache.Count);
        Assert.Equal((source, 50), Assert.Single(discarded));
    }

    [Fact]
    public async Task AnUnexpectedExceptionFailsTheJobWithoutAReason()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _, _, _) => throw new InvalidOperationException("unexpected"));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.Null(item.Failure);
        Assert.True(queue.HasFailed(source, 50));
        Assert.False(cache.IsSkipped(source));
        Assert.IsType<InvalidOperationException>(Assert.Single(reported));
    }

    [Fact]
    public async Task CancelAllStopsARunningJobAndKeepsTheFinishedChunks()
    {
        var source = CreateSource();
        var started = new TaskCompletionSource();
        var queue = CreateQueue(async (first, _, output, _, token) =>
        {
            if (first == 0)
                return await WriteChunk(output);

            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0L;
        });
        options = new ProxyEncodeOptions(50, 30, 4, false);

        var item = await EnqueueAsync(queue, source, 50);
        await started.Task;
        queue.CancelAll();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Cancelled, item.Status);
        Assert.False(queue.HasFailed(source, 50));
        Assert.Equal([0], cache.Find(source, 50)!.Chunks);
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CancelAllStopsAWaitingJob()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, output, _, _) => WriteChunk(output));
        phase = ExportPhase.Exporting;

        var item = await EnqueueAsync(queue, source, 50);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        queue.CancelAll();
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Cancelled, item.Status);
        Assert.Null(cache.Find(source, 50));
    }

    [Fact]
    public async Task ASourceThatChangedDuringTheEncodeIsRejectedAndItsEntryDiscarded()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, output, _, _) =>
        {
            File.WriteAllBytes(source.Path, new byte[200]);
            return WriteChunk(output);
        });

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyEncodeFailure.SourceChanged, item.Failure);
        Assert.Null(cache.Find(source, 50));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.IsSkipped(source));
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task AJobWhoseTemporaryFileVanishedFailsWithoutAReason()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _, _, _) => Task.FromResult(0L));

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Failed, item.Status);
        Assert.True(queue.HasFailed(source, 50));
        Assert.IsType<FileNotFoundException>(Assert.Single(reported));
    }

    [Fact]
    public async Task AJobWhoseEntryWasRemovedMeanwhileIsCancelled()
    {
        var source = CreateSource();
        var queue = CreateQueue((first, _, output, _, _) =>
        {
            if (first == 0)
                cache.Clear();
            return WriteChunk(output);
        });

        var item = await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Equal(ProxyGenerationStatus.Cancelled, item.Status);
        Assert.Null(cache.Find(source, 50));
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task TheSessionIsDisposedWhenTheJobEnds()
    {
        var source = CreateSource();
        FakeEncodeSession? session = null;
        var queue = CreateQueue((_, _) =>
        {
            session = new FakeEncodeSession(Analysis(100), (_, _, output, _, _) => WriteChunk(output));
            return Task.FromResult<IProxyEncodeSession>(session);
        });

        queue.TryEnqueue(source, 50);
        await queue.WhenIdleAsync();

        Assert.True(session!.IsDisposed);
    }

    [Fact]
    public async Task ExpectedFailuresAreNotReported()
    {
        var source = CreateSource();
        var queue = CreateQueue((_, _, _, _, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"));

        await EnqueueAsync(queue, source, 50);
        await queue.WhenIdleAsync();

        Assert.Empty(reported);
    }
}
