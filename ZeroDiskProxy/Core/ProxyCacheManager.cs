using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Localization;
using ZeroDiskProxy.Progress;
using ZeroDiskProxy.Settings;

namespace ZeroDiskProxy.Core;

internal sealed class ProxyCacheManager : IDisposable
{
    private readonly record struct CacheKey(string Path, float Scale) : IEquatable<CacheKey>
    {
        public bool Equals(CacheKey other) =>
            Scale == other.Scale &&
            string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), Scale);
    }

    private readonly ConcurrentDictionary<CacheKey, ProxyCacheEntry> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, CancellationTokenSource> _pendingGenerations = new();
    private readonly IProxyEncoderFactory _encoderFactory;
    private int _disposed;

    internal ObservableCollection<ProxyGenerationItem> ActiveGenerations { get; } = [];
    internal event Action<string, ProxyCacheEntry>? ProxyCompleted;

    internal ProxyCacheManager(IProxyEncoderFactory encoderFactory)
    {
        _encoderFactory = encoderFactory;
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
        return _cache.TryGetValue(key, out var entry) && entry.IsValid ? entry : null;
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

        var delayMs = 2000;

        try
        {
            using var encoder = _encoderFactory.Create();
            var entry = await encoder.EncodeAsync(
                originalPath, scale, settings.BitrateFactor / 100f, settings.GopSize,
                progressItem, cts.Token).ConfigureAwait(false);

            var stored = _cache.GetOrAdd(key, entry);
            if (!ReferenceEquals(stored, entry))
                entry.Dispose();

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

        await Task.Delay(delayMs, CancellationToken.None).ConfigureAwait(false);
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
        if (_cache.TryRemove(key, out var entry))
            entry.Dispose();
    }

    internal (int count, long totalSize) ClearAll()
    {
        foreach (var kvp in _pendingGenerations)
        {
            if (_pendingGenerations.TryRemove(kvp.Key, out var removed))
            {
                try { removed.Cancel(); removed.Dispose(); }
                catch { }
            }
        }

        long totalSize = 0;
        var count = 0;
        foreach (var kvp in _cache)
        {
            if (_cache.TryRemove(kvp.Key, out var removed))
            {
                totalSize += removed.DataSize;
                removed.Dispose();
                count++;
            }
        }

        return (count, totalSize);
    }

    internal CacheEntrySnapshot[] GetAllSnapshots()
    {
        if (_cache.IsEmpty)
            return [];

        var result = new List<CacheEntrySnapshot>();
        foreach (var entry in _cache.Values)
        {
            if (entry.IsValid)
                result.Add(entry.CreateSnapshot());
        }

        return result.Count > 0 ? [.. result] : [];
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