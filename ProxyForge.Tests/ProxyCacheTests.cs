using System.IO;
using ProxyForge.Cache;

namespace ProxyForge.Tests;

public sealed class ProxyCacheTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));

    public ProxyCacheTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    string CacheDirectory => Path.Combine(root, "cache");

    ProxyCache CreateCache() => new(CacheDirectory);

    SourceIdentity CreateSource(string name = "source.mp4", int length = 1000)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[length]);
        return SourceIdentity.Of(path)!.Value;
    }

    static ProxyCacheEntry Describe(SourceIdentity source, int scale = 50, int frameCount = 300, int chunkLength = 100) => new()
    {
        SourcePath = source.Path,
        SourceLength = source.Length,
        SourceWriteTimeTicks = source.WriteTimeTicks,
        Scale = scale,
        ProxyWidth = 960,
        ProxyHeight = 540,
        DisplayLeft = -960f,
        DisplayTop = -540f,
        DisplayWidth = 1920f,
        DisplayHeight = 1080f,
        FrameRateNumerator = 30000,
        FrameRateDenominator = 1001,
        DurationTicks = TimeSpan.TicksPerSecond * 10,
        FrameCount = frameCount,
        ChunkLength = chunkLength,
    };

    static string WriteChunk(ProxyCache cache, int length = 100)
    {
        var path = cache.CreateTemporaryPath();
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    static ProxyCacheEntry Fill(ProxyCache cache, ProxyCacheEntry registered, int chunkLength = 100)
    {
        var entry = registered;
        for (var chunk = 0; chunk < registered.ChunkCount; chunk++)
            entry = cache.AddChunk(registered.Id, chunk, WriteChunk(cache, chunkLength))!;
        return entry;
    }

    ProxyCacheEntry AddComplete(ProxyCache cache, SourceIdentity source, int scale = 50, int chunkLength = 100)
        => Fill(cache, cache.Register(Describe(source, scale)), chunkLength);

    [Fact]
    public void AMissingSourceHasNoIdentity()
        => Assert.Null(SourceIdentity.Of(Path.Combine(root, "missing.mp4")));

    [Fact]
    public void AnInvalidPathHasNoIdentity()
        => Assert.Null(SourceIdentity.Of("\0"));

    [Fact]
    public void TheIdentityIsTheFullPathLengthAndWriteTime()
    {
        var path = Path.Combine(root, "source.mp4");
        File.WriteAllBytes(path, new byte[123]);

        var identity = SourceIdentity.Of(Path.Combine(root, ".", "source.mp4"))!.Value;

        Assert.Equal(path, identity.Path);
        Assert.Equal(123L, identity.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, identity.WriteTimeTicks);
    }

    [Fact]
    public void IdentitiesAreEqualRegardlessOfThePathCase()
    {
        var identity = new SourceIdentity(@"C:\Videos\Source.mp4", 123L, 456L);
        var upper = identity with { Path = identity.Path.ToUpperInvariant() };

        Assert.Equal(identity, upper);
        Assert.Equal(identity.GetHashCode(), upper.GetHashCode());
        Assert.NotEqual(identity, identity with { Length = 124L });
        Assert.NotEqual(identity, identity with { WriteTimeTicks = 457L });
        Assert.NotEqual(identity, identity with { Path = @"C:\Videos\Other.mp4" });
    }

    [Theory]
    [InlineData(300, 100, 3)]
    [InlineData(301, 100, 4)]
    [InlineData(1, 100, 1)]
    [InlineData(100, 0, 0)]
    public void TheChunkCountCoversEveryFrame(int frameCount, int chunkLength, int expected)
        => Assert.Equal(expected, new ProxyCacheEntry { FrameCount = frameCount, ChunkLength = chunkLength }.ChunkCount);

    [Fact]
    public void AnEntryIsCompleteWhenEveryChunkExists()
    {
        var entry = new ProxyCacheEntry { FrameCount = 250, ChunkLength = 100, Chunks = [0, 2] };

        Assert.False(entry.IsComplete);
        Assert.True(entry.HasChunk(2));
        Assert.False(entry.HasChunk(1));

        entry.Chunks.Add(1);

        Assert.True(entry.IsComplete);
    }

    [Fact]
    public void ClonesDoNotShareTheChunkList()
    {
        var entry = new ProxyCacheEntry { FrameCount = 300, ChunkLength = 100, Chunks = [0] };

        var clone = entry.Clone();
        clone.Chunks.Add(1);

        Assert.Single(entry.Chunks);
    }

    [Fact]
    public void AnEmptyCacheFindsNothing()
    {
        var cache = CreateCache();

        Assert.Null(cache.Find(CreateSource(), 50));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0L, cache.TotalBytes);
    }

    [Fact]
    public void RegisterCreatesAnEmptyEntryWithItsDirectory()
    {
        var cache = CreateCache();
        var source = CreateSource();

        var before = DateTime.UtcNow.Ticks;
        var registered = cache.Register(Describe(source));

        Assert.NotEqual(Guid.Empty, registered.Id);
        Assert.Empty(registered.Chunks);
        Assert.Equal(3, registered.ChunkCount);
        Assert.Equal(0L, registered.FileLength);
        Assert.InRange(registered.CreatedTicks, before, DateTime.UtcNow.Ticks);
        Assert.Equal(registered.CreatedTicks, registered.LastUsedTicks);
        Assert.True(Directory.Exists(cache.GetDirectoryPath(registered)));
        Assert.Equal(1, cache.Count);
        Assert.NotNull(cache.Find(source, 50));
    }

    [Fact]
    public void RegisterRejectsAnEntryWithoutALayout()
    {
        var cache = CreateCache();

        Assert.Throws<ArgumentException>(() => cache.Register(Describe(CreateSource(), chunkLength: 0)));
        Assert.Throws<ArgumentException>(() => cache.Register(Describe(CreateSource(), frameCount: 0)));
    }

    [Fact]
    public void RegisteringTheSameLayoutAgainReturnsTheExistingEntry()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var registered = cache.Register(Describe(source));
        cache.AddChunk(registered.Id, 1, WriteChunk(cache));

        var again = cache.Register(Describe(source));

        Assert.Equal(registered.Id, again.Id);
        Assert.Equal([1], again.Chunks);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RegisteringADifferentLayoutReplacesTheEntry()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var registered = cache.Register(Describe(source));
        cache.AddChunk(registered.Id, 0, WriteChunk(cache));

        var replaced = cache.Register(Describe(source, chunkLength: 50));

        Assert.NotEqual(registered.Id, replaced.Id);
        Assert.Empty(replaced.Chunks);
        Assert.False(Directory.Exists(cache.GetDirectoryPath(registered)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(0L, cache.TotalBytes);
    }

    [Fact]
    public void AddChunkMovesTheFileAndFillsTheBookkeeping()
    {
        var cache = CreateCache();
        var registered = cache.Register(Describe(CreateSource()));
        var temporary = WriteChunk(cache, 120);
        Thread.Sleep(20);

        var updated = cache.AddChunk(registered.Id, 2, temporary)!;

        Assert.Equal([2], updated.Chunks);
        Assert.Equal(120L, updated.FileLength);
        Assert.True(updated.LastUsedTicks > registered.LastUsedTicks);
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(cache.GetChunkPath(updated, 2)));
        Assert.EndsWith(Path.Combine(registered.Id.ToString("N"), "000002.mp4"), cache.GetChunkPath(updated, 2));
        Assert.Equal(120L, cache.TotalBytes);
    }

    [Fact]
    public void ChunksAreKeptSortedAndReplacingOneKeepsTheTotalRight()
    {
        var cache = CreateCache();
        var registered = cache.Register(Describe(CreateSource()));
        cache.AddChunk(registered.Id, 2, WriteChunk(cache, 100));
        cache.AddChunk(registered.Id, 0, WriteChunk(cache, 100));

        var updated = cache.AddChunk(registered.Id, 2, WriteChunk(cache, 30))!;

        Assert.Equal([0, 2], updated.Chunks);
        Assert.Equal(130L, updated.FileLength);
        Assert.Equal(30L, new FileInfo(cache.GetChunkPath(updated, 2)).Length);
    }

    [Fact]
    public void AddChunkOutsideTheLayoutOrForAnUnknownEntryDiscardsTheFile()
    {
        var cache = CreateCache();
        var registered = cache.Register(Describe(CreateSource()));
        var outside = WriteChunk(cache);
        var unknown = WriteChunk(cache);

        Assert.Null(cache.AddChunk(registered.Id, 3, outside));
        Assert.Null(cache.AddChunk(Guid.NewGuid(), 0, unknown));
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(unknown));
        Assert.Equal(0L, cache.TotalBytes);
    }

    [Fact]
    public void AddChunkRejectsAnEmptyTemporaryPath()
    {
        var cache = CreateCache();
        var registered = cache.Register(Describe(CreateSource()));

        Assert.Throws<ArgumentException>(() => cache.AddChunk(registered.Id, 0, string.Empty));
    }

    [Fact]
    public void FillingEveryChunkCompletesTheEntry()
    {
        var cache = CreateCache();
        var source = CreateSource();

        var entry = AddComplete(cache, source);

        Assert.True(entry.IsComplete);
        Assert.Equal([0, 1, 2], entry.Chunks);
        Assert.Equal(300L, entry.FileLength);
        Assert.True(cache.Find(source, 50)!.IsComplete);
    }

    [Fact]
    public void ForgetChunkDeletesTheFileAndTheCoverage()
    {
        var cache = CreateCache();
        var entry = AddComplete(cache, CreateSource());

        var updated = cache.ForgetChunk(entry.Id, 1)!;

        Assert.Equal([0, 2], updated.Chunks);
        Assert.Equal(200L, updated.FileLength);
        Assert.False(File.Exists(cache.GetChunkPath(entry, 1)));
        Assert.Equal([0, 2], cache.ForgetChunk(entry.Id, 1)!.Chunks);
        Assert.Null(cache.ForgetChunk(Guid.NewGuid(), 0));
    }

    [Fact]
    public void GetReturnsACopyOfTheEntryById()
    {
        var cache = CreateCache();
        var entry = AddComplete(cache, CreateSource());

        var got = cache.Get(entry.Id)!;
        got.Chunks.Clear();

        Assert.Equal(entry.Id, got.Id);
        Assert.Equal(3, cache.Get(entry.Id)!.Chunks.Count);
        Assert.Null(cache.Get(Guid.NewGuid()));
    }

    [Fact]
    public void FindReturnsTheEntryForTheSameSourceAndScale()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);

        var found = cache.Find(source, 50);

        Assert.NotNull(found);
        Assert.Equal(added.Id, found.Id);
        Assert.Equal(30000, found.FrameRateNumerator);
        Assert.Equal(1001, found.FrameRateDenominator);
        Assert.Equal(100, found.ChunkLength);
    }

    [Fact]
    public void FindIgnoresTheCaseOfThePath()
    {
        var cache = CreateCache();
        var source = CreateSource();
        AddComplete(cache, source);

        Assert.NotNull(cache.Find(source with { Path = source.Path.ToUpperInvariant() }, 50));
    }

    [Fact]
    public void FindDistinguishesTheScale()
    {
        var cache = CreateCache();
        var source = CreateSource();
        AddComplete(cache, source, 50);

        Assert.Null(cache.Find(source, 25));
    }

    [Fact]
    public void AChangedSourceIsNotFound()
    {
        var cache = CreateCache();
        var source = CreateSource();
        AddComplete(cache, source);

        Assert.Null(cache.Find(source with { Length = source.Length + 1 }, 50));
        Assert.Null(cache.Find(source with { WriteTimeTicks = source.WriteTimeTicks + 1 }, 50));
    }

    [Fact]
    public void FindTouchesTheLastUse()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);
        Thread.Sleep(20);

        var found = cache.Find(source, 50)!;

        Assert.True(found.LastUsedTicks > added.LastUsedTicks);
        Assert.Equal(found.LastUsedTicks, cache.Snapshot().Single().LastUsedTicks);
    }

    [Fact]
    public void FindDropsAnEntryWhoseDirectoryVanished()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);
        Directory.Delete(cache.GetDirectoryPath(added), true);

        Assert.Null(cache.Find(source, 50));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DifferentScalesOfTheSameSourceCoexist()
    {
        var cache = CreateCache();
        var source = CreateSource();
        AddComplete(cache, source, 50);
        AddComplete(cache, source, 25);

        Assert.Equal(2, cache.Count);
        Assert.Equal(50, cache.Find(source, 50)!.Scale);
        Assert.Equal(25, cache.Find(source, 25)!.Scale);
    }

    [Fact]
    public void AddChunkTrimsTheLeastRecentlyUsedEntriesBeyondTheLimit()
    {
        var cache = CreateCache();
        cache.LimitBytes = 750;
        var first = AddComplete(cache, CreateSource("a.mp4"));
        Thread.Sleep(20);
        var second = AddComplete(cache, CreateSource("b.mp4"));
        Thread.Sleep(20);
        cache.Find(first.Source, 50);
        Thread.Sleep(20);

        var third = AddComplete(cache, CreateSource("c.mp4"));

        Assert.Equal(2, cache.Count);
        Assert.True(Directory.Exists(cache.GetDirectoryPath(first)));
        Assert.False(Directory.Exists(cache.GetDirectoryPath(second)));
        Assert.True(Directory.Exists(cache.GetDirectoryPath(third)));
    }

    [Fact]
    public void AnEntryLargerThanTheLimitStaysAlone()
    {
        var cache = CreateCache();
        cache.LimitBytes = 50;
        AddComplete(cache, CreateSource("a.mp4"));

        var big = AddComplete(cache, CreateSource("b.mp4"));

        Assert.Equal(big.Id, cache.Snapshot().Single().Id);
    }

    [Fact]
    public void TrimAppliesANewLimit()
    {
        var cache = CreateCache();
        AddComplete(cache, CreateSource("a.mp4"));
        Thread.Sleep(20);
        AddComplete(cache, CreateSource("b.mp4"));
        cache.LimitBytes = 450;

        Assert.Equal(1, cache.Trim());
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.Trim());
    }

    [Fact]
    public void ACacheExactlyAtTheLimitIsNotTrimmed()
    {
        var cache = CreateCache();
        AddComplete(cache, CreateSource("a.mp4"));
        AddComplete(cache, CreateSource("b.mp4"));
        cache.LimitBytes = 600;

        Assert.Equal(0, cache.Trim());
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TrimSkipsAnEntryThatCannotBeDeleted()
    {
        var cache = CreateCache();
        var locked = AddComplete(cache, CreateSource("a.mp4"));
        Thread.Sleep(20);
        var second = AddComplete(cache, CreateSource("b.mp4"));
        cache.LimitBytes = 450;

        using (File.Open(cache.GetChunkPath(locked, 0), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, cache.Trim());
        }

        Assert.True(Directory.Exists(cache.GetDirectoryPath(locked)));
        Assert.False(Directory.Exists(cache.GetDirectoryPath(second)));
    }

    [Fact]
    public void RemoveDeletesTheDirectoryAndTheEntry()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);

        Assert.True(cache.Remove(added.Id));
        Assert.False(Directory.Exists(cache.GetDirectoryPath(added)));
        Assert.Null(cache.Find(source, 50));
        Assert.False(cache.Remove(added.Id));
    }

    [Fact]
    public void RemoveKeepsAnEntryWhoseFileIsLocked()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());

        using (File.Open(cache.GetChunkPath(added, 0), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(cache.Remove(added.Id));
        }

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ClearDeletesEverythingIncludingTheSkips()
    {
        var cache = CreateCache();
        var skipped = CreateSource("skipped.mp4");
        cache.AddSkip(skipped, ProxyCacheSkipReason.Transparent);
        var first = AddComplete(cache, CreateSource("a.mp4"));
        var second = AddComplete(cache, CreateSource("b.mp4"));

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Count);
        Assert.False(Directory.Exists(cache.GetDirectoryPath(first)));
        Assert.False(Directory.Exists(cache.GetDirectoryPath(second)));
        Assert.False(cache.IsSkipped(skipped));
    }

    [Fact]
    public void ASkipIsRememberedForTheSameSourceOnly()
    {
        var cache = CreateCache();
        var source = CreateSource();

        cache.AddSkip(source, ProxyCacheSkipReason.Transparent);

        Assert.True(cache.IsSkipped(source));
        Assert.True(cache.IsSkipped(source with { Path = source.Path.ToUpperInvariant() }));
        Assert.False(cache.IsSkipped(source with { Length = source.Length + 1 }));
        Assert.False(cache.IsSkipped(source with { WriteTimeTicks = source.WriteTimeTicks + 1 }));
    }

    [Fact]
    public void TheIndexSurvivesANewInstance()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var skipped = CreateSource("skipped.mp4");
        var registered = cache.Register(Describe(source));
        var added = cache.AddChunk(registered.Id, 1, WriteChunk(cache))!;
        cache.AddSkip(skipped, ProxyCacheSkipReason.Transparent);

        var reloaded = CreateCache();

        var found = reloaded.Find(source, 50);
        Assert.NotNull(found);
        Assert.Equal(added.Id, found.Id);
        Assert.Equal([1], found.Chunks);
        Assert.Equal(added.DisplayLeft, found.DisplayLeft);
        Assert.Equal(added.DurationTicks, found.DurationTicks);
        Assert.Equal(added.FrameCount, found.FrameCount);
        Assert.Equal(added.ChunkLength, found.ChunkLength);
        Assert.Equal(100L, found.FileLength);
        Assert.True(reloaded.IsSkipped(skipped));
    }

    [Fact]
    public void LoadingDropsEntriesWhoseDirectoryIsGone()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);
        Directory.Delete(cache.GetDirectoryPath(added), true);

        var reloaded = CreateCache();

        Assert.Equal(0, reloaded.Count);
        Assert.DoesNotContain(added.Id.ToString("N"), File.ReadAllText(reloaded.IndexPath));
    }

    [Fact]
    public void LoadingReconcilesTheChunksWithTheFiles()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());
        File.Delete(cache.GetChunkPath(added, 1));
        var stray = Path.Combine(cache.GetDirectoryPath(added), "000007.mp4");
        File.WriteAllBytes(stray, new byte[10]);
        var partial = Path.Combine(cache.GetDirectoryPath(added), "000002.tmp");
        File.WriteAllBytes(partial, new byte[10]);

        var reloaded = CreateCache();

        var found = reloaded.Get(added.Id)!;
        Assert.Equal([0, 2], found.Chunks);
        Assert.Equal(200L, found.FileLength);
        Assert.False(File.Exists(stray));
        Assert.False(File.Exists(partial));
        Assert.Equal([0, 2], CreateCache().Get(added.Id)!.Chunks);
    }

    [Fact]
    public void LoadingDeletesLeftoversAndOrphans()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());
        var leftover = cache.CreateTemporaryPath();
        File.WriteAllBytes(leftover, new byte[10]);
        var orphanFile = Path.Combine(cache.DirectoryPath, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(orphanFile, new byte[10]);
        var orphanDirectory = Path.Combine(cache.DirectoryPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanDirectory);
        File.WriteAllBytes(Path.Combine(orphanDirectory, "000000.mp4"), new byte[10]);

        var reloaded = CreateCache();

        Assert.Equal(1, reloaded.Count);
        Assert.True(File.Exists(cache.GetChunkPath(added, 0)));
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(orphanFile));
        Assert.False(Directory.Exists(orphanDirectory));
    }

    [Fact]
    public void ACorruptIndexIsReplacedAndItsFilesAreDiscarded()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());
        File.WriteAllText(cache.IndexPath, "{ not json");

        var reloaded = CreateCache();

        Assert.Equal(0, reloaded.Count);
        Assert.False(Directory.Exists(cache.GetDirectoryPath(added)));
        Assert.Contains("\"Entries\"", File.ReadAllText(reloaded.IndexPath));
    }

    [Fact]
    public void AnIndexWithBrokenRecordsKeepsTheGoodOnes()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = AddComplete(cache, source);
        var text = File.ReadAllText(cache.IndexPath);
        text = text.Replace("\"Entries\": [", "\"Entries\": [ null, { \"Id\": \"00000000000000000000000000000000\" }, { \"Id\": \"" + Guid.NewGuid().ToString("N") + "\", \"SourcePath\": \"\" },");
        File.WriteAllText(cache.IndexPath, text);

        var reloaded = CreateCache();

        Assert.Equal(added.Id, reloaded.Snapshot().Single().Id);
    }

    [Fact]
    public void AnIndexFromTheSingleFileFormatIsDiscarded()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());
        var text = File.ReadAllText(cache.IndexPath).Replace("\"ChunkLength\": 100", "\"ChunkLength\": 0");
        File.WriteAllText(cache.IndexPath, text);

        var reloaded = CreateCache();

        Assert.Equal(0, reloaded.Count);
        Assert.False(Directory.Exists(cache.GetDirectoryPath(added)));
    }

    [Fact]
    public void AnIndexListingChunksOutsideTheLayoutIsRepaired()
    {
        var cache = CreateCache();
        var added = AddComplete(cache, CreateSource());
        var text = File.ReadAllText(cache.IndexPath).Replace("\"Chunks\": [", "\"Chunks\": [ 7, 0, -1,");
        File.WriteAllText(cache.IndexPath, text);

        var reloaded = CreateCache();

        Assert.Equal([0, 1, 2], reloaded.Get(added.Id)!.Chunks);
    }

    [Fact]
    public void SnapshotsAreCopies()
    {
        var cache = CreateCache();
        var source = CreateSource();
        AddComplete(cache, source);

        cache.Snapshot()[0].Scale = 99;
        cache.Snapshot()[0].Chunks.Clear();

        Assert.Equal(50, cache.Snapshot()[0].Scale);
        Assert.Equal(3, cache.Snapshot()[0].Chunks.Count);
        Assert.NotNull(cache.Find(source, 50));
    }

    [Fact]
    public void TemporaryPathsAreUniqueAndInsideTheCacheDirectory()
    {
        var cache = CreateCache();

        var first = cache.CreateTemporaryPath();
        var second = cache.CreateTemporaryPath();

        Assert.NotEqual(first, second);
        Assert.StartsWith(cache.DirectoryPath, first);
        Assert.EndsWith(".tmp", first);
        Assert.True(Directory.Exists(cache.DirectoryPath));
    }
}
