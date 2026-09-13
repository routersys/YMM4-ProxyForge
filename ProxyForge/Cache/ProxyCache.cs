using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Cache;

internal sealed class ProxyCache(string directoryPath)
{
    public const string IndexFileName = "index.json";
    public const string ChunkExtension = ".mp4";
    public const string TemporaryExtension = ".tmp";
    public const long DefaultLimitBytes = 10L * 1024L * 1024L * 1024L;

    static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
    };

    readonly Lock gate = new();
    ProxyCacheIndex index = new();
    bool loaded;

    public static ProxyCache Shared { get; } = new(Path.Combine(AppDirectories.UserResourceDirectory, "cache", "ProxyForge"));

    public string DirectoryPath { get; } = directoryPath;

    public string IndexPath => Path.Combine(DirectoryPath, IndexFileName);

    public long LimitBytes { get; set; } = DefaultLimitBytes;

    public long TotalBytes
    {
        get
        {
            using (gate.EnterScope())
            {
                EnsureLoaded();
                return index.Entries.Sum(entry => entry.FileLength);
            }
        }
    }

    public int Count
    {
        get
        {
            using (gate.EnterScope())
            {
                EnsureLoaded();
                return index.Entries.Count;
            }
        }
    }

    public string GetDirectoryPath(ProxyCacheEntry entry) => GetDirectoryPath(entry.Id);

    public string GetChunkPath(ProxyCacheEntry entry, int chunk) => GetChunkPath(entry.Id, chunk);

    public string CreateTemporaryPath()
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            Directory.CreateDirectory(DirectoryPath);
            return Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + TemporaryExtension);
        }
    }

    public ProxyCacheEntry? Find(SourceIdentity source, int scale)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            var entry = index.Entries.FirstOrDefault(candidate => candidate.Matches(source, scale));
            if (entry is null)
                return null;

            if (!Directory.Exists(GetDirectoryPath(entry)))
            {
                index.Entries.Remove(entry);
                Save();
                return null;
            }

            entry.LastUsedTicks = DateTime.UtcNow.Ticks;
            Save();
            return entry.Clone();
        }
    }

    public ProxyCacheEntry? Get(Guid id)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            return index.Entries.FirstOrDefault(candidate => candidate.Id == id)?.Clone();
        }
    }

    public bool IsSkipped(SourceIdentity source)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            return index.Skips.Exists(skip => skip.Matches(source));
        }
    }

    public ProxyCacheEntry Register(ProxyCacheEntry layout)
    {
        if (!layout.HasValidLayout)
            throw new ArgumentException("The entry needs a positive frame count and chunk length.", nameof(layout));

        using (gate.EnterScope())
        {
            EnsureLoaded();

            var existing = index.Entries.FirstOrDefault(candidate => candidate.Matches(layout.Source, layout.Scale));
            if (existing is not null && existing.HasSameLayout(layout) && Directory.Exists(GetDirectoryPath(existing)))
            {
                existing.LastUsedTicks = DateTime.UtcNow.Ticks;
                Save();
                return existing.Clone();
            }

            if (existing is not null && TryDeleteDirectory(GetDirectoryPath(existing)))
                index.Entries.Remove(existing);

            var entry = layout.Clone();
            entry.Id = Guid.NewGuid();
            entry.Chunks = [];
            entry.FileLength = 0L;
            var now = DateTime.UtcNow.Ticks;
            entry.CreatedTicks = now;
            entry.LastUsedTicks = now;
            Directory.CreateDirectory(GetDirectoryPath(entry));
            index.Entries.Add(entry);
            Save();
            return entry.Clone();
        }
    }

    public ProxyCacheEntry? AddChunk(Guid id, int chunk, string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryPath);

        using (gate.EnterScope())
        {
            EnsureLoaded();

            var entry = index.Entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null || chunk < 0 || chunk >= entry.ChunkCount)
            {
                TryDeleteFile(temporaryPath);
                return null;
            }

            var length = new FileInfo(temporaryPath).Length;
            var path = GetChunkPath(entry, chunk);
            Directory.CreateDirectory(GetDirectoryPath(entry));
            if (entry.HasChunk(chunk))
                entry.FileLength -= FileLengthOf(path);
            File.Move(temporaryPath, path, true);

            if (!entry.HasChunk(chunk))
            {
                entry.Chunks.Add(chunk);
                entry.Chunks.Sort();
            }

            entry.FileLength += length;
            entry.LastUsedTicks = DateTime.UtcNow.Ticks;
            TrimCore(entry);
            Save();
            return entry.Clone();
        }
    }

    public ProxyCacheEntry? ForgetChunk(Guid id, int chunk)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();

            var entry = index.Entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null || !entry.HasChunk(chunk))
                return entry?.Clone();

            var path = GetChunkPath(entry, chunk);
            entry.FileLength -= FileLengthOf(path);
            entry.Chunks.Remove(chunk);
            TryDeleteFile(path);
            Save();
            return entry.Clone();
        }
    }

    public void AddSkip(SourceIdentity source, ProxyCacheSkipReason reason)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            index.Skips.RemoveAll(skip => skip.Matches(source));
            index.Skips.Add(new ProxyCacheSkip
            {
                SourcePath = source.Path,
                SourceLength = source.Length,
                SourceWriteTimeTicks = source.WriteTimeTicks,
                Reason = reason,
            });
            Save();
        }
    }

    public bool Remove(Guid id)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            var entry = index.Entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null)
                return false;
            if (!TryDeleteDirectory(GetDirectoryPath(entry)))
                return false;

            index.Entries.Remove(entry);
            Save();
            return true;
        }
    }

    public int Clear()
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            var removed = 0;
            foreach (var entry in index.Entries.ToArray())
            {
                if (!TryDeleteDirectory(GetDirectoryPath(entry)))
                    continue;

                index.Entries.Remove(entry);
                removed++;
            }

            index.Skips.Clear();
            Save();
            return removed;
        }
    }

    public int Trim()
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            var removed = TrimCore(null);
            if (removed > 0)
                Save();
            return removed;
        }
    }

    public IReadOnlyList<ProxyCacheEntry> Snapshot()
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            return index.Entries.Select(entry => entry.Clone()).ToArray();
        }
    }

    string GetDirectoryPath(Guid id) => Path.Combine(DirectoryPath, id.ToString("N"));

    string GetChunkPath(Guid id, int chunk) => Path.Combine(GetDirectoryPath(id), chunk.ToString("D6", CultureInfo.InvariantCulture) + ChunkExtension);

    void EnsureLoaded()
    {
        if (loaded)
            return;

        loaded = true;
        Directory.CreateDirectory(DirectoryPath);
        var dirty = !TryReadIndex(out index);

        dirty |= index.Entries.RemoveAll(entry => !entry.HasValidLayout || !Directory.Exists(GetDirectoryPath(entry))) > 0;
        foreach (var entry in index.Entries)
            dirty |= Reconcile(entry);

        var known = index.Entries.Select(entry => GetDirectoryPath(entry)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath))
        {
            if (!string.Equals(file, IndexPath, StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(DirectoryPath))
        {
            if (!known.Contains(directory))
                TryDeleteDirectory(directory);
        }

        if (dirty)
            Save();
    }

    bool Reconcile(ProxyCacheEntry entry)
    {
        var directory = GetDirectoryPath(entry);
        var listed = entry.Chunks.Where(chunk => chunk >= 0 && chunk < entry.ChunkCount).Distinct().ToList();
        var expected = listed.ToDictionary(chunk => GetChunkPath(entry, chunk), chunk => chunk, StringComparer.OrdinalIgnoreCase);
        var present = new List<int>();
        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (expected.TryGetValue(file, out var chunk))
            {
                present.Add(chunk);
                total += FileLengthOf(file);
            }
            else
            {
                TryDeleteFile(file);
            }
        }

        present.Sort();
        var changed = !present.SequenceEqual(entry.Chunks) || total != entry.FileLength;
        entry.Chunks = present;
        entry.FileLength = total;
        return changed;
    }

    bool TryReadIndex(out ProxyCacheIndex read)
    {
        read = new ProxyCacheIndex();
        if (!File.Exists(IndexPath))
            return true;

        try
        {
            var text = File.ReadAllText(IndexPath);
            var parsed = JsonConvert.DeserializeObject<ProxyCacheIndex>(text, SerializerSettings);
            if (parsed is null)
                return false;

            parsed.Entries ??= [];
            parsed.Skips ??= [];
            var broken = parsed.Entries.RemoveAll(entry => entry is null || entry.Id == Guid.Empty || string.IsNullOrEmpty(entry.SourcePath))
                + parsed.Skips.RemoveAll(skip => skip is null || string.IsNullOrEmpty(skip.SourcePath));
            foreach (var entry in parsed.Entries)
                entry.Chunks ??= [];
            read = parsed;
            return broken == 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Default.Write("ProxyForge: キャッシュの索引を読めなかったため作り直します。", exception);
            return false;
        }
    }

    void Save()
    {
        var temporaryPath = IndexPath + TemporaryExtension;
        try
        {
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(index, SerializerSettings));
            File.Move(temporaryPath, IndexPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Default.Write("ProxyForge: キャッシュの索引を保存できませんでした。", exception);
        }
    }

    int TrimCore(ProxyCacheEntry? protectedEntry)
    {
        var removed = 0;
        var total = index.Entries.Sum(entry => entry.FileLength);
        var candidates = index.Entries.Where(entry => !ReferenceEquals(entry, protectedEntry)).OrderBy(entry => entry.LastUsedTicks).ToList();
        foreach (var entry in candidates)
        {
            if (total <= LimitBytes)
                break;
            if (!TryDeleteDirectory(GetDirectoryPath(entry)))
                continue;

            index.Entries.Remove(entry);
            total -= entry.FileLength;
            removed++;
        }

        return removed;
    }

    static long FileLengthOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0L;
    }

    static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Default.Write($"ProxyForge: キャッシュのファイルを削除できませんでした。{path}", exception);
            return false;
        }
    }

    static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Default.Write($"ProxyForge: キャッシュのフォルダーを削除できませんでした。{path}", exception);
            return false;
        }
    }
}
