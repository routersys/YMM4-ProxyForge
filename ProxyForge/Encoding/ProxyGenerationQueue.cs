using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ProxyForge.Cache;
using ProxyForge.Export;
using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Encoding;

internal delegate Task<ProxyEncodeResult> ProxyEncodeFunction(ProxyEncodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken);

internal readonly record struct ProxyEncodeOptions(int BitrateScale, int KeyFrameInterval, bool UsesHardwareEncoder);

internal sealed class ProxyGenerationQueue(
    ProxyCache cache,
    ProxyEncodeFunction encode,
    Func<ProxyEncodeOptions> options,
    Func<ExportPhase> exportPhase,
    Action<Exception> reportUnexpected)
{
    public static readonly TimeSpan ExportPollInterval = TimeSpan.FromMilliseconds(500);

    readonly record struct Key(SourceIdentity Source, int Scale);

    sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(Key x, Key y)
            => x.Scale == y.Scale && x.Source.Matches(y.Source.Path, y.Source.Length, y.Source.WriteTimeTicks);

        public int GetHashCode(Key key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Source.Path), key.Source.Length, key.Source.WriteTimeTicks, key.Scale);
    }

    sealed class ItemProgress(ProxyGenerationItem item) : IProgress<double>
    {
        public void Report(double value) => OnUi(() => item.Progress = value);
    }

    readonly Lock gate = new();
    readonly Dictionary<Key, (CancellationTokenSource Cancellation, Task Task)> pending = new(KeyComparer.Instance);
    readonly HashSet<Key> failures = new(KeyComparer.Instance);
    readonly SemaphoreSlim slot = new(1, 1);

    public static ProxyGenerationQueue Shared { get; } = new(
        ProxyCache.Shared,
        (request, progress, cancellationToken) => new ProxyEncoder(FFmpegRuntime.Executables, VideoSourceLoader.Load).EncodeAsync(request, progress, cancellationToken),
        () => new ProxyEncodeOptions(ProxyForgeSettings.Default.BitrateScale, ProxyForgeSettings.Default.KeyFrameInterval, ProxyForgeSettings.Default.UsesHardwareEncoder),
        () => ExportDetector.Shared.Phase,
        ProxyForgeTelemetry.Report);

    public ObservableCollection<ProxyGenerationItem> Items { get; } = [];

    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan CancelledRetention { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<SourceIdentity, int, ProxyCacheEntry>? Completed;

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
        OnUi(() => Items.Add(item));

        try
        {
            await slot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await WaitForExportToEndAsync(token).ConfigureAwait(false);
                OnUi(() => item.Status = ProxyGenerationStatus.Generating);
                var entry = await GenerateAsync(key, item, token).ConfigureAwait(false);
                OnUi(() =>
                {
                    item.Progress = 1d;
                    item.Status = ProxyGenerationStatus.Completed;
                });
                Completed?.Invoke(key.Source, key.Scale, entry);
            }
            finally
            {
                slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            retention = CancelledRetention;
            OnUi(() => item.Status = ProxyGenerationStatus.Cancelled);
        }
        catch (ProxyEncodeException exception)
        {
            retention = FailedRetention;
            RememberFailure(key, exception);
            Log.Default.Write($"ProxyForge: プロキシを生成できませんでした。{key.Source.Path}", exception);
            OnUi(() =>
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
            OnUi(() => item.Status = ProxyGenerationStatus.Failed);
        }
        finally
        {
            using (gate.EnterScope())
                pending.Remove(key);
            cancellation.Dispose();
        }

        await Task.Delay(retention, CancellationToken.None).ConfigureAwait(false);
        OnUi(() => Items.Remove(item));
    }

    async Task WaitForExportToEndAsync(CancellationToken token)
    {
        while (exportPhase() != ExportPhase.Idle)
            await Task.Delay(ExportPollInterval, token).ConfigureAwait(false);
    }

    async Task<ProxyCacheEntry> GenerateAsync(Key key, ProxyGenerationItem item, CancellationToken token)
    {
        var temporaryPath = cache.CreateTemporaryPath();
        var current = options();
        var request = new ProxyEncodeRequest(
            key.Source.Path,
            temporaryPath,
            cache.DirectoryPath,
            key.Scale,
            current.BitrateScale,
            current.KeyFrameInterval,
            current.UsesHardwareEncoder);

        ProxyEncodeResult result;
        try
        {
            result = await encode(request, new ItemProgress(item), token).ConfigureAwait(false);
            var latest = SourceIdentity.Of(key.Source.Path);
            if (latest is null || !latest.Value.Matches(key.Source.Path, key.Source.Length, key.Source.WriteTimeTicks))
                throw new ProxyEncodeException(ProxyEncodeFailure.SourceChanged, "The source file changed while the proxy was being generated.");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        return cache.Add(new ProxyCacheEntry
        {
            SourcePath = key.Source.Path,
            SourceLength = key.Source.Length,
            SourceWriteTimeTicks = key.Source.WriteTimeTicks,
            Scale = key.Scale,
            ProxyWidth = result.Geometry.Width,
            ProxyHeight = result.Geometry.Height,
            DisplayLeft = result.Display.Left,
            DisplayTop = result.Display.Top,
            DisplayWidth = result.Display.Width,
            DisplayHeight = result.Display.Height,
            FrameRateNumerator = result.FrameRate.Numerator,
            FrameRateDenominator = result.FrameRate.Denominator,
            DurationTicks = result.Duration.Ticks,
            FrameCount = result.FrameCount,
        }, temporaryPath);
    }

    void RememberFailure(Key key, ProxyEncodeException? exception)
    {
        using (gate.EnterScope())
            failures.Add(key);

        if (exception?.Failure == ProxyEncodeFailure.Transparent)
            cache.AddSkip(key.Source, ProxyCacheSkipReason.Transparent);
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

    static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
            return;

        dispatcher.InvokeAsync(action);
    }
}
