using System.Collections.Immutable;
using YukkuriMovieMaker.Plugin;

namespace ProxyForge;

internal static class PluginOrder
{
    public static string PluginName { get; } = typeof(ProxyForgeVideoFileSourcePlugin).FullName ?? nameof(ProxyForgeVideoFileSourcePlugin);

    public static bool IsFirst(ImmutableList<string> order, string name)
        => order.Count > 0 && string.Equals(order[0], name, StringComparison.Ordinal);

    public static ImmutableList<string> MoveToFront(ImmutableList<string> order, string name)
        => order.RemoveAll(entry => string.Equals(entry, name, StringComparison.Ordinal)).Insert(0, name);

    public static bool IsPluginFirst()
        => IsFirst(PluginLoaderSettings.Default.VideoFileSourcePlugins, PluginName);

    public static void MovePluginToFront()
        => PluginLoaderSettings.Default.VideoFileSourcePlugins = MoveToFront(PluginLoaderSettings.Default.VideoFileSourcePlugins, PluginName);
}
