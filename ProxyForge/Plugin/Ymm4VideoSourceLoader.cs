using System.Diagnostics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Plugin;

internal delegate IVideoFileSource? Ymm4VideoSourceFactory(
    IGraphicsDevicesAndContext devices, string filePath);

internal static class Ymm4VideoSourceLoader
{
    internal static IVideoFileSource? Load(IGraphicsDevicesAndContext devices, string filePath)
    {
        foreach (var plugin in PluginLoader.VideoFileSourcePlugins)
        {
            if (plugin is ProxyForgePlugin)
                continue;

            try
            {
                var source = plugin.CreateVideoFileSource(devices, filePath);
                if (source is not null)
                    return source;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat(
                    "[ProxyForge] Plugin ", plugin.Name, " failed for ", filePath, ": ", ex.Message));
            }
        }

        return null;
    }
}
