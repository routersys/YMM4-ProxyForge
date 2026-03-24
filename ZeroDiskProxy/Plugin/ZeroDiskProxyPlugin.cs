using System.IO;
using System.Windows;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Detection;
using ZeroDiskProxy.Settings;

namespace ZeroDiskProxy.Plugin;

internal sealed class ZeroDiskProxyPlugin : IVideoFileSourcePlugin
{
    public string Name => "ZeroDiskProxy";

    public ZeroDiskProxyPlugin()
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
        catch
        {
            return FallbackLoad(devices, filePath);
        }
    }

    private IVideoFileSource? CreateCore(IGraphicsDevicesAndContext devices, string filePath)
    {
        var host = PluginHost.Instance;
        if (host is null)
            return null;

        var settings = ZeroDiskProxySettings.Default;
        if (!settings.UseProxy && !settings.AutoGenerate)
            return null;

        if (ExportDetector.IsExporting())
            return WrapFromOther(devices, filePath);

        if (!IsLargeEnough(filePath, settings.MinFileSizeForProxy))
            return null;

        var proxyScale = settings.Scale / 100f;

        if (settings.UseProxy)
        {
            var cached = host.CacheManager.TryGetProxy(filePath, proxyScale);
            if (cached is not null && cached.IsValid)
                return LoadFromCache(devices, cached, host.FallbackDirectory);

            host.CacheManager.StartProxyGeneration(filePath, proxyScale, settings);

            var source = WrapFromOther(devices, filePath);
            if (source is null)
                return null;

            return new ZeroDiskProxyVideoSource(source, devices, () => TryLoadProxy(devices, filePath, proxyScale, host));
        }

        if (settings.AutoGenerate && !host.CacheManager.ProxyExists(filePath, proxyScale))
            host.CacheManager.StartProxyGeneration(filePath, proxyScale, settings);

        return null;
    }

    private static IVideoFileSource? LoadFromCache(
        IGraphicsDevicesAndContext devices, ProxyCacheEntry entry, string fallbackDir)
    {
        try
        {
            var tempPath = entry.GetOrCreateTempFilePath(fallbackDir);
            var source = LoadRaw(devices, tempPath);
            if (source is null)
                return null;

            return entry.Scale < 0.99f
                ? new ZeroDiskProxyVideoSourceWithScale(source, devices, entry.Scale)
                : new ZeroDiskProxyVideoSource(source, devices);
        }
        catch
        {
            return null;
        }
    }

    private static IVideoFileSource? WrapFromOther(IGraphicsDevicesAndContext devices, string filePath)
    {
        var source = LoadRaw(devices, filePath);
        return source is not null ? new ZeroDiskProxyVideoSource(source, devices) : null;
    }

    private static IVideoFileSource? LoadRaw(IGraphicsDevicesAndContext devices, string filePath)
    {
        foreach (var plugin in PluginLoader.VideoFileSourcePlugins)
        {
            if (plugin is ZeroDiskProxyPlugin)
                continue;

            try
            {
                var s = plugin.CreateVideoFileSource(devices, filePath);
                if (s is not null)
                    return s;
            }
            catch { }
        }
        return null;
    }

    private static IVideoFileSource? FallbackLoad(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            return WrapFromOther(devices, filePath);
        }
        catch
        {
            return null;
        }
    }

    private static IVideoFileSource? TryLoadProxy(
        IGraphicsDevicesAndContext devices, string filePath, float proxyScale, PluginHost host)
    {
        var cached = host.CacheManager.TryGetProxy(filePath, proxyScale);
        if (cached is null || !cached.IsValid)
            return null;

        var source = LoadFromCache(devices, cached, host.FallbackDirectory);
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
            if (ZeroDiskProxySettings.Default.ClearCacheOnExit)
                PluginHost.Instance?.CacheManager.ClearAll();

            PluginHost.TearDown();
        }
        catch { }
    }
}