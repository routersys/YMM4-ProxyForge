using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly string _path;
        private readonly float _scale;

        internal CacheKey(string path, float scale)
        {
            _path = path;
            _scale = scale;
        }

        public bool Equals(CacheKey other) =>
            _scale == other._scale &&
            string.Equals(_path, other._path, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is CacheKey k && Equals(k);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(_path), _scale);
    }

    private readonly ConcurrentDictionary<CacheKey, ProxyCacheEntry> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, CancellationTokenSource> _pendingGenerations = new();
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

    private static CacheKey MakeKey(string path, float scale) =>
        new(GetFullPath(path), scale);

    private static string GetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    internal ProxyCacheEntry? TryGetProxy(string originalPath, float scale)
    {
        var key = MakeKey(originalPath, scale);
        if (_cache.TryGetValue(key, out var entry) && entry.IsValid)
            return entry;
        return null;
    }

    internal bool ProxyExists(string originalPath, float scale)
    {
        var key = MakeKey(originalPath, scale);
        return _cache.TryGetValue(key, out var entry) && entry.IsValid;
    }

    internal void StartProxyGeneration(string originalPath, float scale, ZeroDiskProxySettings settings)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var key = MakeKey(originalPath, scale);

        if (_cache.TryGetValue(key, out var existing) && existing.IsValid)
            return;

        var cts = new CancellationTokenSource();
        if (!_pendingGenerations.TryAdd(key, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => RunGenerationAsync(key, originalPath, scale, settings, cts), CancellationToken.None);
    }

    private async Task RunGenerationAsync(CacheKey key, string originalPath, float scale, ZeroDiskProxySettings settings, CancellationTokenSource cts)
    {
        var progressItem = new ProxyGenerationItem(originalPath);
        DispatchInvoke(() => ActiveGenerations.Add(progressItem));

        int delayMs = 2000;

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
        }
        catch (OperationCanceledException)
        {
            delayMs = 3000;
            DispatchInvoke(() =>
            {
                progressItem.StatusMessage = Translate.ProxyGenerationStatusCanceled;
                progressItem.IsFailed = true;
            });
        }
        catch (Exception ex)
        {
            delayMs = 8000;
            var msg = ex.Message;
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] Proxy generation failed for ", originalPath, ": ", ex));
            DispatchInvoke(() =>
            {
                progressItem.StatusMessage = string.Concat(Translate.ProxyGenerationStatusFailedPrefix, msg);
                progressItem.IsFailed = true;
            });
        }
        finally
        {
            _pendingGenerations.TryRemove(key, out _);
            cts.Dispose();
        }

        await Task.Delay(delayMs, CancellationToken.None);
        DispatchInvoke(() => ActiveGenerations.Remove(progressItem));
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
        var key = MakeKey(originalPath, scale);
        if (!_cache.TryRemove(key, out var entry))
            return;

        var size = entry.DataSize;
        entry.Dispose();
        _memoryBudget.RecordDeallocation(size);
    }

    internal (int count, long totalSize) ClearAll()
    {
        foreach (var (key, cts) in _pendingGenerations)
        {
            if (_pendingGenerations.TryRemove(key, out var removed))
            {
                try { removed.Cancel(); removed.Dispose(); }
                catch { }
            }
        }

        long totalSize = 0;
        int count = 0;
        foreach (var (key, entry) in _cache)
        {
            if (_cache.TryRemove(key, out var removed))
            {
                totalSize += removed.DataSize;
                removed.Dispose();
                count++;
            }
        }

        _memoryBudget.RecordDeallocation(totalSize);
        return (count, totalSize);
    }

    internal CacheEntrySnapshot[] GetAllSnapshots()
    {
        var capacity = _cache.Count;
        if (capacity == 0)
            return [];

        var result = new CacheEntrySnapshot[capacity];
        int i = 0;
        foreach (var entry in _cache.Values)
        {
            if (entry.IsValid && i < result.Length)
                result[i++] = entry.CreateSnapshot();
        }

        if (i < result.Length)
            Array.Resize(ref result, i);

        return result;
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