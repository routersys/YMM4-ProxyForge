using System.Diagnostics;
using System.IO;
using System.Windows;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using ZeroDiskProxy.Core;
using ZeroDiskProxy.Settings;
using ZeroDiskProxy.Streaming;

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
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] CreateCore failed for ", filePath, ": ", ex.Message));
            return FallbackLoad(devices, filePath);
        }
    }

    private static IVideoFileSource? CreateCore(IGraphicsDevicesAndContext devices, string filePath)
    {
        var host = PluginHost.Instance;
        if (host is null)
            return null;

        var settings = ZeroDiskProxySettings.Default;
        if (!settings.UseProxy && !settings.AutoGenerate)
            return null;

        if (host.ExportDetector.IsExporting())
            return WrapFromOther(devices, filePath);

        if (!IsLargeEnough(filePath, settings.MinFileSizeForProxy))
            return null;

        var proxyScale = settings.Scale / 100f;

        if (settings.UseProxy)
        {
            var cached = host.CacheManager.TryGetProxy(filePath, proxyScale);
            if (cached is { IsValid: true })
                return LoadFromCache(devices, cached, host.FallbackDirectory);

            var initialSource = CreateLightweightSource(devices, filePath)
                                ?? WrapFromOther(devices, filePath);
            if (initialSource is null)
                return null;

            var generationStarted = 0;
            return new ZeroDiskProxyVideoSource(initialSource, devices,
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

            return WrapFromOther(devices, filePath);
        }

        return null;
    }

    private static IVideoFileSource? CreateLightweightSource(
        IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            return LightweightVideoFileSource.TryCreate(filePath, devices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] LightweightSource failed for ", filePath, ": ", ex.Message));
            return null;
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] LoadFromCache failed: ", ex.Message));
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
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat("[ZeroDiskProxy] Plugin ", plugin.Name, " failed for ", filePath, ": ", ex.Message));
            }
        }
        return null;
    }

    private static IVideoFileSource? FallbackLoad(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            var lightSource = CreateLightweightSource(devices, filePath);
            if (lightSource is not null)
                return lightSource;

            return WrapFromOther(devices, filePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[ZeroDiskProxy] FallbackLoad failed for ", filePath, ": ", ex.Message));
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