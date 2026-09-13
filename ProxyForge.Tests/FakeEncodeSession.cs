using ProxyForge.Encoding;

namespace ProxyForge.Tests;

sealed class FakeEncodeSession(ProxyAnalysis analysis, Func<int, int, string, IProgress<double>?, CancellationToken, Task<long>> encode) : IProxyEncodeSession
{
    public bool IsDisposed { get; private set; }

    public ProxyAnalysis Analysis => analysis;

    public Task<long> EncodeAsync(int firstFrame, int frameCount, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        => encode(firstFrame, frameCount, outputPath, progress, cancellationToken);

    public void Dispose() => IsDisposed = true;
}
