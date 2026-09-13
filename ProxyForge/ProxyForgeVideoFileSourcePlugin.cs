using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge;

[PluginDetails(AuthorName = "routersys")]
internal sealed class ProxyForgeVideoFileSourcePlugin : IVideoFileSourcePlugin
{
    public string Name => Texts.ProxyForge;

    public IVideoFileSource? CreateVideoFileSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        ProxyForgeHost.EnsureStartedOnce();
        return ProxyForgeHost.Provider.Create(devices, filePath);
    }
}
