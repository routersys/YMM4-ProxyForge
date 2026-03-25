using System.Collections.ObjectModel;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using ZeroDiskProxy.Cache;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Localization;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Utilities;

namespace ZeroDiskProxy.ViewModels;

internal sealed class SettingsViewModel : Bindable
{
    private readonly ProxyCacheManager? _cacheManager;
    private readonly MemoryBudget? _budget;
    private readonly VideoCacheDatabase? _videoCache;
    private readonly IDialogService _dialogService;

    private string _cacheSummaryText = Translate.CacheSummaryLoading;
    public string CacheSummaryText
    {
        get => _cacheSummaryText;
        set => Set(ref _cacheSummaryText, value);
    }

    private string _memoryUsageText = "";
    public string MemoryUsageText
    {
        get => _memoryUsageText;
        set => Set(ref _memoryUsageText, value);
    }

    private bool _hasCacheEntries;
    public bool HasCacheEntries
    {
        get => _hasCacheEntries;
        set => Set(ref _hasCacheEntries, value);
    }

    public ObservableCollection<CacheEntryViewModel> CacheEntries { get; } = [];

    private string _videoCacheSummaryText = "";
    public string VideoCacheSummaryText
    {
        get => _videoCacheSummaryText;
        set => Set(ref _videoCacheSummaryText, value);
    }

    private bool _hasVideoCacheEntries;
    public bool HasVideoCacheEntries
    {
        get => _hasVideoCacheEntries;
        set => Set(ref _hasVideoCacheEntries, value);
    }

    public ObservableCollection<VideoCacheEntryViewModel> VideoCacheEntries { get; } = [];

    public ICommand ClearCacheCommand { get; }
    public ICommand RefreshCacheInfoCommand { get; }
    public ICommand RemoveCacheEntryCommand { get; }
    public ICommand ClearVideoCacheCommand { get; }
    public ICommand RemoveVideoCacheEntryCommand { get; }

    internal SettingsViewModel(ProxyCacheManager? cacheManager, MemoryBudget? budget, VideoCacheDatabase? videoCache, IDialogService dialogService)
    {
        _cacheManager = cacheManager;
        _budget = budget;
        _videoCache = videoCache;
        _dialogService = dialogService;
        ClearCacheCommand = new ActionCommand(ExecuteClearCache);
        RefreshCacheInfoCommand = new ActionCommand(ExecuteRefreshCacheInfo);
        RemoveCacheEntryCommand = new ActionCommand<CacheEntryViewModel>(ExecuteRemoveEntry);
        ClearVideoCacheCommand = new ActionCommand(ExecuteClearVideoCache);
        RemoveVideoCacheEntryCommand = new ActionCommand<VideoCacheEntryViewModel>(ExecuteRemoveVideoCacheEntry);
        ExecuteRefreshCacheInfo();
    }

    private void ExecuteClearCache()
    {
        if (_cacheManager is null)
            return;

        var (count, totalSize) = _cacheManager.ClearAll();
        if (count == 0)
        {
            _dialogService.ShowInformation(Translate.CacheNoEntriesToDelete, Translate.AppName);
        }
        else
        {
            _dialogService.ShowInformation(
                string.Format(Translate.CacheDeleteSummaryFormat, count.ToString("N0"), ByteFormatter.Format(totalSize)),
                Translate.AppName);
        }

        ExecuteRefreshCacheInfo();
    }

    private void ExecuteRefreshCacheInfo()
    {
        if (_cacheManager is null)
        {
            CacheSummaryText = Translate.CacheSummaryPluginNotInitialized;
            MemoryUsageText = "";
            HasCacheEntries = false;
            return;
        }

        var (count, memSize, diskSize, memCount, diskCount) = _cacheManager.GetCacheInfo();
        CacheSummaryText = string.Format(Translate.CacheSummaryCountFormat, count.ToString("N0"));

        var usageText = string.Format(
            Translate.CacheUsageMemoryAndDiskFormat,
            memCount.ToString("N0"),
            ByteFormatter.Format(memSize),
            diskCount.ToString("N0"),
            ByteFormatter.Format(diskSize));

        if (_budget is not null)
            usageText = string.Concat(
                usageText,
                string.Format(Translate.CacheUsageAllocatedFormat, ByteFormatter.Format(_budget.AllocatedBytes)));

        MemoryUsageText = usageText;

        CacheEntries.Clear();
        var snapshots = _cacheManager.GetAllSnapshots();
        foreach (var snap in snapshots)
            CacheEntries.Add(new CacheEntryViewModel(snap));

        HasCacheEntries = CacheEntries.Count > 0;

        RefreshVideoCacheInfo();
    }

    private void ExecuteRemoveEntry(CacheEntryViewModel? entry)
    {
        if (entry is null || _cacheManager is null)
            return;

        _cacheManager.RemoveProxy(entry.OriginalPath, entry.Scale);
        ExecuteRefreshCacheInfo();
    }

    private void ExecuteClearVideoCache()
    {
        if (_videoCache is null)
            return;

        var count = _videoCache.ClearAll();
        if (count == 0)
            _dialogService.ShowInformation(Translate.VideoCacheListEmpty, Translate.AppName);
        else
            _dialogService.ShowInformation(
                string.Format(Translate.CacheDeleteSummaryFormat, count.ToString("N0"), ""),
                Translate.AppName);

        ExecuteRefreshCacheInfo();
    }

    private void ExecuteRemoveVideoCacheEntry(VideoCacheEntryViewModel? entry)
    {
        if (entry is null || _videoCache is null)
            return;

        _videoCache.Remove(entry.Uuid);
        ExecuteRefreshCacheInfo();
    }

    private void RefreshVideoCacheInfo()
    {
        VideoCacheEntries.Clear();

        if (_videoCache is null)
        {
            VideoCacheSummaryText = "";
            HasVideoCacheEntries = false;
            return;
        }

        var entries = _videoCache.GetAll();
        long totalSize = 0;
        foreach (var e in entries)
        {
            totalSize += e.FileSize;
            VideoCacheEntries.Add(new VideoCacheEntryViewModel(e));
        }

        VideoCacheSummaryText = string.Format(
            Translate.VideoCacheSummaryFormat,
            entries.Length.ToString("N0"),
            ByteFormatter.Format(totalSize));

        HasVideoCacheEntries = entries.Length > 0;
    }
}

internal sealed class CacheEntryViewModel(CacheEntrySnapshot snapshot) : Bindable
{
    public string FileName { get; } = snapshot.FileName;
    public string OriginalPath { get; } = snapshot.OriginalPath;
    public float Scale { get; } = snapshot.Scale;
    public string Resolution { get; } = string.Create(null, stackalloc char[24], $"{snapshot.ProxyWidth}\u00d7{snapshot.ProxyHeight}");
    public bool IsInMemory { get; } = snapshot.IsInMemory;
    public string StorageType { get; } = snapshot.IsInMemory ? Translate.StorageMemory : Translate.StorageDisk;
    public long DataSize { get; } = snapshot.DataSize;
    public string DataSizeText { get; } = ByteFormatter.Format(snapshot.DataSize);
    public string CreatedAtText { get; } = snapshot.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    public string LastAccessedText { get; } = snapshot.LastAccessedAt.ToLocalTime().ToString("HH:mm:ss");
    public string ScaleText { get; } = string.Create(null, stackalloc char[8], $"{snapshot.Scale * 100:F0}%");
}

internal sealed class VideoCacheEntryViewModel(VideoCacheLookupResult entry) : Bindable
{
    public Guid Uuid { get; } = entry.Uuid;
    public string FileName { get; } = entry.FileName;
    public string OriginalPath { get; } = entry.OriginalPath;
    public float Scale { get; } = entry.Scale;
    public string Resolution { get; } = entry.ProxyWidth > 0
        ? string.Create(null, stackalloc char[24], $"{entry.ProxyWidth}\u00d7{entry.ProxyHeight}")
        : "—";
    public string StorageType { get; } = Translate.StoragePersistent;
    public long DataSize { get; } = entry.FileSize;
    public string DataSizeText { get; } = ByteFormatter.Format(entry.FileSize);
    public string CreatedAtText { get; } = entry.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string LastAccessedText { get; } = entry.LastAccessedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string ScaleText { get; } = entry.Scale > 0
        ? string.Create(null, stackalloc char[8], $"{entry.Scale * 100:F0}%")
        : "—";
}

internal sealed class ActionCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

internal sealed class ActionCommand<T>(Action<T?> execute) : ICommand where T : class
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter as T);
}