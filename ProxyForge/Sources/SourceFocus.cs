using ProxyForge.Cache;

namespace ProxyForge.Sources;

internal sealed class SourceFocus
{
    readonly Lock gate = new();
    readonly Dictionary<SourceIdentity, int> frames = [];

    public static SourceFocus Shared { get; } = new();

    public void Report(SourceIdentity source, int frame)
    {
        using (gate.EnterScope())
            frames[source] = frame;
    }

    public int? Get(SourceIdentity source)
    {
        using (gate.EnterScope())
            return frames.TryGetValue(source, out var frame) ? frame : null;
    }
}
