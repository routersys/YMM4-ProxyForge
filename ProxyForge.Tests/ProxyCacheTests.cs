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

    static ProxyCacheEntry Describe(SourceIdentity source, int scale = 50) => new()
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
        FrameCount = 300,
    };

    static string WriteProxy(ProxyCache cache, int length = 100)
    {
        var path = cache.CreateTemporaryPath();
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

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

    [Fact]
    public void AnEmptyCacheFindsNothing()
    {
        var cache = CreateCache();

        Assert.Null(cache.Find(CreateSource(), 50));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0L, cache.TotalBytes);
    }

    [Fact]
    public void AddMovesTheFileAndFillsTheBookkeeping()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var temporary = WriteProxy(cache, 100);

        var before = DateTime.UtcNow.Ticks;
        var added = cache.Add(Describe(source), temporary);

        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.Equal(100L, added.FileLength);
        Assert.InRange(added.CreatedTicks, before, DateTime.UtcNow.Ticks);
        Assert.Equal(added.CreatedTicks, added.LastUsedTicks);
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(cache.GetFilePath(added)));
        Assert.EndsWith(".mp4", cache.GetFilePath(added));
        Assert.Equal(1, cache.Count);
        Assert.Equal(100L, cache.TotalBytes);
    }

    [Fact]
    public void FindReturnsTheEntryForTheSameSourceAndScale()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));

        var found = cache.Find(source, 50);

        Assert.NotNull(found);
        Assert.Equal(added.Id, found.Id);
        Assert.Equal(30000, found.FrameRateNumerator);
        Assert.Equal(1001, found.FrameRateDenominator);
    }

    [Fact]
    public void FindIgnoresTheCaseOfThePath()
    {
        var cache = CreateCache();
        var source = CreateSource();
        cache.Add(Describe(source), WriteProxy(cache));

        Assert.NotNull(cache.Find(source with { Path = source.Path.ToUpperInvariant() }, 50));
    }

    [Fact]
    public void FindDistinguishesTheScale()
    {
        var cache = CreateCache();
        var source = CreateSource();
        cache.Add(Describe(source, 50), WriteProxy(cache));

        Assert.Null(cache.Find(source, 25));
    }

    [Fact]
    public void AChangedSourceIsNotFound()
    {
        var cache = CreateCache();
        var source = CreateSource();
        cache.Add(Describe(source), WriteProxy(cache));

        Assert.Null(cache.Find(source with { Length = source.Length + 1 }, 50));
        Assert.Null(cache.Find(source with { WriteTimeTicks = source.WriteTimeTicks + 1 }, 50));
    }

    [Fact]
    public void FindTouchesTheLastUse()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));
        Thread.Sleep(20);

        var found = cache.Find(source, 50)!;

        Assert.True(found.LastUsedTicks > added.LastUsedTicks);
        Assert.Equal(found.LastUsedTicks, cache.Snapshot().Single().LastUsedTicks);
    }

    [Fact]
    public void FindDropsAnEntryWhoseFileVanished()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));
        File.Delete(cache.GetFilePath(added));

        Assert.Null(cache.Find(source, 50));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void AddingTheSameSourceAndScaleAgainReplacesTheOldProxy()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var first = cache.Add(Describe(source), WriteProxy(cache, 100));

        var second = cache.Add(Describe(source), WriteProxy(cache, 200));

        Assert.False(File.Exists(cache.GetFilePath(first)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(second.Id, cache.Find(source, 50)!.Id);
        Assert.Equal(200L, cache.TotalBytes);
    }

    [Fact]
    public void DifferentScalesOfTheSameSourceCoexist()
    {
        var cache = CreateCache();
        var source = CreateSource();
        cache.Add(Describe(source, 50), WriteProxy(cache));
        cache.Add(Describe(source, 25), WriteProxy(cache));

        Assert.Equal(2, cache.Count);
        Assert.Equal(50, cache.Find(source, 50)!.Scale);
        Assert.Equal(25, cache.Find(source, 25)!.Scale);
    }

    [Fact]
    public void AddTrimsTheLeastRecentlyUsedEntriesBeyondTheLimit()
    {
        var cache = CreateCache();
        cache.LimitBytes = 250;
        var first = cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache, 100));
        Thread.Sleep(20);
        var second = cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache, 100));
        Thread.Sleep(20);
        cache.Find(SourceIdentity.Of(first.SourcePath)!.Value, 50);
        Thread.Sleep(20);

        var third = cache.Add(Describe(CreateSource("c.mp4")), WriteProxy(cache, 100));

        Assert.Equal(2, cache.Count);
        Assert.True(File.Exists(cache.GetFilePath(first)));
        Assert.False(File.Exists(cache.GetFilePath(second)));
        Assert.True(File.Exists(cache.GetFilePath(third)));
    }

    [Fact]
    public void AnEntryLargerThanTheLimitStaysAlone()
    {
        var cache = CreateCache();
        cache.LimitBytes = 50;
        cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache, 100));

        var big = cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache, 100));

        Assert.Equal(big.Id, cache.Snapshot().Single().Id);
    }

    [Fact]
    public void TrimAppliesANewLimit()
    {
        var cache = CreateCache();
        cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache, 100));
        Thread.Sleep(20);
        cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache, 100));
        cache.LimitBytes = 150;

        Assert.Equal(1, cache.Trim());
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.Trim());
    }

    [Fact]
    public void ACacheExactlyAtTheLimitIsNotTrimmed()
    {
        var cache = CreateCache();
        cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache, 100));
        cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache, 100));
        cache.LimitBytes = 200;

        Assert.Equal(0, cache.Trim());
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TrimSkipsAFileThatCannotBeDeleted()
    {
        var cache = CreateCache();
        var locked = cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache, 100));
        Thread.Sleep(20);
        var second = cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache, 100));
        cache.LimitBytes = 150;

        using (File.Open(cache.GetFilePath(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, cache.Trim());
        }

        Assert.True(File.Exists(cache.GetFilePath(locked)));
        Assert.False(File.Exists(cache.GetFilePath(second)));
    }

    [Fact]
    public void RemoveDeletesTheFileAndTheEntry()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));

        Assert.True(cache.Remove(added.Id));
        Assert.False(File.Exists(cache.GetFilePath(added)));
        Assert.Null(cache.Find(source, 50));
        Assert.False(cache.Remove(added.Id));
    }

    [Fact]
    public void RemoveKeepsAnEntryWhoseFileIsLocked()
    {
        var cache = CreateCache();
        var added = cache.Add(Describe(CreateSource()), WriteProxy(cache));

        using (File.Open(cache.GetFilePath(added), FileMode.Open, FileAccess.Read, FileShare.None))
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
        var first = cache.Add(Describe(CreateSource("a.mp4")), WriteProxy(cache));
        var second = cache.Add(Describe(CreateSource("b.mp4")), WriteProxy(cache));

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Count);
        Assert.False(File.Exists(cache.GetFilePath(first)));
        Assert.False(File.Exists(cache.GetFilePath(second)));
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
        var added = cache.Add(Describe(source), WriteProxy(cache));
        cache.AddSkip(skipped, ProxyCacheSkipReason.Transparent);

        var reloaded = CreateCache();

        var found = reloaded.Find(source, 50);
        Assert.NotNull(found);
        Assert.Equal(added.Id, found.Id);
        Assert.Equal(added.DisplayLeft, found.DisplayLeft);
        Assert.Equal(added.DurationTicks, found.DurationTicks);
        Assert.Equal(added.FrameCount, found.FrameCount);
        Assert.True(reloaded.IsSkipped(skipped));
    }

    [Fact]
    public void LoadingDropsEntriesWhoseFilesAreGone()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));
        File.Delete(cache.GetFilePath(added));

        var reloaded = CreateCache();

        Assert.Equal(0, reloaded.Count);
        Assert.DoesNotContain(added.Id.ToString("N"), File.ReadAllText(reloaded.IndexPath));
    }

    [Fact]
    public void LoadingDeletesLeftoversAndOrphans()
    {
        var cache = CreateCache();
        var added = cache.Add(Describe(CreateSource()), WriteProxy(cache));
        var leftover = cache.CreateTemporaryPath();
        File.WriteAllBytes(leftover, new byte[10]);
        var orphan = Path.Combine(cache.DirectoryPath, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(orphan, new byte[10]);
        var unrelated = Path.Combine(cache.DirectoryPath, "notes.txt");
        File.WriteAllText(unrelated, "keep");

        var reloaded = CreateCache();

        Assert.Equal(1, reloaded.Count);
        Assert.True(File.Exists(cache.GetFilePath(added)));
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void ACorruptIndexIsReplacedAndItsFilesAreDiscarded()
    {
        var cache = CreateCache();
        var added = cache.Add(Describe(CreateSource()), WriteProxy(cache));
        File.WriteAllText(cache.IndexPath, "{ not json");

        var reloaded = CreateCache();

        Assert.Equal(0, reloaded.Count);
        Assert.False(File.Exists(cache.GetFilePath(added)));
        Assert.Contains("\"Entries\"", File.ReadAllText(reloaded.IndexPath));
    }

    [Fact]
    public void AnIndexWithBrokenRecordsKeepsTheGoodOnes()
    {
        var cache = CreateCache();
        var source = CreateSource();
        var added = cache.Add(Describe(source), WriteProxy(cache));
        var text = File.ReadAllText(cache.IndexPath);
        text = text.Replace("\"Entries\": [", "\"Entries\": [ null, { \"Id\": \"00000000000000000000000000000000\" }, { \"Id\": \"" + Guid.NewGuid().ToString("N") + "\", \"SourcePath\": \"\" },");
        File.WriteAllText(cache.IndexPath, text);

        var reloaded = CreateCache();

        Assert.Equal(added.Id, reloaded.Snapshot().Single().Id);
    }

    [Fact]
    public void SnapshotsAreCopies()
    {
        var cache = CreateCache();
        var source = CreateSource();
        cache.Add(Describe(source), WriteProxy(cache));

        cache.Snapshot()[0].Scale = 99;

        Assert.Equal(50, cache.Snapshot()[0].Scale);
        Assert.NotNull(cache.Find(source, 50));
    }

    [Fact]
    public void AddRejectsAnEmptyTemporaryPath()
    {
        var cache = CreateCache();

        Assert.Throws<ArgumentException>(() => cache.Add(Describe(CreateSource()), string.Empty));
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
