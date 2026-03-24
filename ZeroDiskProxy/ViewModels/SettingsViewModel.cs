using System.Collections.ObjectModel;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Plugin;

namespace ZeroDiskProxy.ViewModels;

internal sealed class SettingsViewModel : Bindable
{
    private string _cacheSummaryText = "読み込み中...";
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

    internal SettingsViewModel()
    {
        ClearCacheCommand = new ActionCommand(ExecuteClearCache);
        RefreshCacheInfoCommand = new ActionCommand(ExecuteRefreshCacheInfo);
        RemoveCacheEntryCommand = new ActionCommand<CacheEntryViewModel>(ExecuteRemoveEntry);
        ExecuteRefreshCacheInfo();
    }

    private void ExecuteClearCache()
    {
        var manager = PluginHost.Instance?.CacheManager;
        if (manager is null)
            return;

        var (count, totalSize) = manager.ClearAll();
        if (count == 0)
        {
            System.Windows.MessageBox.Show(
                "削除するキャッシュがありません。",
                "ZeroDiskProxy",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(
                string.Concat(count.ToString("N0"), " 件 (", FormatBytes(totalSize), ") を削除しました。"),
                "ZeroDiskProxy",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        ExecuteRefreshCacheInfo();
    }

    private void ExecuteRefreshCacheInfo()
    {
        var manager = PluginHost.Instance?.CacheManager;
        if (manager is null)
        {
            CacheSummaryText = "プラグイン未初期化";
            MemoryUsageText = "";
            HasCacheEntries = false;
            return;
        }

        var (count, memSize, diskSize, memCount, diskCount) = manager.GetCacheInfo();
        CacheSummaryText = string.Concat("キャッシュ数: ", count.ToString("N0"), " 件");

        var usageText = string.Concat(
            "メモリ: ", memCount.ToString("N0"), " 件 (", FormatBytes(memSize),
            ")   ディスク: ", diskCount.ToString("N0"), " 件 (", FormatBytes(diskSize), ")");

        var budget = PluginHost.Instance?.Budget;
        if (budget is not null)
            usageText = string.Concat(usageText, "   割当済: ", FormatBytes(budget.AllocatedBytes));

        MemoryUsageText = usageText;

        CacheEntries.Clear();
        var snapshots = manager.GetAllSnapshots();
        foreach (var snap in snapshots)
            CacheEntries.Add(new CacheEntryViewModel(snap));

        HasCacheEntries = CacheEntries.Count > 0;
    }

    private void ExecuteRemoveEntry(CacheEntryViewModel? entry)
    {
        if (entry is null)
            return;

        var manager = PluginHost.Instance?.CacheManager;
        if (manager is null)
            return;

        manager.RemoveProxy(entry.OriginalPath, entry.Scale);
        ExecuteRefreshCacheInfo();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return string.Concat(len.ToString("0.##"), " ", sizes[order]);
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
        StorageType = snapshot.IsInMemory ? "メモリ" : "ディスク";
        DataSize = snapshot.DataSize;
        DataSizeText = FormatBytes(snapshot.DataSize);
        CreatedAtText = snapshot.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        LastAccessedText = snapshot.LastAccessedAt.ToLocalTime().ToString("HH:mm:ss");
        ScaleText = string.Concat((snapshot.Scale * 100).ToString("F0"), "%");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int order = 0;
        while (value >= 1024 && order < units.Length - 1)
        {
            order++;
            value /= 1024;
        }
        return string.Concat(value.ToString("0.##"), " ", units[order]);
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