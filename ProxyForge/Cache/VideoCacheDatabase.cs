using ProxyForge.Interfaces;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProxyForge.Cache;

internal sealed class VideoCacheDatabase : IVideoCacheDatabase
{
    private const long MagicNumber = 0x0042_4456_5F50_445A;
    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int HeaderCrcOffset = 16;
    private const string DbFileName = "zerodiskproxy.vdb";
    private const string BackupSuffix = ".bak";
    private const string TempSuffix = ".tmp";

    private readonly string _dbPath;
    private readonly string _backupPath;
    private readonly string _cacheDirectory;
    private readonly VideoCacheDiskStore _diskStore;
    private readonly Mutex _mutex;
    private List<VideoCacheRecord> _records;
    private int _disposed;

    public string CacheDirectory => _cacheDirectory;

    internal VideoCacheDatabase(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
        _dbPath = Path.Combine(_cacheDirectory, DbFileName);
        _backupPath = _dbPath + BackupSuffix;
        _diskStore = new VideoCacheDiskStore(_cacheDirectory);

        Span<byte> hashBuf = stackalloc byte[32];
        VideoCacheRecord.ComputePathHashToSpan(_cacheDirectory.AsSpan(), hashBuf);
        Span<char> mutexName = stackalloc char[64];
        "Global\\ZDP_VDB_".AsSpan().CopyTo(mutexName);
        for (var i = 0; i < 16; i++)
            hashBuf[i].TryFormat(mutexName[(15 + i * 2)..], out _, "X2");
        _mutex = new Mutex(false, new string(mutexName[..47]));

        _records = [];
        LoadDatabase();
    }

    public bool TryGet(string originalPath, float scale, out VideoCacheLookupResult result)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Span<byte> hashBuf = stackalloc byte[VideoCacheRecord.HashBytes];
        VideoCacheRecord.ComputePathHashToSpan(originalPath.AsSpan(), hashBuf);

        lock (_records)
        {
            for (var i = 0; i < _records.Count; i++)
            {
                ref var rec = ref CollectionsMarshal.AsSpan(_records)[i];
                if (!rec.IsActive)
                    continue;

                if (!rec.MatchesPathAndScale(originalPath.AsSpan(), hashBuf, scale))
                    continue;

                if (!_diskStore.FileExists(rec.Uuid))
                {
                    rec.Flags = 0;
                    rec.RecomputeCrc32();
                    ScheduleFlush();
                    continue;
                }

                rec.UpdateLastAccess();
                result = ToLookupResult(in rec);
                return true;
            }
        }

        result = default;
        return false;
    }

    public VideoCacheLookupResult[] GetAll()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_records)
        {
            if (_records.Count == 0)
                return [];

            var count = 0;
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsActive)
                    count++;
            }

            if (count == 0)
                return [];

            var results = new VideoCacheLookupResult[count];
            var idx = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsActive)
                    results[idx++] = ToLookupResult(in span[i]);
            }
            return results;
        }
    }

    public void Add(string originalPath, float scale, uint proxyWidth, uint proxyHeight,
        long fileSize, uint fileCrc32, Guid uuid)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Span<byte> hashBuf = stackalloc byte[VideoCacheRecord.HashBytes];
        VideoCacheRecord.ComputePathHashToSpan(originalPath.AsSpan(), hashBuf);

        lock (_records)
        {
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsActive && span[i].MatchesPathAndScale(originalPath.AsSpan(), hashBuf, scale))
                {
                    _diskStore.DeleteFile(span[i].Uuid);
                    span[i].Flags = 0;
                    span[i].RecomputeCrc32();
                }
            }

            var record = new VideoCacheRecord
            {
                Uuid = uuid,
                PathLength = Math.Min(originalPath.Length, VideoCacheRecord.PathMaxChars),
                Scale = scale,
                ProxyWidth = proxyWidth,
                ProxyHeight = proxyHeight,
                FileSize = fileSize,
                CreatedAtTicks = DateTime.UtcNow.Ticks,
                LastAccessedTicks = DateTime.UtcNow.Ticks,
                Flags = VideoCacheRecord.FlagActive,
                FileCrc32 = fileCrc32
            };

            VideoCacheRecord.ComputePathHash(originalPath.AsSpan(), ref record.PathHash);
            WritePathToRecord(originalPath, ref record);
            record.RecomputeCrc32();

            _records.Add(record);
        }

        FlushToFile();
    }

    public bool Remove(Guid uuid)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_records)
        {
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].Uuid == uuid && span[i].IsActive)
                {
                    _diskStore.DeleteFile(span[i].Uuid);
                    span[i].Flags = 0;
                    span[i].RecomputeCrc32();
                    FlushToFile();
                    return true;
                }
            }
        }
        return false;
    }

    public int RemoveByPath(string originalPath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Span<byte> hashBuf = stackalloc byte[VideoCacheRecord.HashBytes];
        VideoCacheRecord.ComputePathHashToSpan(originalPath.AsSpan(), hashBuf);
        var removed = 0;

        lock (_records)
        {
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (!span[i].IsActive)
                    continue;

                if (!span[i].MatchesPath(originalPath.AsSpan(), hashBuf))
                    continue;

                _diskStore.DeleteFile(span[i].Uuid);
                span[i].Flags = 0;
                span[i].RecomputeCrc32();
                removed++;
            }
        }

        if (removed > 0)
            FlushToFile();

        return removed;
    }

    public int ClearAll()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int count;
        lock (_records)
        {
            count = 0;
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsActive)
                {
                    _diskStore.DeleteFile(span[i].Uuid);
                    count++;
                }
            }
            _records.Clear();
        }

        FlushToFile();
        return count;
    }

    public void UpdateLastAccess(Guid uuid)
    {
        lock (_records)
        {
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].Uuid == uuid)
                {
                    span[i].UpdateLastAccess();
                    break;
                }
            }
        }
    }

    public string GetCacheFilePath(Guid uuid) =>
        _diskStore.GetFilePath(uuid);

    internal VideoCacheDiskStore DiskStore => _diskStore;

    private void LoadDatabase()
    {
        _mutex.WaitOne();
        try
        {
            if (TryLoadFromFile(_dbPath))
                return;

            Debug.WriteLine("[VideoCacheDatabase] Primary DB failed, trying backup");
            if (TryLoadFromFile(_backupPath))
            {
                FlushToFileCore();
                return;
            }

            Debug.WriteLine("[VideoCacheDatabase] Both DB files failed, rebuilding from disk");
            RebuildFromDisk();
            FlushToFileCore();
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    private bool TryLoadFromFile(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);

            if (fs.Length < HeaderSize)
                return false;

            Span<byte> headerBuf = stackalloc byte[HeaderSize];
            if (fs.Read(headerBuf) < HeaderSize)
                return false;

            var magic = MemoryMarshal.Read<long>(headerBuf);
            if (magic != MagicNumber)
                return false;

            var version = MemoryMarshal.Read<int>(headerBuf[8..]);
            if (version != FormatVersion)
                return false;

            var headerCrc = MemoryMarshal.Read<uint>(headerBuf[HeaderCrcOffset..]);
            headerBuf.Slice(HeaderCrcOffset, 4).Clear();
            var computedHeaderCrc = Crc32.HashToUInt32(headerBuf[..HeaderCrcOffset]);
            if (headerCrc != computedHeaderCrc)
                return false;

            var recordCount = MemoryMarshal.Read<int>(headerBuf[12..]);
            if (recordCount < 0 || recordCount > 100_000)
                return false;

            var expectedSize = HeaderSize + (long)recordCount * VideoCacheRecord.RecordSize;
            if (fs.Length < expectedSize)
                return false;

            var records = new List<VideoCacheRecord>(recordCount);
            var recordBuf = new byte[VideoCacheRecord.RecordSize];
            var repairNeeded = false;

            for (var i = 0; i < recordCount; i++)
            {
                if (fs.Read(recordBuf) < VideoCacheRecord.RecordSize)
                    break;

                var record = MemoryMarshal.Read<VideoCacheRecord>(recordBuf);

                if (!record.ValidateCrc32())
                {
                    Debug.WriteLine(string.Concat("[VideoCacheDatabase] Corrupt record at index ", i.ToString()));
                    if (TryRepairRecord(ref record))
                    {
                        repairNeeded = true;
                        records.Add(record);
                    }
                    continue;
                }

                if (record.IsActive && !_diskStore.FileExists(record.Uuid))
                {
                    record.Flags = 0;
                    record.RecomputeCrc32();
                    repairNeeded = true;
                }

                records.Add(record);
            }

            lock (_records)
            {
                _records = records;
            }

            if (repairNeeded)
                FlushToFileCore();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[VideoCacheDatabase] Load failed: ", ex.Message));
            return false;
        }
    }

    private bool TryRepairRecord(ref VideoCacheRecord record)
    {
        if (!_diskStore.FileExists(record.Uuid))
            return false;

        try
        {
            var fileSize = _diskStore.GetFileSize(record.Uuid);
            if (fileSize <= 0)
                return false;

            record.FileSize = fileSize;
            record.FileCrc32 = _diskStore.ComputeFileCrc32(record.Uuid);
            record.Flags = VideoCacheRecord.FlagActive;
            record.LastAccessedTicks = DateTime.UtcNow.Ticks;
            record.RecomputeCrc32();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RebuildFromDisk()
    {
        var records = new List<VideoCacheRecord>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.mp4"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(name, out var uuid))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length == 0)
                        continue;

                    var record = new VideoCacheRecord
                    {
                        Uuid = uuid,
                        PathLength = 0,
                        Scale = 0.5f,
                        ProxyWidth = 0,
                        ProxyHeight = 0,
                        FileSize = info.Length,
                        CreatedAtTicks = info.CreationTimeUtc.Ticks,
                        LastAccessedTicks = info.LastAccessTimeUtc.Ticks,
                        Flags = VideoCacheRecord.FlagActive,
                        FileCrc32 = _diskStore.ComputeFileCrc32(uuid)
                    };
                    record.RecomputeCrc32();
                    records.Add(record);
                }
                catch { }
            }
        }
        catch { }

        lock (_records)
        {
            _records = records;
        }
    }

    private void FlushToFile()
    {
        _mutex.WaitOne();
        try
        {
            FlushToFileCore();
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    private void FlushToFileCore()
    {
        List<VideoCacheRecord> snapshot;
        lock (_records)
        {
            snapshot = new List<VideoCacheRecord>(_records.Count);
            var span = CollectionsMarshal.AsSpan(_records);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsActive)
                    snapshot.Add(span[i]);
            }
        }

        try
        {
            if (File.Exists(_dbPath))
            {
                try { File.Copy(_dbPath, _backupPath, true); }
                catch { }
            }

            var tempPath = _dbPath + TempSuffix;

            using (var fs = new FileStream(
                       tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
                       FileOptions.WriteThrough))
            {
                WriteHeader(fs, snapshot.Count);

                var span = CollectionsMarshal.AsSpan(snapshot);
                for (var i = 0; i < span.Length; i++)
                {
                    WriteRecord(fs, in span[i]);
                }

                fs.Flush(true);
            }

            File.Move(tempPath, _dbPath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[VideoCacheDatabase] Flush failed: ", ex.Message));
        }
    }

    private static void WriteHeader(FileStream fs, int recordCount)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        var magic = MagicNumber;
        var version = FormatVersion;
        MemoryMarshal.Write(header, in magic);
        MemoryMarshal.Write(header[8..], in version);
        MemoryMarshal.Write(header[12..], in recordCount);
        var crc = Crc32.HashToUInt32(header[..HeaderCrcOffset]);
        MemoryMarshal.Write(header[HeaderCrcOffset..], in crc);
        fs.Write(header);
    }

    private static void WriteRecord(FileStream fs, in VideoCacheRecord record)
    {
        ReadOnlySpan<byte> span = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<VideoCacheRecord, byte>(ref Unsafe.AsRef(in record)),
            VideoCacheRecord.RecordSize);
        fs.Write(span);
    }

    private void ScheduleFlush()
    {
        _ = Task.Run(() =>
        {
            try { FlushToFile(); }
            catch { }
        });
    }

    private static void WritePathToRecord(string path, ref VideoCacheRecord record)
    {
        Span<byte> raw = record.PathChars;
        raw.Clear();
        var chars = MemoryMarshal.Cast<byte, char>(raw);
        var len = Math.Min(path.Length, VideoCacheRecord.PathMaxChars);
        path.AsSpan(0, len).CopyTo(chars);
        record.PathLength = len;
    }

    private static VideoCacheLookupResult ToLookupResult(in VideoCacheRecord rec) =>
        new(rec.Uuid,
            rec.GetOriginalPath(),
            rec.GetFileName(),
            rec.Scale,
            rec.ProxyWidth,
            rec.ProxyHeight,
            rec.FileSize,
            rec.FileCrc32,
            new DateTime(rec.CreatedAtTicks, DateTimeKind.Utc),
            new DateTime(rec.LastAccessedTicks, DateTimeKind.Utc));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            FlushToFile();
        }
        catch { }

        _mutex.Dispose();
    }
}