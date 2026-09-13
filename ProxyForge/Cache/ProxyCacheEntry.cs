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

    public long FileLength { get; set; }

    public long CreatedTicks { get; set; }

    public long LastUsedTicks { get; set; }

    public bool Matches(SourceIdentity source, int scale)
        => Scale == scale && source.Matches(SourcePath, SourceLength, SourceWriteTimeTicks);

    public ProxyCacheEntry Clone() => (ProxyCacheEntry)MemberwiseClone();
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

    public bool Matches(SourceIdentity source)
        => source.Matches(SourcePath, SourceLength, SourceWriteTimeTicks);
}

internal sealed class ProxyCacheIndex
{
    public List<ProxyCacheEntry> Entries { get; set; } = [];

    public List<ProxyCacheSkip> Skips { get; set; } = [];
}
