using ProxyForge.Cache;
using YukkuriMovieMaker.Plugin;

namespace ProxyForge.Tests;

public sealed class ProxyForgeSettingsTests
{
    [Fact]
    public void TheDefaultsMatchTheDocumentedBehaviour()
    {
        var settings = new ProxyForgeSettings();

        Assert.True(settings.IsEnabled);
        Assert.True(settings.GeneratesAutomatically);
        Assert.Equal(50, settings.Scale);
        Assert.Equal(100, settings.MinimumFileSizeMegabytes);
        Assert.Equal(50, settings.BitrateScale);
        Assert.Equal(30, settings.KeyFrameInterval);
        Assert.Equal(10, settings.ChunkSeconds);
        Assert.True(settings.UsesHardwareEncoder);
        Assert.Equal(10, settings.CacheLimitGigabytes);
        Assert.True(settings.ShowsProgressWindow);
        Assert.Equal(SettingsCategory.VideoFileSource, settings.Category);
        Assert.Equal("ProxyForge", settings.Name);
    }

    [Theory]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void TheScaleIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { Scale = value };

        Assert.Equal(expected, settings.Scale);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100_000, 100_000)]
    [InlineData(100_001, 100_000)]
    public void TheMinimumFileSizeIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { MinimumFileSizeMegabytes = value };

        Assert.Equal(expected, settings.MinimumFileSizeMegabytes);
        Assert.Equal(expected * 1024L * 1024L, settings.MinimumFileSizeBytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    public void TheBitrateScaleIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { BitrateScale = value };

        Assert.Equal(expected, settings.BitrateScale);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    public void TheKeyFrameIntervalIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { KeyFrameInterval = value };

        Assert.Equal(expected, settings.KeyFrameInterval);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 10)]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    public void TheChunkLengthIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { ChunkSeconds = value };

        Assert.Equal(expected, settings.ChunkSeconds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    public void TheCacheLimitIsClamped(int value, int expected)
    {
        var settings = new ProxyForgeSettings { CacheLimitGigabytes = value };

        Assert.Equal(expected, settings.CacheLimitGigabytes);
        Assert.Equal(expected * 1024L * 1024L * 1024L, ProxyCache.Shared.LimitBytes);
    }

    [Fact]
    public void InitializeAppliesTheCacheLimit()
    {
        var settings = new ProxyForgeSettings { CacheLimitGigabytes = 3 };
        ProxyCache.Shared.LimitBytes = 1;

        settings.Initialize();

        Assert.Equal(3L * 1024L * 1024L * 1024L, ProxyCache.Shared.LimitBytes);
    }

    [Fact]
    public void ChangingAValueRaisesPropertyChanged()
    {
        var settings = new ProxyForgeSettings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Scale = 60;
        settings.Scale = 60;
        settings.IsEnabled = false;

        Assert.Equal([nameof(ProxyForgeSettings.Scale), nameof(ProxyForgeSettings.IsEnabled)], raised);
    }
}
