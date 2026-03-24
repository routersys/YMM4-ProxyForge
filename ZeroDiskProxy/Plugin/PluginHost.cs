using System.IO;
using System.Windows;
using System.Windows.Interop;
using YukkuriMovieMaker.Commons;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Detection;
using ZeroDiskProxy.Interfaces;
using ZeroDiskProxy.Memory;
using ZeroDiskProxy.Resource;
using ZeroDiskProxy.Settings;
using ZeroDiskProxy.ViewModels;
using ZeroDiskProxy.Views;

namespace ZeroDiskProxy.Plugin;

internal sealed class PluginHost : IDisposable
{
    private static PluginHost? _instance;
    private static readonly object Lock = new();
    private int _disposed;
    private GenerationPopupView? _popupView;

    internal static PluginHost? Instance => _instance;
    internal ResourceRegistry Resources { get; }
    internal MemoryBudget Budget { get; }
    internal ProxyCacheManager CacheManager { get; }
    internal IExportDetector ExportDetector { get; }
    internal string FallbackDirectory { get; }

    private PluginHost(
        ResourceRegistry resources,
        MemoryBudget budget,
        IProxyEncoderFactory encoderFactory,
        IExportDetector exportDetector,
        string fallbackDirectory)
    {
        Resources = resources;
        Budget = budget;
        ExportDetector = exportDetector;
        FallbackDirectory = fallbackDirectory;
        CacheManager = new ProxyCacheManager(encoderFactory, Budget);

        CacheManager.ActiveGenerations.CollectionChanged += OnGenerationsChanged;
    }

    private static PluginHost CreateDefault()
    {
        var settings = ZeroDiskProxySettings.Default;
        var resources = new ResourceRegistry();
        var budget = new MemoryBudget(settings.MemoryReserveMb, settings.MaxCacheMemoryMb);
        var fallbackDirectory = Path.Combine(AppDirectories.TemporaryDirectory, "ZeroDiskProxyTemp");
        IProxyEncoderFactory encoderFactory = new MfProxyEncoderFactory(
            budget,
            fallbackDirectory,
            static () => new EncoderConfig(
                ZeroDiskProxySettings.Default.EnableHardwareAcceleration,
                ZeroDiskProxySettings.Default.EnableDiskFallback));
        IExportDetector exportDetector = new ExportDetector();
        return new PluginHost(resources, budget, encoderFactory, exportDetector, fallbackDirectory);
    }

    internal static PluginHost EnsureInitialized()
    {
        if (_instance is not null)
            return _instance;

        lock (Lock)
        {
            _instance ??= CreateDefault();
        }

        return _instance;
    }

    internal static void TearDown()
    {
        lock (Lock)
        {
            if (_instance is null)
                return;

            _instance.Dispose();
            _instance = null;
        }
    }

    private void OnGenerationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (CacheManager.ActiveGenerations.Count > 0)
                EnsurePopupVisible();
            else
                _popupView?.Hide();
        });
    }

    private void EnsurePopupVisible()
    {
        if (_popupView is null)
        {
            var vm = new GenerationPopupViewModel(CacheManager.ActiveGenerations);
            _popupView = new GenerationPopupView(vm);

            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow is not null && mainWindow.IsLoaded)
            {
                try
                {
                    _popupView.Owner = mainWindow;
                }
                catch
                {
                    var helper = new WindowInteropHelper(_popupView);
                    var mainHelper = new WindowInteropHelper(mainWindow);
                    if (mainHelper.Handle != nint.Zero)
                        helper.Owner = mainHelper.Handle;
                }
            }
        }

        if (!_popupView.IsVisible)
            _popupView.Show();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CacheManager.ActiveGenerations.CollectionChanged -= OnGenerationsChanged;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _popupView?.ForceClose();
            _popupView = null;
        });

        CacheManager.Dispose();
        Resources.Dispose();
        CleanupFallbackDirectory();
    }

    private void CleanupFallbackDirectory()
    {
        try
        {
            if (!Directory.Exists(FallbackDirectory))
                return;

            foreach (var file in Directory.GetFiles(FallbackDirectory))
            {
                try { File.Delete(file); }
                catch { }
            }
        }
        catch { }
    }
}