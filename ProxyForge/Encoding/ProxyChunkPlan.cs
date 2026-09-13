using ProxyForge.Sources;

namespace ProxyForge.Encoding;

internal static class ProxyChunkPlan
{
    public static int LengthFor(FrameRate frameRate, int seconds)
        => Math.Max(1, (int)Math.Round(frameRate.Value * seconds, MidpointRounding.AwayFromZero));

    public static int? Next(IReadOnlyCollection<int> completed, int chunkCount, int chunkLength, int? focusFrame)
    {
        if (chunkCount <= 0)
            return null;

        var start = focusFrame is { } frame && frame > 0 ? Math.Min(frame / chunkLength, chunkCount - 1) : 0;
        for (var chunk = start; chunk < chunkCount; chunk++)
        {
            if (!completed.Contains(chunk))
                return chunk;
        }

        for (var chunk = 0; chunk < start; chunk++)
        {
            if (!completed.Contains(chunk))
                return chunk;
        }

        return null;
    }
}
