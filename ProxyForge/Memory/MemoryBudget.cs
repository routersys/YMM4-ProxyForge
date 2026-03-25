using System.Runtime.InteropServices;

namespace ProxyForge.Memory;

internal sealed class MemoryBudget(long reserveMb, long maxCacheMb)
{
    private readonly long _reserveBytes = reserveMb * 1024L * 1024L;
    private readonly long _maxCacheBytes = maxCacheMb * 1024L * 1024L;
    private long _allocatedBytes;

    private long _memoryCachePacked;
    private const long MemoryCacheIntervalMs = 100L;

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

    private static uint WrapSafeElapsedMs(uint nowMs, uint cachedMs) =>
        unchecked(nowMs - cachedMs);

    private long GetCachedAvailablePhysicalMemory()
    {
        var nowMs = (uint)Environment.TickCount64;
        var packed = Interlocked.Read(ref _memoryCachePacked);
        var cachedMs = (uint)(packed >> 32);

        if (WrapSafeElapsedMs(nowMs, cachedMs) < (uint)MemoryCacheIntervalMs)
        {
            var memKb = (uint)packed;
            if (memKb > 0)
                return (long)memKb * 1024L;
        }

        var fresh = GetAvailablePhysicalMemory();
        var freshKbCapped = (uint)Math.Min(fresh >> 10, (long)uint.MaxValue);
        Interlocked.Exchange(ref _memoryCachePacked, ((long)nowMs << 32) | freshKbCapped);
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