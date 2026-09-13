using System.Collections.ObjectModel;
using System.IO;
using ProxyForge.Cache;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Encoding;

internal readonly record struct ProxyEncodeOptions(int BitrateScale, int KeyFrameInterval, int ChunkSeconds, bool UsesHardwareEncoder);

internal sealed class ProxyGenerationQueue(
    ProxyCache cache,
    ProxyEncodeSessionFactory open,
    Func<ProxyEncodeOptions> options,
    Func<ExportPhase> exportPhase,
    SourceFocus focus,
    Action<Exception> reportUnexpected,
    Action<Action> onUi)
{
    public static readonly TimeSpan ExportPollInterval = TimeSpan.FromMilliseconds(500);

    readonly record struct Key(SourceIdentity Source, int Scale);

    sealed class ChunkProgress(ProxyGenerationItem item, ProxyCacheEntry entry, int chunk, Action<Action> onUi) : IProgress<double>
    {
        public void Report(double value)
        {
            var fraction = Math.Clamp(value, 0d, 1d);
            onUi(() =>
            {
                item.Progress = (entry.Chunks.Count + fraction) / entry.ChunkCount;
                item.Coverage = new ProxyChunkCoverage(entry.ChunkCount, entry.Chunks, chunk, fraction);
            });
        }
    }

    readonly Lock gate = new();
    readonly Dictionary<Key, (CancellationTokenSource Cancellation, Task Task)> pending = [];
    readonly HashSet<Key> failures = [];
    readonly SemaphoreSlim slot = new(1, 1);

    public static ProxyGenerationQueue Shared { get; } = new(
        ProxyCache.Shared,
        (request, cancellationToken) => new ProxyEncoder(FFmpegRuntime.Executables, VideoSourceLoader.Load).OpenAsync(request, cancellationToken),
        () => new ProxyEncodeOptions(ProxyForgeSettings.Default.BitrateScale, ProxyForgeSettings.Default.KeyFrameInterval, ProxyForgeSettings.Default.ChunkSeconds, ProxyForgeSettings.Default.UsesHardwareEncoder),
        ExportDetector.Shared.Resolve,
        SourceFocus.Shared,
        ProxyForgeTelemetry.Report,
        UiThread.Post);

    public ObservableCollection<ProxyGenerationItem> Items { get; } = [];

    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan CancelledRetention { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<SourceIdentity, int, ProxyCacheEntry>? ChunkCompleted;

    public event Action<SourceIdentity, int>? EntryDiscarded;

    public bool IsPending(SourceIdentity source, int scale)
    {
        using (gate.EnterScope())
            return pending.ContainsKey(new Key(source, scale));
    }

    public bool HasFailed(SourceIdentity source, int scale)
    {
        using (gate.EnterScope())
            return failures.Contains(new Key(source, scale));
    }

    public bool TryEnqueue(SourceIdentity source, int scale)
    {
        var key = new Key(source, scale);
        var item = new ProxyGenerationItem(source.Path, scale);
        var cancellation = new CancellationTokenSource();

        using (gate.EnterScope())
        {
            if (pending.ContainsKey(key) || failures.Contains(key))
            {
                cancellation.Dispose();
                return false;
            }

            var task = Task.Run(() => RunAsync(key, item, cancellation));
            pending[key] = (cancellation, task);
        }

        return true;
    }

    public Task WhenIdleAsync()
    {
        Task[] tasks;
        using (gate.EnterScope())
            tasks = pending.Values.Select(job => job.Task).ToArray();
        return Task.WhenAll(tasks);
    }

    public void ForgetFailures()
    {
        using (gate.EnterScope())
            failures.Clear();
    }

    public void CancelAll()
    {
        (CancellationTokenSource Cancellation, Task Task)[] jobs;
        using (gate.EnterScope())
            jobs = pending.Values.ToArray();

        foreach (var job in jobs)
        {
            try
            {
                job.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    async Task RunAsync(Key key, ProxyGenerationItem item, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var retention = CompletedRetention;
        onUi(() => Items.Add(item));

        try
        {
            await slot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await WaitForExportToEndAsync(token).ConfigureAwait(false);
                onUi(() => item.Status = ProxyGenerationStatus.Generating);
                await GenerateAsync(key, item, token).ConfigureAwait(false);
                onUi(() =>
                {
                    item.Progress = 1d;
                    item.Status = ProxyGenerationStatus.Completed;
                });
            }
            finally
            {
                slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            retention = CancelledRetention;
            onUi(() => item.Status = ProxyGenerationStatus.Cancelled);
        }
        catch (ProxyEncodeException exception)
        {
            retention = FailedRetention;
            RememberFailure(key, exception);
            Log.Default.Write($"ProxyForge: プロキシを生成できませんでした。{key.Source.Path}", exception);
            onUi(() =>
            {
                item.Failure = exception.Failure;
                item.Status = ProxyGenerationStatus.Failed;
            });
        }
        catch (Exception exception)
        {
            retention = FailedRetention;
            RememberFailure(key, null);
            Log.Default.Write($"ProxyForge: プロキシの生成で想定していない例外が起きました。{key.Source.Path}", exception);
            reportUnexpected(exception);
            onUi(() => item.Status = ProxyGenerationStatus.Failed);
        }
        finally
        {
            using (gate.EnterScope())
                pending.Remove(key);
            cancellation.Dispose();
        }

        await Task.Delay(retention, CancellationToken.None).ConfigureAwait(false);
        onUi(() => Items.Remove(item));
    }

    async Task WaitForExportToEndAsync(CancellationToken token)
    {
        while (exportPhase() != ExportPhase.Idle)
            await Task.Delay(ExportPollInterval, token).ConfigureAwait(false);
    }

    async Task GenerateAsync(Key key, ProxyGenerationItem item, CancellationToken token)
    {
        var current = options();
        var request = new ProxyEncodeRequest(
            key.Source.Path,
            cache.DirectoryPath,
            key.Scale,
            current.BitrateScale,
            current.KeyFrameInterval,
            current.UsesHardwareEncoder);

        using var session = await open(request, token).ConfigureAwait(false);
        var analysis = session.Analysis;
        var entry = cache.Register(Describe(key, analysis, ProxyChunkPlan.LengthFor(analysis.FrameRate, current.ChunkSeconds)));
        var chunkCount = entry.ChunkCount;
        Publish(item, entry, null);

        while (ProxyChunkPlan.Next(entry.Chunks, chunkCount, entry.ChunkLength, focus.Get(key.Source)) is { } chunk)
        {
            await WaitForExportToEndAsync(token).ConfigureAwait(false);

            var firstFrame = chunk * entry.ChunkLength;
            var frameCount = Math.Min(entry.ChunkLength, analysis.FrameCount - firstFrame);
            var temporaryPath = cache.CreateTemporaryPath();
            ProxyCacheEntry? updated;
            try
            {
                Publish(item, entry, chunk);
                await session.EncodeAsync(firstFrame, frameCount, temporaryPath, new ChunkProgress(item, entry, chunk, onUi), token).ConfigureAwait(false);
                if (SourceIdentity.Of(key.Source.Path) != key.Source)
                    throw new ProxyEncodeException(ProxyEncodeFailure.SourceChanged, "The source file changed while the proxy was being generated.");

                updated = cache.AddChunk(entry.Id, chunk, temporaryPath);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }

            if (updated is null)
                throw new OperationCanceledException();

            entry = updated;
            Publish(item, entry, null);
            ChunkCompleted?.Invoke(key.Source, key.Scale, entry);
        }
    }

    void Publish(ProxyGenerationItem item, ProxyCacheEntry entry, int? chunk)
        => onUi(() =>
        {
            item.Progress = (double)entry.Chunks.Count / entry.ChunkCount;
            item.Coverage = new ProxyChunkCoverage(entry.ChunkCount, entry.Chunks, chunk, 0d);
        });

    static ProxyCacheEntry Describe(Key key, ProxyAnalysis analysis, int chunkLength) => new()
    {
        SourcePath = key.Source.Path,
        SourceLength = key.Source.Length,
        SourceWriteTimeTicks = key.Source.WriteTimeTicks,
        Scale = key.Scale,
        ProxyWidth = analysis.Geometry.Width,
        ProxyHeight = analysis.Geometry.Height,
        DisplayLeft = analysis.Display.Left,
        DisplayTop = analysis.Display.Top,
        DisplayWidth = analysis.Display.Width,
        DisplayHeight = analysis.Display.Height,
        FrameRateNumerator = analysis.FrameRate.Numerator,
        FrameRateDenominator = analysis.FrameRate.Denominator,
        DurationTicks = analysis.Duration.Ticks,
        FrameCount = analysis.FrameCount,
        ChunkLength = chunkLength,
    };

    void RememberFailure(Key key, ProxyEncodeException? exception)
    {
        using (gate.EnterScope())
            failures.Add(key);

        if (exception?.Failure is not (ProxyEncodeFailure.Transparent or ProxyEncodeFailure.SourceChanged))
            return;

        if (exception.Failure == ProxyEncodeFailure.Transparent)
            cache.AddSkip(key.Source, ProxyCacheSkipReason.Transparent);

        var entry = cache.Find(key.Source, key.Scale);
        if (entry is not null && cache.Remove(entry.Id))
            EntryDiscarded?.Invoke(key.Source, key.Scale);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Default.Write($"ProxyForge: 途中まで書き出したファイルを削除できませんでした。{path}", exception);
        }
    }
}
