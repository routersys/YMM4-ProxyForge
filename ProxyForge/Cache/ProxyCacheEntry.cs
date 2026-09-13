using Newtonsoft.Json;

namespace ProxyForge.Cache;

internal sealed class ProxyCacheEntry
{
    public Guid Id { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public long SourceLength { get; set; }

    public long SourceWriteTimeTicks { get; set; }

    public int Scale { get; set; }

    public int ProxyWidth { get; set; }

    public int ProxyHeight { get; set; }

    public float DisplayLeft { get; set; }

    public float DisplayTop { get; set; }

    public float DisplayWidth { get; set; }

    public float DisplayHeight { get; set; }

    public int FrameRateNumerator { get; set; }

    public int FrameRateDenominator { get; set; }

    public long DurationTicks { get; set; }

    public int FrameCount { get; set; }

    public int ChunkLength { get; set; }

    public List<int> Chunks { get; set; } = [];

    public long FileLength { get; set; }

    public long CreatedTicks { get; set; }

    public long LastUsedTicks { get; set; }

    [JsonIgnore]
    public SourceIdentity Source => new(SourcePath, SourceLength, SourceWriteTimeTicks);

    [JsonIgnore]
    public int ChunkCount => ChunkLength <= 0 ? 0 : (FrameCount + ChunkLength - 1) / ChunkLength;

    [JsonIgnore]
    public bool IsComplete => Chunks.Count >= ChunkCount;

    [JsonIgnore]
    public bool HasValidLayout => ChunkLength > 0 && FrameCount > 0;

    public bool Matches(SourceIdentity source, int scale) => Scale == scale && Source == source;

    public bool HasChunk(int chunk) => Chunks.Contains(chunk);

    public bool HasSameLayout(ProxyCacheEntry other)
        => ProxyWidth == other.ProxyWidth
            && ProxyHeight == other.ProxyHeight
            && DisplayLeft == other.DisplayLeft
            && DisplayTop == other.DisplayTop
            && DisplayWidth == other.DisplayWidth
            && DisplayHeight == other.DisplayHeight
            && FrameRateNumerator == other.FrameRateNumerator
            && FrameRateDenominator == other.FrameRateDenominator
            && DurationTicks == other.DurationTicks
            && FrameCount == other.FrameCount
            && ChunkLength == other.ChunkLength;

    public ProxyCacheEntry Clone()
    {
        var clone = (ProxyCacheEntry)MemberwiseClone();
        clone.Chunks = [.. Chunks];
        return clone;
    }
}

internal enum ProxyCacheSkipReason
{
    Transparent,
}

internal sealed class ProxyCacheSkip
{
    public string SourcePath { get; set; } = string.Empty;

    public long SourceLength { get; set; }

    public long SourceWriteTimeTicks { get; set; }

    public ProxyCacheSkipReason Reason { get; set; }

    public bool Matches(SourceIdentity source) => new SourceIdentity(SourcePath, SourceLength, SourceWriteTimeTicks) == source;
}

internal sealed class ProxyCacheIndex
{
    public List<ProxyCacheEntry> Entries { get; set; } = [];

    public List<ProxyCacheSkip> Skips { get; set; } = [];
}
