using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ZeroDiskProxy.Cache;
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
    private readonly VideoCacheDatabase? _videoCache;
    private readonly string _tmpDirectory;
    private int _disposed;

    internal ObservableCollection<ProxyGenerationItem> ActiveGenerations { get; } = [];
    internal event Action<string, ProxyCacheEntry>? ProxyCompleted;

    internal ProxyCacheManager(IProxyEncoderFactory encoderFactory, VideoCacheDatabase? videoCache = null, string tmpDirectory = "")
    {
        _encoderFactory = encoderFactory;
        _videoCache = videoCache;
        _tmpDirectory = tmpDirectory;
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

        if (_videoCache is not null &&
            ZeroDiskProxySettings.Default.EnableVideoCache &&
            _videoCache.TryGet(originalPath, scale, out var dbResult))
        {
            var cachePath = _videoCache.GetCacheFilePath(dbResult.Uuid);
            if (File.Exists(cachePath))
            {
                var diskEntry = new ProxyCacheEntry(originalPath, scale)
                {
                    ProxyWidth = dbResult.ProxyWidth,
                    ProxyHeight = dbResult.ProxyHeight
                };
                diskEntry.SetDiskPath(cachePath, dbResult.FileSize);
                diskEntry.MarkPersistent();
                var stored = _cache.GetOrAdd(key, diskEntry);
                if (!ReferenceEquals(stored, diskEntry))
                    diskEntry.Dispose();
                _videoCache.UpdateLastAccess(dbResult.Uuid);
                return stored.IsValid ? stored : null;
            }
        }

        return null;
    }

    internal bool ProxyExists(string originalPath, float scale)
    {
        var key = MakeKey(originalPath, scale);
        if (_cache.TryGetValue(key, out var entry) && entry.IsValid)
            return true;

        if (_videoCache is not null &&
            ZeroDiskProxySettings.Default.EnableVideoCache &&
            _videoCache.TryGet(originalPath, scale, out _))
            return true;

        return false;
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

            if (_videoCache is not null && ZeroDiskProxySettings.Default.EnableVideoCache)
                PersistToVideoCache(originalPath, scale, stored);

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

        _videoCache?.RemoveByPath(originalPath);
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
        var resultList = new List<CacheEntrySnapshot>();

        foreach (var entry in _cache.Values)
        {
            if (entry.IsValid)
                resultList.Add(entry.CreateSnapshot());
        }

        if (_videoCache is not null && ZeroDiskProxySettings.Default.EnableVideoCache)
        {
            var dbEntries = _videoCache.GetAll();
            foreach (var db in dbEntries)
            {
                var alreadyInMemory = false;
                foreach (var existing in resultList)
                {
                    if (string.Equals(existing.OriginalPath, db.OriginalPath, StringComparison.OrdinalIgnoreCase) &&
                        existing.Scale == db.Scale)
                    {
                        alreadyInMemory = true;
                        break;
                    }
                }
                if (!alreadyInMemory)
                {
                    resultList.Add(new CacheEntrySnapshot(
                        db.OriginalPath, db.FileName, db.Scale,
                        db.ProxyWidth, db.ProxyHeight,
                        false, db.FileSize, db.CreatedAt, db.LastAccessedAt));
                }
            }
        }

        return resultList.Count > 0 ? [.. resultList] : [];
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

    private void PersistToVideoCache(string originalPath, float scale, ProxyCacheEntry entry)
    {
        if (_videoCache is null)
            return;

        try
        {
            var uuid = Guid.NewGuid();
            var diskStore = _videoCache.DiskStore;
            var destPath = diskStore.GetFilePath(uuid);

            var tmpPath = entry.GetCurrentDiskPath();
            if (tmpPath is not null && File.Exists(tmpPath) && IsTmpFile(tmpPath))
            {
                File.Move(tmpPath, destPath, true);
                entry.ReplaceDiskPath(destPath);
            }
            else
            {
                using var stream = entry.OpenReadStream();
                diskStore.SaveFromStream(uuid, stream);
            }

            var fileCrc = diskStore.ComputeFileCrc32FromPath(destPath);
            var fileSize = new FileInfo(destPath).Length;

            _videoCache.Add(originalPath, scale, entry.ProxyWidth, entry.ProxyHeight,
                fileSize, fileCrc, uuid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ProxyCacheManager] PersistToVideoCache failed: ", ex.Message));
        }
    }

    private bool IsTmpFile(string path) =>
        !string.IsNullOrEmpty(_tmpDirectory) &&
        path.StartsWith(_tmpDirectory, StringComparison.OrdinalIgnoreCase);
}