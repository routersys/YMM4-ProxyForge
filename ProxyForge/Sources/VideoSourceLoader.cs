using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal delegate IVideoFileSource? VideoSourceFactory(IGraphicsDevicesAndContext devices, string filePath);

internal static class VideoSourceLoader
{
    public static IVideoFileSource? Load(IGraphicsDevicesAndContext devices, string filePath)
    {
        foreach (var plugin in PluginLoader.VideoFileSourcePlugins)
        {
            if (ReferenceEquals(plugin.GetType().Assembly, typeof(VideoSourceLoader).Assembly))
                continue;

            var source = plugin.CreateVideoFileSource(devices, filePath);
            if (source is not null)
                return source;
        }

        return null;
    }
}
