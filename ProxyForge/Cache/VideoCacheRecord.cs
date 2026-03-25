using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ProxyForge.Cache;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VideoCacheRecord : IEquatable<VideoCacheRecord>
{
    internal const int PathMaxChars = 260;
    internal const int PathBufferBytes = PathMaxChars * 2;
    internal const int HashBytes = 32;
    internal const int ReservedBytes = 36;
    internal const int RecordSize = 656;
    internal const int CrcOffset = 616;

    internal Guid Uuid;
    internal PathHashBuffer PathHash;
    internal int PathLength;
    internal PathCharBuffer PathChars;
    internal float Scale;
    internal uint ProxyWidth;
    internal uint ProxyHeight;
    internal long FileSize;
    internal long CreatedAtTicks;
    internal long LastAccessedTicks;
    internal int Flags;
    internal uint FileCrc32;
    internal uint RecordCrc32;
    internal ReservedBuffer Reserved;

    [InlineArray(HashBytes)]
    internal struct PathHashBuffer
    {
        private byte _element;
    }

    [InlineArray(PathBufferBytes)]
    internal struct PathCharBuffer
    {
        private byte _element;
    }

    [InlineArray(ReservedBytes)]
    internal struct ReservedBuffer
    {
        private byte _element;
    }

    internal const int FlagActive = 1;

    internal readonly bool IsActive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & FlagActive) != 0;
    }

    internal static VideoCacheRecord Create(
        string originalPath, float scale, uint proxyWidth, uint proxyHeight,
        long fileSize, uint fileCrc32)
    {
        var record = new VideoCacheRecord
        {
            Uuid = Guid.NewGuid(),
            PathLength = Math.Min(originalPath.Length, PathMaxChars),
            Scale = scale,
            ProxyWidth = proxyWidth,
            ProxyHeight = proxyHeight,
            FileSize = fileSize,
            CreatedAtTicks = DateTime.UtcNow.Ticks,
            LastAccessedTicks = DateTime.UtcNow.Ticks,
            Flags = FlagActive,
            FileCrc32 = fileCrc32
        };

        ComputePathHash(originalPath, ref record.PathHash);
        WritePathChars(originalPath, ref record.PathChars, record.PathLength);
        record.RecordCrc32 = record.ComputeCrc32();

        return record;
    }

    internal readonly string GetOriginalPath()
    {
        ReadOnlySpan<byte> raw = PathChars;
        var chars = MemoryMarshal.Cast<byte, char>(raw);
        return new string(chars[..PathLength]);
    }

    internal readonly string GetFileName()
    {
        ReadOnlySpan<byte> raw = PathChars;
        var chars = MemoryMarshal.Cast<byte, char>(raw)[..PathLength];
        var lastSep = chars.LastIndexOfAny('\\', '/');
        return lastSep >= 0 ? new string(chars[(lastSep + 1)..]) : new string(chars);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool MatchesPath(ReadOnlySpan<char> path, ReadOnlySpan<byte> pathHash)
    {
        ReadOnlySpan<byte> storedHash = PathHash;
        if (!storedHash.SequenceEqual(pathHash))
            return false;

        ReadOnlySpan<byte> raw = PathChars;
        var storedPath = MemoryMarshal.Cast<byte, char>(raw)[..PathLength];
        return storedPath.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool MatchesPathAndScale(ReadOnlySpan<char> path, ReadOnlySpan<byte> pathHash, float scale)
    {
        return Scale == scale && MatchesPath(path, pathHash);
    }

    internal readonly bool ValidateCrc32()
    {
        return RecordCrc32 == ComputeCrc32();
    }

    internal void RecomputeCrc32()
    {
        RecordCrc32 = ComputeCrc32();
    }

    internal void UpdateLastAccess()
    {
        LastAccessedTicks = DateTime.UtcNow.Ticks;
        RecomputeCrc32();
    }

    private readonly uint ComputeCrc32()
    {
        ReadOnlySpan<byte> span = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<VideoCacheRecord, byte>(ref Unsafe.AsRef(in this)),
            CrcOffset);
        return Crc32.HashToUInt32(span);
    }

    internal static void ComputePathHash(ReadOnlySpan<char> path, ref PathHashBuffer hash)
    {
        Span<char> upper = stackalloc char[Math.Min(path.Length, PathMaxChars)];
        path[..upper.Length].ToUpperInvariant(upper);
        var bytes = MemoryMarshal.AsBytes(upper);
        Span<byte> dest = hash;
        SHA256.TryHashData(bytes, dest, out _);
    }

    internal static void ComputePathHashToSpan(ReadOnlySpan<char> path, Span<byte> dest)
    {
        Span<char> upper = stackalloc char[Math.Min(path.Length, PathMaxChars)];
        path[..upper.Length].ToUpperInvariant(upper);
        var bytes = MemoryMarshal.AsBytes(upper);
        SHA256.TryHashData(bytes, dest, out _);
    }

    private static void WritePathChars(string path, ref PathCharBuffer buffer, int length)
    {
        Span<byte> raw = buffer;
        raw.Clear();
        var chars = MemoryMarshal.Cast<byte, char>(raw);
        path.AsSpan(0, length).CopyTo(chars);
    }

    public readonly bool Equals(VideoCacheRecord other) =>
        Uuid.Equals(other.Uuid);

    public override readonly bool Equals(object? obj) =>
        obj is VideoCacheRecord other && Equals(other);

    public override readonly int GetHashCode() => Uuid.GetHashCode();
}