using System.IO;
using System.IO.Hashing;
using ZeroDiskProxy.Memory;

namespace ZeroDiskProxy.Cache;

internal sealed class VideoCacheDiskStore
{
    private readonly string _cacheDirectory;

    internal VideoCacheDiskStore(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    internal string GetFilePath(Guid uuid)
    {
        Span<char> buf = stackalloc char[36 + 4];
        uuid.TryFormat(buf, out _, "D");
        ".mp4".AsSpan().CopyTo(buf[36..]);
        return Path.Combine(_cacheDirectory, new string(buf));
    }

    internal bool FileExists(Guid uuid) =>
        File.Exists(GetFilePath(uuid));

    internal long GetFileSize(Guid uuid)
    {
        var path = GetFilePath(uuid);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    internal void SaveFromEntry(Guid uuid, byte[] data, int length)
    {
        var path = GetFilePath(uuid);
        using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        fs.Write(data, 0, length);
        fs.Flush(true);
    }

    internal void SaveFromStream(Guid uuid, Stream source)
    {
        var path = GetFilePath(uuid);
        var buf = BufferPool.Rent(65536);
        try
        {
            using var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
                FileOptions.SequentialScan | FileOptions.WriteThrough);

            int read;
            while ((read = source.Read(buf, 0, buf.Length)) > 0)
                fs.Write(buf, 0, read);

            fs.Flush(true);
        }
        finally
        {
            BufferPool.Return(buf);
        }
    }

    internal void CopyFromFile(Guid uuid, string sourcePath)
    {
        var destPath = GetFilePath(uuid);
        var buf = BufferPool.Rent(65536);
        try
        {
            using var src = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);
            using var dst = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
                FileOptions.SequentialScan | FileOptions.WriteThrough);

            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
                dst.Write(buf, 0, read);

            dst.Flush(true);
        }
        finally
        {
            BufferPool.Return(buf);
        }
    }

    internal bool DeleteFile(Guid uuid)
    {
        var path = GetFilePath(uuid);
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal uint ComputeFileCrc32(Guid uuid)
    {
        var path = GetFilePath(uuid);
        if (!File.Exists(path))
            return 0;

        var buf = BufferPool.Rent(65536);
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);

            var crc = new Crc32();
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                crc.Append(buf.AsSpan(0, read));

            Span<byte> hash = stackalloc byte[4];
            crc.GetHashAndReset(hash);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash);
        }
        finally
        {
            BufferPool.Return(buf);
        }
    }

    internal uint ComputeFileCrc32FromPath(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        var buf = BufferPool.Rent(65536);
        try
        {
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);

            var crc = new Crc32();
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                crc.Append(buf.AsSpan(0, read));

            Span<byte> hash = stackalloc byte[4];
            crc.GetHashAndReset(hash);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash);
        }
        finally
        {
            BufferPool.Return(buf);
        }
    }

    internal int CleanupOrphanedFiles(HashSet<Guid> knownUuids)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.mp4"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(name, out var uuid) && !knownUuids.Contains(uuid))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return removed;
    }
}
