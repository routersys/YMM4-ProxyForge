using ProxyForge.Core;
using ProxyForge.Settings;
using System.Diagnostics;
using System.IO;
using System.Windows;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Plugin;

internal sealed class ProxyForgePlugin : IVideoFileSourcePlugin
{
    public string Name => "ProxyForge";

    public ProxyForgePlugin()
    {
        PluginHost.EnsureInitialized();
        if (Application.Current is not null)
            Application.Current.Exit += OnApplicationExit;
    }

    public IVideoFileSource? CreateVideoFileSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            return CreateCore(devices, filePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ProxyForge] CreateCore failed for ", filePath, ": ", ex.Message));
            return null;
        }
    }

    private static IVideoFileSource? CreateCore(IGraphicsDevicesAndContext devices, string filePath)
    {
        var host = PluginHost.Instance;
        if (host is null)
            return null;

        var settings = ProxyForgeSettings.Default;
        if (!settings.UseProxy && !settings.AutoGenerate)
            return null;

        if (host.ExportDetector.IsExporting())
            return null;

        if (!IsLargeEnough(filePath, settings.MinFileSizeForProxy))
            return null;

        var proxyScale = settings.Scale / 100f;

        if (settings.UseProxy)
        {
            var cached = host.CacheManager.TryGetProxy(filePath, proxyScale);
            if (cached is { IsValid: true })
                return LoadFromCache(devices, cached);

            var initialSource = LoadRaw(devices, filePath);
            if (initialSource is null)
                return null;

            var generationStarted = 0;
            return new ProxyForgeVideoSource(initialSource, devices,
                () =>
                {
                    if (Interlocked.Exchange(ref generationStarted, 1) == 0)
                        host.CacheManager.StartProxyGeneration(filePath, proxyScale, settings);
                    return TryLoadProxy(devices, filePath, proxyScale, host);
                });
        }

        if (settings.AutoGenerate)
        {
            if (!host.CacheManager.ProxyExists(filePath, proxyScale))
                host.CacheManager.StartProxyGeneration(filePath, proxyScale, settings);

            return null;
        }

        return null;
    }

    private static IVideoFileSource? LoadFromCache(
        IGraphicsDevicesAndContext devices, ProxyCacheEntry entry)
    {
        try
        {
            var source = LoadRaw(devices, entry.GetFilePath());
            if (source is null)
                return null;

            return entry.Scale < 0.99f
                ? new ProxyForgeVideoSourceWithScale(source, devices, entry.Scale)
                : new ProxyForgeVideoSource(source, devices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ProxyForge] LoadFromCache failed: ", ex.Message));
            return null;
        }
    }

    private static IVideoFileSource? LoadRaw(IGraphicsDevicesAndContext devices, string filePath) =>
        Ymm4VideoSourceLoader.Load(devices, filePath);

    private static IVideoFileSource? TryLoadProxy(
        IGraphicsDevicesAndContext devices, string filePath, float proxyScale, PluginHost host)
    {
        var cached = host.CacheManager.TryGetProxy(filePath, proxyScale);
        if (cached is null || !cached.IsValid)
            return null;

        var source = LoadFromCache(devices, cached);
        if (source is null)
            host.CacheManager.RemoveProxy(filePath, proxyScale);

        return source;
    }

    private static bool IsLargeEnough(string filePath, int minSizeMb)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists && info.Length >= (long)minSizeMb * 1048576L;
        }
        catch
        {
            return false;
        }
    }

    private static void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        try
        {
            if (ProxyForgeSettings.Default.ClearCacheOnExit)
                PluginHost.Instance?.CacheManager.ClearAll();

            PluginHost.TearDown();
        }
        catch { }
    }
}