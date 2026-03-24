using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeroDiskProxy.Memory;

internal sealed class MemoryBudget
{
    private readonly long _reserveBytes;
    private readonly long _maxCacheBytes;
    private long _allocatedBytes;

    private long _cachedAvailableMemory;
    private long _memoryCacheTimestamp;
    private static readonly long MemoryCacheTicks = Stopwatch.Frequency / 10;

    internal MemoryBudget(long reserveMb, long maxCacheMb)
    {
        _reserveBytes = reserveMb * 1024L * 1024L;
        _maxCacheBytes = maxCacheMb * 1024L * 1024L;
    }

    internal long AllocatedBytes => Volatile.Read(ref _allocatedBytes);

    internal bool CanAllocateInMemory(long requestedBytes)
    {
        if (requestedBytes <= 0)
            return true;

        var limit = Math.Min(GetCachedAvailablePhysicalMemory() - _reserveBytes, _maxCacheBytes);
        if (limit <= 0)
            return false;

        var allocated = Volatile.Read(ref _allocatedBytes);
        return allocated >= 0 && requestedBytes <= limit - allocated;
    }

    internal bool TryAllocate(long requestedBytes)
    {
        if (requestedBytes <= 0)
            return true;

        while (true)
        {
            var available = GetCachedAvailablePhysicalMemory();
            var current = Volatile.Read(ref _allocatedBytes);
            if (current < 0)
                return false;

            var limit = Math.Min(available - _reserveBytes, _maxCacheBytes);
            if (limit <= 0)
                return false;

            var next = current + requestedBytes;
            if (next < 0 || next > limit)
                return false;

            if (Interlocked.CompareExchange(ref _allocatedBytes, next, current) == current)
                return true;
        }
    }

    internal void RecordAllocation(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _allocatedBytes, bytes);
    }

    internal void RecordDeallocation(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _allocatedBytes, -bytes);
    }

    internal static long EstimateProxySize(long originalFileSize, float scale)
        => (long)(originalFileSize * scale * scale * 0.3);

    private long GetCachedAvailablePhysicalMemory()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _memoryCacheTimestamp) < MemoryCacheTicks)
        {
            var cached = Volatile.Read(ref _cachedAvailableMemory);
            if (cached > 0)
                return cached;
        }

        var fresh = GetAvailablePhysicalMemory();
        Volatile.Write(ref _cachedAvailableMemory, fresh);
        Volatile.Write(ref _memoryCacheTimestamp, now);
        return fresh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long GetAvailablePhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 2L * 1024 * 1024 * 1024;
    }
}