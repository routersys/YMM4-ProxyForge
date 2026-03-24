using System.Collections.ObjectModel;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
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

    public ICommand ClearCacheCommand { get; }
    public ICommand RefreshCacheInfoCommand { get; }
    public ICommand RemoveCacheEntryCommand { get; }

    internal SettingsViewModel(ProxyCacheManager? cacheManager, MemoryBudget? budget, IDialogService dialogService)
    {
        _cacheManager = cacheManager;
        _budget = budget;
        _dialogService = dialogService;
        ClearCacheCommand = new ActionCommand(ExecuteClearCache);
        RefreshCacheInfoCommand = new ActionCommand(ExecuteRefreshCacheInfo);
        RemoveCacheEntryCommand = new ActionCommand<CacheEntryViewModel>(ExecuteRemoveEntry);
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
    }

    private void ExecuteRemoveEntry(CacheEntryViewModel? entry)
    {
        if (entry is null || _cacheManager is null)
            return;

        _cacheManager.RemoveProxy(entry.OriginalPath, entry.Scale);
        ExecuteRefreshCacheInfo();
    }
}

internal sealed class CacheEntryViewModel : Bindable
{
    public string FileName { get; }
    public string OriginalPath { get; }
    public float Scale { get; }
    public string Resolution { get; }
    public bool IsInMemory { get; }
    public string StorageType { get; }
    public long DataSize { get; }
    public string DataSizeText { get; }
    public string CreatedAtText { get; }
    public string LastAccessedText { get; }
    public string ScaleText { get; }

    internal CacheEntryViewModel(CacheEntrySnapshot snapshot)
    {
        FileName = snapshot.FileName;
        OriginalPath = snapshot.OriginalPath;
        Scale = snapshot.Scale;
        Resolution = string.Concat(snapshot.ProxyWidth.ToString(), "×", snapshot.ProxyHeight.ToString());
        IsInMemory = snapshot.IsInMemory;
        StorageType = snapshot.IsInMemory ? Translate.StorageMemory : Translate.StorageDisk;
        DataSize = snapshot.DataSize;
        DataSizeText = ByteFormatter.Format(snapshot.DataSize);
        CreatedAtText = snapshot.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        LastAccessedText = snapshot.LastAccessedAt.ToLocalTime().ToString("HH:mm:ss");
        ScaleText = string.Concat((snapshot.Scale * 100).ToString("F0"), "%");
    }
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