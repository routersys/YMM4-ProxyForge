using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.ViewModels;

internal sealed class ProxyForgeSettingsViewModel : Bindable
{
    readonly ProxyCache cache;
    readonly ProxyGenerationQueue queue;
    readonly Func<bool> isPluginFirst;
    readonly Action movePluginToFront;
    readonly Func<bool> isFFmpegAvailable;
    bool isPluginFirstValue;
    string cacheSummary = string.Empty;
    string generationText = string.Empty;
    ProxyCacheEntryViewModel? selectedEntry;

    public ProxyForgeSettingsViewModel()
        : this(ProxyForgeSettings.Default, ProxyCache.Shared, ProxyGenerationQueue.Shared, PluginOrder.IsPluginFirst, PluginOrder.MovePluginToFront, () => FFmpegRuntime.Executables.IsAvailable)
    {
    }

    public ProxyForgeSettingsViewModel(
        ProxyForgeSettings settings,
        ProxyCache cache,
        ProxyGenerationQueue queue,
        Func<bool> isPluginFirst,
        Action movePluginToFront,
        Func<bool> isFFmpegAvailable)
    {
        Settings = settings;
        this.cache = cache;
        this.queue = queue;
        this.isPluginFirst = isPluginFirst;
        this.movePluginToFront = movePluginToFront;
        this.isFFmpegAvailable = isFFmpegAvailable;

        MoveToFrontCommand = new ActionCommand(_ => !IsPluginFirst, _ => MoveToFront());
        RefreshCommand = new ActionCommand(_ => true, _ => Refresh());
        OpenDirectoryCommand = new ActionCommand(_ => true, _ => OpenDirectory());
        RemoveEntryCommand = new ActionCommand(_ => SelectedEntry is not null, _ => RemoveEntry());
        ClearCommand = new ActionCommand(_ => Entries.Count > 0, _ => Clear());

        PropertyChangedEventManager.AddHandler(settings, OnSettingsChanged, nameof(ProxyForgeSettings.CacheLimitGigabytes));
        CollectionChangedEventManager.AddHandler(queue.Items, OnGenerationsChanged);
        Refresh();
    }

    public ProxyForgeSettings Settings { get; }

    public ObservableCollection<ProxyCacheEntryViewModel> Entries { get; } = [];

    public ICommand MoveToFrontCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenDirectoryCommand { get; }

    public ICommand RemoveEntryCommand { get; }

    public ICommand ClearCommand { get; }

    public bool IsPluginFirst
    {
        get => isPluginFirstValue;
        private set => Set(ref isPluginFirstValue, value, nameof(IsPluginFirst), nameof(PluginOrderText));
    }

    public string PluginOrderText => IsPluginFirst ? Texts.PluginOrderFirst : Texts.PluginOrderNotFirst;

    public string FFmpegText => isFFmpegAvailable() ? Texts.FFmpegAvailable : Texts.FFmpegUnavailable;

    public string CacheSummary
    {
        get => cacheSummary;
        private set => Set(ref cacheSummary, value);
    }

    public string GenerationText
    {
        get => generationText;
        private set => Set(ref generationText, value);
    }

    public ProxyCacheEntryViewModel? SelectedEntry
    {
        get => selectedEntry;
        set => Set(ref selectedEntry, value);
    }

    public void Refresh()
    {
        IsPluginFirst = isPluginFirst();
        OnPropertyChanged(nameof(FFmpegText));
        UpdateGenerationText();

        var entries = cache.Snapshot();
        Entries.Clear();
        foreach (var entry in entries.OrderByDescending(entry => entry.LastUsedTicks))
            Entries.Add(new ProxyCacheEntryViewModel(entry));

        SelectedEntry = null;
        CacheSummary = string.Format(Texts.CacheSummaryFormat, Entries.Count, ByteText.Format(entries.Sum(entry => entry.FileLength)));
    }

    void MoveToFront()
    {
        movePluginToFront();
        IsPluginFirst = isPluginFirst();
    }

    void OpenDirectory()
    {
        try
        {
            Directory.CreateDirectory(cache.DirectoryPath);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cache.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ProxyForgeTelemetry.Report(exception);
            Log.Default.Write("ProxyForge: キャッシュのフォルダーを開けませんでした。", exception);
        }
    }

    void RemoveEntry()
    {
        if (SelectedEntry is not { } entry)
            return;

        cache.Remove(entry.Id);
        Refresh();
    }

    void Clear()
    {
        cache.Clear();
        queue.ForgetFailures();
        Refresh();
    }

    void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        cache.Trim();
        Refresh();
    }

    void OnGenerationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateGenerationText();

    void UpdateGenerationText() => GenerationText = string.Format(Texts.GenerationCountFormat, queue.Items.Count);
}
