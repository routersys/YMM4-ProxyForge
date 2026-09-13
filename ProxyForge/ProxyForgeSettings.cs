using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Views;
using YukkuriMovieMaker.Plugin;

namespace ProxyForge;

internal sealed class ProxyForgeSettings : SettingsBase<ProxyForgeSettings>
{
    public const int MinimumScale = ProxyGeometryCalculator.MinimumScale;
    public const int MaximumScale = ProxyGeometryCalculator.MaximumScale;
    public const int MinimumFileSizeMegabytesLowerBound = 1;
    public const int MinimumFileSizeMegabytesUpperBound = 100_000;
    public const int MinimumBitrateScale = 1;
    public const int MaximumBitrateScale = 200;
    public const int MinimumKeyFrameInterval = 1;
    public const int MaximumKeyFrameInterval = 300;
    public const int MinimumChunkSeconds = 1;
    public const int MaximumChunkSeconds = 120;
    public const int MinimumCacheLimitGigabytes = 1;
    public const int MaximumCacheLimitGigabytes = 1024;
    const long BytesPerMegabyte = 1024L * 1024L;
    const long BytesPerGigabyte = 1024L * 1024L * 1024L;

    bool isEnabled = true;
    bool generatesAutomatically = true;
    int scale = 50;
    int minimumFileSizeMegabytes = 100;
    int bitrateScale = 50;
    int keyFrameInterval = 30;
    int chunkSeconds = 10;
    bool usesHardwareEncoder = true;
    int cacheLimitGigabytes = 10;
    bool showsProgressWindow = true;

    public override SettingsCategory Category => SettingsCategory.VideoFileSource;

    public override string Name => Texts.ProxyForge;

    public override bool HasSettingView => true;

    public override object? SettingView => new ProxyForgeSettingsView();

    public bool IsEnabled
    {
        get => isEnabled;
        set => Set(ref isEnabled, value);
    }

    public bool GeneratesAutomatically
    {
        get => generatesAutomatically;
        set => Set(ref generatesAutomatically, value);
    }

    public int Scale
    {
        get => scale;
        set => Set(ref scale, Math.Clamp(value, MinimumScale, MaximumScale));
    }

    public int MinimumFileSizeMegabytes
    {
        get => minimumFileSizeMegabytes;
        set => Set(ref minimumFileSizeMegabytes, Math.Clamp(value, MinimumFileSizeMegabytesLowerBound, MinimumFileSizeMegabytesUpperBound));
    }

    public int BitrateScale
    {
        get => bitrateScale;
        set => Set(ref bitrateScale, Math.Clamp(value, MinimumBitrateScale, MaximumBitrateScale));
    }

    public int KeyFrameInterval
    {
        get => keyFrameInterval;
        set => Set(ref keyFrameInterval, Math.Clamp(value, MinimumKeyFrameInterval, MaximumKeyFrameInterval));
    }

    public int ChunkSeconds
    {
        get => chunkSeconds;
        set => Set(ref chunkSeconds, Math.Clamp(value, MinimumChunkSeconds, MaximumChunkSeconds));
    }

    public bool UsesHardwareEncoder
    {
        get => usesHardwareEncoder;
        set => Set(ref usesHardwareEncoder, value);
    }

    public int CacheLimitGigabytes
    {
        get => cacheLimitGigabytes;
        set
        {
            if (Set(ref cacheLimitGigabytes, Math.Clamp(value, MinimumCacheLimitGigabytes, MaximumCacheLimitGigabytes)))
                ApplyCacheLimit();
        }
    }

    public bool ShowsProgressWindow
    {
        get => showsProgressWindow;
        set => Set(ref showsProgressWindow, value);
    }

    public long MinimumFileSizeBytes => minimumFileSizeMegabytes * BytesPerMegabyte;

    public override void Initialize() => ApplyCacheLimit();

    void ApplyCacheLimit() => ProxyCache.Shared.LimitBytes = cacheLimitGigabytes * BytesPerGigabyte;
}
