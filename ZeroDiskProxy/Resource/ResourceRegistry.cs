using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Resource;

internal sealed class ResourceRegistry : IDisposable
{
    private readonly ConcurrentDictionary<long, RegistryEntry> _entries = new();
    private long _nextId;
    private int _disposed;

    internal int Count => _entries.Count;

    internal ResourceHandle TrackComPtr(nint comPtr, string? debugName = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (comPtr == nint.Zero)
            return ResourceHandle.Invalid;

        var id = Interlocked.Increment(ref _nextId);
        _entries[id] = new RegistryEntry(id, comPtr, null, debugName);
        return new ResourceHandle(id, this);
    }

    internal ResourceHandle TrackDisposable(IDisposable disposable, string? debugName = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = Interlocked.Increment(ref _nextId);
        _entries[id] = new RegistryEntry(id, nint.Zero, disposable, debugName);
        return new ResourceHandle(id, this);
    }

    internal void Release(long id)
    {
        if (!_entries.TryRemove(id, out var entry))
            return;

        ReleaseEntry(entry);
    }

    internal void ReleaseAll()
    {
        var snapshot = _entries.Values.ToArray();
        _entries.Clear();
        foreach (var entry in snapshot)
            ReleaseEntry(entry);
    }

    private static void ReleaseEntry(RegistryEntry entry)
    {
        try
        {
            if (entry.ComPtr != nint.Zero)
                Marshal.Release(entry.ComPtr);

            entry.Disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ResourceRegistry] Release failed: ", ex.Message));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ReleaseAll();
    }

    private sealed record RegistryEntry(long Id, nint ComPtr, IDisposable? Disposable, string? DebugName);
}

internal readonly struct ResourceHandle : IDisposable
{
    private readonly long _id;
    private readonly ResourceRegistry? _registry;

    internal ResourceHandle(long id, ResourceRegistry registry)
    {
        _id = id;
        _registry = registry;
    }

    internal static ResourceHandle Invalid => default;

    public void Dispose() => _registry?.Release(_id);
}