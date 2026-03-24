using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Localization;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Progress;
using ZeroDiskProxy.Settings;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ProxyCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingGenerations = new(StringComparer.Ordinal);
    private readonly IProxyEncoderFactory _encoderFactory;
    private readonly MemoryBudget _memoryBudget;
    private int _disposed;

    internal ObservableCollection<ProxyGenerationItem> ActiveGenerations { get; } = [];
    internal event Action<string, ProxyCacheEntry>? ProxyCompleted;

    internal ProxyCacheManager(IProxyEncoderFactory encoderFactory, MemoryBudget memoryBudget)
    {
        _encoderFactory = encoderFactory;
        _memoryBudget = memoryBudget;
    }

    private static string MakeCacheKey(string path, float scale)
        => $"{GetFullPath(path)}|{scale.ToString("F3", CultureInfo.InvariantCulture)}";

    private static string GetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
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
        DispatchInvoke(() => ActiveGenerations.Add(progressItem));

        try
        {
            using var encoder = _encoderFactory.Create();
            var entry = await encoder.EncodeAsync(
                originalPath, scale, settings.BitrateFactor / 100f, settings.GopSize,
                progressItem, cts.Token);

            var stored = _cache.GetOrAdd(key, entry);
            if (!ReferenceEquals(stored, entry))
            {
                _memoryBudget.RecordDeallocation(entry.DataSize);
                entry.Dispose();
            }

            var isInMem = stored.IsInMemory;
            DispatchInvoke(() =>
            {
                progressItem.Progress = 100;
                progressItem.IsCompleted = true;
                progressItem.StatusMessage = Translate.ProxyGenerationStatusCompleted;
                progressItem.IsInMemory = isInMem;
            });

            ProxyCompleted?.Invoke(originalPath, stored);

            await Task.Delay(2000, CancellationToken.None);
            DispatchInvoke(() => ActiveGenerations.Remove(progressItem));
        }
        catch (OperationCanceledException)
        {
            DispatchInvoke(() =>
            {
                progressItem.StatusMessage = Translate.ProxyGenerationStatusCanceled;
                progressItem.IsFailed = true;
            });

            await Task.Delay(3000, CancellationToken.None);
            DispatchInvoke(() => ActiveGenerations.Remove(progressItem));
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] Proxy generation failed for ", originalPath, ": ", ex));
            DispatchInvoke(() =>
            {
                progressItem.StatusMessage = string.Concat(Translate.ProxyGenerationStatusFailedPrefix, msg);
                progressItem.IsFailed = true;
            });

            await Task.Delay(8000, CancellationToken.None);
            DispatchInvoke(() => ActiveGenerations.Remove(progressItem));
        }
        finally
        {
            _pendingGenerations.TryRemove(key, out _);
            cts.Dispose();
        }
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
        foreach (var key in _pendingGenerations.Keys.ToArray())
        {
            if (_pendingGenerations.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); }
                catch { }
            }
        }

        long totalSize = 0;
        int count = 0;
        foreach (var key in _cache.Keys.ToArray())
        {
            if (_cache.TryRemove(key, out var entry))
            {
                totalSize += entry.DataSize;
                entry.Dispose();
                count++;
            }
        }

        _memoryBudget.RecordDeallocation(totalSize);
        return (count, totalSize);
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