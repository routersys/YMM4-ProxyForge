using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Cache;

internal sealed class ProxyCache(string directoryPath)
{
    public const string IndexFileName = "index.json";
    public const string ProxyExtension = ".mp4";
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

    public string GetFilePath(ProxyCacheEntry entry) => GetFilePath(entry.Id);

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

            if (!File.Exists(GetFilePath(entry)))
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

    public bool IsSkipped(SourceIdentity source)
    {
        using (gate.EnterScope())
        {
            EnsureLoaded();
            return index.Skips.Exists(skip => skip.Matches(source));
        }
    }

    public ProxyCacheEntry Add(ProxyCacheEntry entry, string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryPath);

        using (gate.EnterScope())
        {
            EnsureLoaded();

            entry.Id = Guid.NewGuid();
            var now = DateTime.UtcNow.Ticks;
            entry.CreatedTicks = now;
            entry.LastUsedTicks = now;
            entry.FileLength = new FileInfo(temporaryPath).Length;
            File.Move(temporaryPath, GetFilePath(entry), true);

            var source = new SourceIdentity(entry.SourcePath, entry.SourceLength, entry.SourceWriteTimeTicks);
            foreach (var stale in index.Entries.Where(candidate => candidate.Matches(source, entry.Scale)).ToArray())
            {
                if (TryDeleteFile(GetFilePath(stale)))
                    index.Entries.Remove(stale);
            }

            index.Entries.Add(entry);
            TrimCore(entry);
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
            if (!TryDeleteFile(GetFilePath(entry)))
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
                if (!TryDeleteFile(GetFilePath(entry)))
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

    string GetFilePath(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + ProxyExtension);

    void EnsureLoaded()
    {
        if (loaded)
            return;

        loaded = true;
        Directory.CreateDirectory(DirectoryPath);
        var dirty = !TryReadIndex(out index);

        dirty |= index.Entries.RemoveAll(entry => !File.Exists(GetFilePath(entry))) > 0;
        var known = index.Entries.Select(entry => GetFilePath(entry)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath))
        {
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, TemporaryExtension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ProxyExtension, StringComparison.OrdinalIgnoreCase) && !known.Contains(file))
                TryDeleteFile(file);
        }

        if (dirty)
            Save();
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
            if (!TryDeleteFile(GetFilePath(entry)))
                continue;

            index.Entries.Remove(entry);
            total -= entry.FileLength;
            removed++;
        }

        return removed;
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
}
