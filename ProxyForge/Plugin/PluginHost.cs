using ProxyForge.Cache;
using ProxyForge.Core;
using ProxyForge.Detection;
using ProxyForge.Interfaces;
using ProxyForge.Services;
using ProxyForge.Settings;
using ProxyForge.ViewModels;
using ProxyForge.Views;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Plugin;

internal sealed class PluginHost : IDisposable
{
    private static PluginHost? _instance;
    private static readonly object Lock = new();
    private int _disposed;
    private GenerationPopupView? _popupView;

    internal static PluginHost? Instance => _instance;
    internal ProxyCacheManager CacheManager { get; }
    internal IExportDetector ExportDetector { get; }
    internal string FallbackDirectory { get; }
    internal string TmpDirectory { get; }
    internal VideoCacheDatabase? VideoCache { get; }

    private PluginHost(
        IProxyEncoderFactory encoderFactory,
        IExportDetector exportDetector,
        string fallbackDirectory,
        string tmpDirectory,
        VideoCacheDatabase? videoCache)
    {
        ExportDetector = exportDetector;
        FallbackDirectory = fallbackDirectory;
        TmpDirectory = tmpDirectory;
        VideoCache = videoCache;
        CacheManager = new ProxyCacheManager(encoderFactory, videoCache, tmpDirectory);

        CacheManager.ActiveGenerations.CollectionChanged += OnGenerationsChanged;
    }

    private static PluginHost CreateDefault()
    {
        var settings = ProxyForgeSettings.Default;
        var pluginDir = Path.GetDirectoryName(typeof(PluginHost).Assembly.Location) ?? AppDirectories.TemporaryDirectory;
        var cacheDir = Path.Combine(pluginDir, "cache");
        var tmpDirectory = Path.Combine(cacheDir, "tmp");

        WindowThemeRegistry.Register(new WindowThemeService());

        IProxyEncoderFactory encoderFactory = new FFmpegProxyEncoderFactory(
            tmpDirectory,
            static () => new EncoderConfig(ProxyForgeSettings.Default.EnableHardwareAcceleration));

        VideoCacheDatabase? videoCache = null;
        if (settings.EnableVideoCache)
        {
            try
            {
                videoCache = new VideoCacheDatabase(cacheDir);
            }
            catch { }
        }

        IExportDetector exportDetector = new ExportDetector();
        return new PluginHost(encoderFactory, exportDetector, tmpDirectory, tmpDirectory, videoCache);
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
            WindowThemeRegistry.Bind(_popupView);

            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow is { IsLoaded: true })
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
        VideoCache?.Dispose();
        CleanupTmpDirectory();
    }

    private void CleanupTmpDirectory()
    {
        try
        {
            if (!Directory.Exists(TmpDirectory))
                return;

            foreach (var file in Directory.GetFiles(TmpDirectory))
            {
                try { File.Delete(file); }
                catch { }
            }
        }
        catch { }
    }
}