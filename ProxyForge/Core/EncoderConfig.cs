namespace ProxyForge.Core;

internal readonly record struct EncoderConfig(
    bool EnableHardwareAcceleration,
    bool EnableDiskFallback
);