namespace ProxyForge.Encoding;

internal sealed record ProxyChunkCoverage(int ChunkCount, IReadOnlyList<int> Chunks, int? CurrentChunk, double CurrentProgress)
{
    public static ProxyChunkCoverage Empty { get; } = new(0, [], null, 0d);

    public bool Contains(int chunk) => Chunks.Contains(chunk);
}
