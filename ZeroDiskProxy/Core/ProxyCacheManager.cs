using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using ZeroDiskProxy.Localization;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Progress;
using ZeroDiskProxy.Settings;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ProxyCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingGenerations = new(StringComparer.Ordinal);
    private readonly Func<IProxyEncoder> _encoderFactory;
    private readonly MemoryBudget _memoryBudget;
    private int _disposed;

    internal ObservableCollection<ProxyGenerationItem> ActiveGenerations { get; } = [];
    internal event Action<string, ProxyCacheEntry>? ProxyCompleted;

    internal ProxyCacheManager(Func<IProxyEncoder> encoderFactory, MemoryBudget memoryBudget)
    {
        _encoderFactory = encoderFactory;
        _memoryBudget = memoryBudget;
    }

    private static string MakeCacheKey(string path, float scale)
        => string.Concat(NormalizePath(path), "|", scale.ToString("F3", CultureInfo.InvariantCulture));

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch { return path.ToUpperInvariant(); }
    }

    internal ProxyCacheEntry? TryGetProxy(string originalPath, float scale)
    {
        var key = MakeCacheKey(originalPath, scale);
        if (_cache.TryGetValue(key, out var entry) && entry.IsValid)
            return entry;
        return null;
    }

    internal bool ProxyExists(string originalPath, float scale)
    {
        var key = MakeCacheKey(originalPath, scale);
        return _cache.TryGetValue(key, out var entry) && entry.IsValid;
    }

    internal void StartProxyGeneration(string originalPath, float scale, ZeroDiskProxySettings settings)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var key = MakeCacheKey(originalPath, scale);

        if (_cache.TryGetValue(key, out var existing) && existing.IsValid)
            return;

        if (_pendingGenerations.ContainsKey(key))
            return;

        var cts = new CancellationTokenSource();
        if (!_pendingGenerations.TryAdd(key, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => RunGenerationAsync(key, originalPath, scale, settings, cts), CancellationToken.None);
    }

    private async Task RunGenerationAsync(string key, string originalPath, float scale, ZeroDiskProxySettings settings, CancellationTokenSource cts)
    {
        var progressItem = new ProxyGenerationItem(originalPath);
        DispatchInvoke(static state => ((ObservableCollection<ProxyGenerationItem>)state.collection).Add((ProxyGenerationItem)state.item),
            (collection: (object)ActiveGenerations, item: (object)progressItem));

        try
        {
            using var encoder = _encoderFactory();
            var entry = await encoder.EncodeAsync(
                originalPath, scale, settings.BitrateFactor / 100f, settings.GopSize,
                progressItem, cts.Token);

            _cache[key] = entry;

            DispatchInvoke(static state =>
            {
                var (pi, inMem) = ((ProxyGenerationItem, bool))state;
                pi.Progress = 100;
                pi.IsCompleted = true;
                pi.StatusMessage = Translate.ProxyGenerationStatusCompleted;
                pi.IsInMemory = inMem;
            }, (progressItem, entry.IsInMemory));

            ProxyCompleted?.Invoke(originalPath, entry);

            await Task.Delay(2000, CancellationToken.None);
            DispatchInvoke(static state =>
            {
                var (gens, pi) = ((ObservableCollection<ProxyGenerationItem>, ProxyGenerationItem))state;
                gens.Remove(pi);
            }, (ActiveGenerations, progressItem));
        }
        catch (OperationCanceledException)
        {
            DispatchInvoke(static state =>
            {
                var pi = (ProxyGenerationItem)state;
                pi.StatusMessage = Translate.ProxyGenerationStatusCanceled;
                pi.IsFailed = true;
            }, progressItem);

            await Task.Delay(3000, CancellationToken.None);
            DispatchInvoke(static state =>
            {
                var (gens, pi) = ((ObservableCollection<ProxyGenerationItem>, ProxyGenerationItem))state;
                gens.Remove(pi);
            }, (ActiveGenerations, progressItem));
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            DispatchInvoke(static state =>
            {
                var (pi, m) = ((ProxyGenerationItem, string))state;
                pi.StatusMessage = string.Concat(Translate.ProxyGenerationStatusFailedPrefix, m);
                pi.IsFailed = true;
            }, (progressItem, msg));

            Debug.WriteLine(string.Concat("[ZeroDiskProxy] Proxy generation failed for ", originalPath, ": ", msg));

            await Task.Delay(8000, CancellationToken.None);
            DispatchInvoke(static state =>
            {
                var (gens, pi) = ((ObservableCollection<ProxyGenerationItem>, ProxyGenerationItem))state;
                gens.Remove(pi);
            }, (ActiveGenerations, progressItem));
        }
        finally
        {
            _pendingGenerations.TryRemove(key, out _);
            cts.Dispose();
        }
    }

    private static void DispatchInvoke<TState>(Action<TState> action, TState state)
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (app.Dispatcher.CheckAccess())
            action(state);
        else
            app.Dispatcher.Invoke(() => action(state));
    }

    private static void DispatchInvoke(Action action)
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (app.Dispatcher.CheckAccess())
            action();
        else
            app.Dispatcher.Invoke(action);
    }

    internal void RemoveProxy(string originalPath, float scale)
    {
        var key = MakeCacheKey(originalPath, scale);
        if (!_cache.TryRemove(key, out var entry))
            return;

        var size = entry.DataSize;
        entry.Dispose();
        _memoryBudget.RecordDeallocation(size);
    }

    internal (int count, long totalSize) ClearAll()
    {
        var entries = _cache.Values.ToArray();
        _cache.Clear();

        foreach (var kvp in _pendingGenerations)
        {
            if (_pendingGenerations.TryRemove(kvp.Key, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); }
                catch { }
            }
        }

        long totalSize = 0;
        foreach (var e in entries)
        {
            totalSize += e.DataSize;
            e.Dispose();
        }

        _memoryBudget.RecordDeallocation(totalSize);
        return (entries.Length, totalSize);
    }

    internal CacheEntrySnapshot[] GetAllSnapshots()
    {
        var values = _cache.Values;
        var result = new List<CacheEntrySnapshot>(values.Count);
        foreach (var e in values)
        {
            if (e.IsValid)
                result.Add(e.CreateSnapshot());
        }
        return result.ToArray();
    }

    internal (int entryCount, long memSize, long diskSize, int memCount, int diskCount) GetCacheInfo()
    {
        long ms = 0, ds = 0;
        int mc = 0, dc = 0;
        foreach (var e in _cache.Values)
        {
            if (!e.IsValid)
                continue;

            if (e.IsInMemory)
            {
                ms += e.DataSize;
                mc++;
            }
            else
            {
                ds += e.DataSize;
                dc++;
            }
        }
        return (_cache.Count, ms, ds, mc, dc);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ClearAll();
    }
}