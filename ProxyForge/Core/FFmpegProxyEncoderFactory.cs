using ProxyForge.Interfaces;
using ProxyForge.Plugin;
using ProxyForge.Transcoding;

namespace ProxyForge.Core;

internal sealed class FFmpegProxyEncoderFactory(
    string fallbackDirectory,
    Func<EncoderConfig> configProvider) : IProxyEncoderFactory
{
    public IProxyEncoder Create() =>
        new FFmpegProxyEncoder(
            fallbackDirectory, configProvider(), FFmpegRuntime.Require(), Ymm4VideoSourceLoader.Load);
}
