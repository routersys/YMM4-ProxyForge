namespace ProxyForge.Encoding;

public enum ProxyEncodeFailure
{
    FFmpegUnavailable,
    SourceUnavailable,
    NoFrames,
    UnusableSize,
    Transparent,
    FFmpegFailed,
    NoOutput,
    SourceChanged,
    GraphicsDeviceLost,
}

internal sealed class ProxyEncodeException(ProxyEncodeFailure failure, string message) : Exception(message)
{
    public ProxyEncodeFailure Failure { get; } = failure;
}
