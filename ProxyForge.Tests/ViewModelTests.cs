using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using ProxyForge.ViewModels;
using ProxyForge.Views;

namespace ProxyForge.Tests;

public sealed class ByteTextTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(-5L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1024L * 1024L, "1 MB")]
    [InlineData(544_320L, "531.56 KB")]
    [InlineData(10L * 1024L * 1024L * 1024L, "10 GB")]
    [InlineData(1024L * 1024L * 1024L * 1024L * 3L, "3 TB")]
    [InlineData(1024L * 1024L * 1024L * 1024L * 1024L * 2L, "2048 TB")]
    public void FormatsWithTheLargestFittingUnit(long bytes, string expected)
        => Assert.Equal(expected, ByteText.Format(bytes));
}

public sealed class PluginOrderTests
{
    const string Name = "ProxyForge.ProxyForgeVideoFileSourcePlugin";

    [Fact]
    public void ThePluginNameIsTheFullTypeName()
        => Assert.Equal(Name, PluginOrder.PluginName);

    [Fact]
    public void AnEmptyOrderIsNotFirst()
        => Assert.False(PluginOrder.IsFirst([], Name));

    [Fact]
    public void TheFirstEntryDecides()
    {
        Assert.True(PluginOrder.IsFirst([Name, "Other"], Name));
        Assert.False(PluginOrder.IsFirst(["Other", Name], Name));
        Assert.False(PluginOrder.IsFirst([Name.ToUpperInvariant()], Name));
    }

    [Fact]
    public void MoveToFrontPutsTheNameFirstAndKeepsTheRest()
    {
        var moved = PluginOrder.MoveToFront(["A", Name, "B"], Name);

        Assert.Equal([Name, "A", "B"], moved);
    }

    [Fact]
    public void MoveToFrontAddsAMissingName()
    {
        var moved = PluginOrder.MoveToFront(["A", "B"], Name);

        Assert.Equal([Name, "A", "B"], moved);
    }

    [Fact]
    public void MoveToFrontRemovesDuplicates()
    {
        var moved = PluginOrder.MoveToFront([Name, "A", Name], Name);

        Assert.Equal([Name, "A"], moved);
    }

    [Fact]
    public void MoveToFrontLeavesAnOrderThatIsAlreadyRightUnchanged()
    {
        ImmutableList<string> order = [Name, "A"];

        Assert.Equal(order, PluginOrder.MoveToFront(order, Name));
    }
}

public sealed class ProxyCacheEntryViewModelTests
{
    [Fact]
    public void FormatsTheEntryForDisplay()
    {
        var lastUsed = new DateTime(2026, 9, 13, 1, 2, 0, DateTimeKind.Utc);
        var entry = new ProxyCacheEntry
        {
            Id = Guid.NewGuid(),
            SourcePath = @"C:\videos\clip.mp4",
            Scale = 50,
            ProxyWidth = 960,
            ProxyHeight = 540,
            FileLength = 1536,
            FrameCount = 250,
            ChunkLength = 100,
            Chunks = [0, 2],
            LastUsedTicks = lastUsed.Ticks,
        };

        var viewModel = new ProxyCacheEntryViewModel(entry);

        Assert.Equal(entry.Id, viewModel.Id);
        Assert.Equal(@"C:\videos\clip.mp4", viewModel.SourcePath);
        Assert.Equal("clip.mp4", viewModel.FileName);
        Assert.Equal("960×540", viewModel.Resolution);
        Assert.Equal("50%", viewModel.Scale);
        Assert.Equal(1536L, viewModel.Bytes);
        Assert.Equal("1.5 KB", viewModel.Size);
        Assert.Equal("2/3", viewModel.Chunks);
        Assert.Equal(lastUsed.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), viewModel.LastUsed);
    }
}

public sealed class GenerationItemViewModelTests
{
    [Fact]
    public void MirrorsTheItem()
    {
        var item = new ProxyGenerationItem(@"C:\videos\clip.mp4", 50);
        var viewModel = new GenerationItemViewModel(item);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("clip.mp4", viewModel.FileName);
        Assert.Equal(@"C:\videos\clip.mp4", viewModel.SourcePath);
        Assert.True(viewModel.IsWaiting);
        Assert.Equal(Texts.StatusWaiting, viewModel.StatusText);
        Assert.Equal(0d, viewModel.Percentage);

        item.Progress = 0.25d;
        Assert.Equal(25d, viewModel.Percentage);
        Assert.Contains(nameof(GenerationItemViewModel.Percentage), raised);

        item.Status = ProxyGenerationStatus.Generating;
        Assert.False(viewModel.IsWaiting);
        Assert.Equal(Texts.StatusGenerating, viewModel.StatusText);
        Assert.Contains(nameof(GenerationItemViewModel.StatusText), raised);
        Assert.Contains(nameof(GenerationItemViewModel.IsWaiting), raised);
    }

    [Theory]
    [InlineData(ProxyGenerationStatus.Completed, null)]
    [InlineData(ProxyGenerationStatus.Cancelled, null)]
    [InlineData(ProxyGenerationStatus.Failed, null)]
    [InlineData(ProxyGenerationStatus.Failed, ProxyEncodeFailure.Transparent)]
    public void DescribesEveryStatus(ProxyGenerationStatus status, ProxyEncodeFailure? failure)
    {
        var item = new ProxyGenerationItem(@"C:\videos\clip.mp4", 50) { Failure = failure, Status = status };

        var text = new GenerationItemViewModel(item).StatusText;

        var expected = status switch
        {
            ProxyGenerationStatus.Completed => Texts.StatusCompleted,
            ProxyGenerationStatus.Cancelled => Texts.StatusCancelled,
            _ => failure is null ? Texts.StatusFailed : string.Concat(Texts.StatusFailed, ": ", Texts.FailureTransparent),
        };
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(ProxyEncodeFailure.FFmpegUnavailable)]
    [InlineData(ProxyEncodeFailure.SourceUnavailable)]
    [InlineData(ProxyEncodeFailure.NoFrames)]
    [InlineData(ProxyEncodeFailure.UnusableSize)]
    [InlineData(ProxyEncodeFailure.Transparent)]
    [InlineData(ProxyEncodeFailure.FFmpegFailed)]
    [InlineData(ProxyEncodeFailure.NoOutput)]
    [InlineData(ProxyEncodeFailure.SourceChanged)]
    public void EveryFailureHasALocalisedDescription(ProxyEncodeFailure failure)
    {
        var text = GenerationItemViewModel.DescribeFailure(failure);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(failure.ToString(), text);
    }

    [Fact]
    public void EveryFailureIsDistinct()
    {
        var texts = Enum.GetValues<ProxyEncodeFailure>().Select(GenerationItemViewModel.DescribeFailure).ToArray();

        Assert.Equal(texts.Length, texts.Distinct().Count());
    }
}

public sealed class GenerationProgressViewModelTests
{
    [Fact]
    public void FollowsTheSourceCollection()
    {
        var source = new ObservableCollection<ProxyGenerationItem>();
        var first = new ProxyGenerationItem(@"C:\a.mp4", 50);
        source.Add(first);
        var viewModel = new GenerationProgressViewModel(source, () => { });
        var raised = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GenerationProgressViewModel.HasItems))
                raised++;
        };

        Assert.True(viewModel.HasItems);
        Assert.Same(first, Assert.Single(viewModel.Items).Item);

        var second = new ProxyGenerationItem(@"C:\b.mp4", 50);
        source.Add(second);
        Assert.Equal(2, viewModel.Items.Count);
        Assert.Same(second, viewModel.Items[1].Item);

        source.Remove(first);
        Assert.Same(second, Assert.Single(viewModel.Items).Item);

        source.Clear();
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.HasItems);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void TheCancelCommandCancelsEverythingWhileItemsExist()
    {
        var source = new ObservableCollection<ProxyGenerationItem>();
        var cancelled = 0;
        var viewModel = new GenerationProgressViewModel(source, () => cancelled++);

        Assert.False(viewModel.CancelCommand.CanExecute(null));

        source.Add(new ProxyGenerationItem(@"C:.mp4", 50));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.CancelCommand.Execute(null);

        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void InsertsAtTheSourcePosition()
    {
        var source = new ObservableCollection<ProxyGenerationItem> { new(@"C:\a.mp4", 50), new(@"C:\c.mp4", 50) };
        var viewModel = new GenerationProgressViewModel(source, () => { });

        var inserted = new ProxyGenerationItem(@"C:\b.mp4", 50);
        source.Insert(1, inserted);

        Assert.Same(inserted, viewModel.Items[1].Item);
    }
}

public sealed class GenerationProgressWindowHostTests
{
    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(1, true, false, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, true, false)]
    [InlineData(0, false, true, false)]
    public void TheWindowShowsOnlyForItemsWhenEnabledAndNotHidden(int count, bool enabled, bool hidden, bool expected)
        => Assert.Equal(expected, GenerationProgressWindowHost.ShouldShow(count, enabled, hidden));
}

public sealed class ProxyForgeSettingsViewModelTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ProxyForgeTests", Guid.NewGuid().ToString("N"));
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly ProxyForgeSettings settings = new();
    bool first;
    bool ffmpeg = true;
    int moved;

    public ProxyForgeSettingsViewModelTests()
    {
        Directory.CreateDirectory(root);
        cache = new ProxyCache(Path.Combine(root, "cache"));
        queue = new ProxyGenerationQueue(cache, (_, _) => throw new ProxyEncodeException(ProxyEncodeFailure.FFmpegFailed, "boom"), () => new ProxyEncodeOptions(50, 30, 10, false), () => ExportPhase.Idle, new SourceFocus(), _ => { }, TestUiThread.Post);
    }

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

    ProxyForgeSettingsViewModel Create()
        => new(settings, cache, queue, () => first, () => { moved++; first = true; }, () => ffmpeg);

    ProxyCacheEntry Seed(string name, int length)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[10]);
        var identity = SourceIdentity.Of(path)!.Value;
        var temporary = cache.CreateTemporaryPath();
        File.WriteAllBytes(temporary, new byte[length]);
        var registered = cache.Register(new ProxyCacheEntry
        {
            SourcePath = identity.Path,
            SourceLength = identity.Length,
            SourceWriteTimeTicks = identity.WriteTimeTicks,
            Scale = 50,
            ProxyWidth = 2,
            ProxyHeight = 2,
            DisplayWidth = 4f,
            DisplayHeight = 4f,
            FrameRateNumerator = 30,
            FrameRateDenominator = 1,
            DurationTicks = 1,
            FrameCount = 2,
            ChunkLength = 1,
        });
        return cache.AddChunk(registered.Id, 0, temporary)!;
    }

    [Fact]
    public void ReportsTheOrderTheFFmpegAndTheCache()
    {
        Seed("a.mp4", 100);
        Seed("b.mp4", 200);

        var viewModel = Create();

        Assert.False(viewModel.IsPluginFirst);
        Assert.Equal(Texts.PluginOrderNotFirst, viewModel.PluginOrderText);
        Assert.Equal(Texts.FFmpegAvailable, viewModel.FFmpegText);
        Assert.Equal(string.Format(Texts.GenerationCountFormat, 0), viewModel.GenerationText);
        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Equal(string.Format(Texts.CacheSummaryFormat, 2, "300 B"), viewModel.CacheSummary);
        Assert.Same(viewModel.Settings, settings);
    }

    [Fact]
    public void ListsTheMostRecentlyUsedFirst()
    {
        var older = Seed("a.mp4", 100);
        Thread.Sleep(20);
        var newer = Seed("b.mp4", 100);

        var viewModel = Create();

        Assert.Equal(newer.Id, viewModel.Entries[0].Id);
        Assert.Equal(older.Id, viewModel.Entries[1].Id);
    }

    [Fact]
    public void MoveToFrontUsesTheHostSettingsAndUpdatesTheText()
    {
        var viewModel = Create();
        Assert.True(viewModel.MoveToFrontCommand.CanExecute(null));

        viewModel.MoveToFrontCommand.Execute(null);

        Assert.Equal(1, moved);
        Assert.True(viewModel.IsPluginFirst);
        Assert.Equal(Texts.PluginOrderFirst, viewModel.PluginOrderText);
        Assert.False(viewModel.MoveToFrontCommand.CanExecute(null));
    }

    [Fact]
    public void AMissingFFmpegIsReported()
    {
        ffmpeg = false;

        Assert.Equal(Texts.FFmpegUnavailable, Create().FFmpegText);
    }

    [Fact]
    public void RemoveDeletesTheSelectedEntry()
    {
        var kept = Seed("a.mp4", 100);
        var removed = Seed("b.mp4", 100);
        var viewModel = Create();
        Assert.False(viewModel.RemoveEntryCommand.CanExecute(null));
        viewModel.SelectedEntry = viewModel.Entries.Single(entry => entry.Id == removed.Id);
        Assert.True(viewModel.RemoveEntryCommand.CanExecute(null));

        viewModel.RemoveEntryCommand.Execute(null);

        Assert.Equal(kept.Id, Assert.Single(viewModel.Entries).Id);
        Assert.Null(viewModel.SelectedEntry);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task ClearDeletesEverythingAndForgetsFailures()
    {
        Seed("a.mp4", 100);
        var failedPath = Path.Combine(root, "failed.mp4");
        File.WriteAllBytes(failedPath, new byte[10]);
        var failed = SourceIdentity.Of(failedPath)!.Value;
        queue.TryEnqueue(failed, 50);
        await queue.WhenIdleAsync();
        Assert.True(queue.HasFailed(failed, 50));
        var viewModel = Create();
        Assert.True(viewModel.ClearCommand.CanExecute(null));

        viewModel.ClearCommand.Execute(null);

        Assert.Empty(viewModel.Entries);
        Assert.Equal(0, cache.Count);
        Assert.False(queue.HasFailed(failed, 50));
        Assert.False(viewModel.ClearCommand.CanExecute(null));
        Assert.Equal(string.Format(Texts.CacheSummaryFormat, 0, "0 B"), viewModel.CacheSummary);
    }

    [Fact]
    public void RefreshPicksUpExternalChanges()
    {
        var viewModel = Create();
        Assert.Empty(viewModel.Entries);
        Seed("a.mp4", 100);

        viewModel.RefreshCommand.Execute(null);

        Assert.Single(viewModel.Entries);
    }

    [Fact]
    public void LoweringTheLimitTrimsTheCacheAndRefreshes()
    {
        Seed("a.mp4", 100);
        Thread.Sleep(20);
        Seed("b.mp4", 100);
        var viewModel = Create();
        Assert.Equal(2, viewModel.Entries.Count);
        cache.LimitBytes = 150;

        settings.CacheLimitGigabytes = settings.CacheLimitGigabytes + 1;

        Assert.Single(viewModel.Entries);
    }

    [Fact]
    public void TheGenerationCountFollowsTheQueue()
    {
        var viewModel = Create();

        queue.Items.Add(new ProxyGenerationItem(@"C:\a.mp4", 50));

        Assert.Equal(string.Format(Texts.GenerationCountFormat, 1), viewModel.GenerationText);
    }
}
