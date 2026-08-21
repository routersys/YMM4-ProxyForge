using ProxyForge.Transcoding;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FrameRateTests
{
    [Fact]
    public void FromFrameCountAndDuration_ReproducesIntegerRate()
    {
        var rate = FrameRate.FromFrameCountAndDuration(180, TimeSpan.FromSeconds(6));

        Assert.Equal(30, rate.Numerator);
        Assert.Equal(1, rate.Denominator);
    }

    [Fact]
    public void FromFrameCountAndDuration_ReproducesNtscRateExactly()
    {
        var duration = TimeSpan.FromTicks(120L * TimeSpan.TicksPerSecond * 1001L / 30000L);

        var rate = FrameRate.FromFrameCountAndDuration(120, duration);

        Assert.Equal(30000, rate.Numerator);
        Assert.Equal(1001, rate.Denominator);
    }

    [Theory]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(60, 1)]
    [InlineData(24000, 1001)]
    [InlineData(60000, 1001)]
    public void FromFrameCountAndDuration_RoundTripsCommonRates(int numerator, int denominator)
    {
        const int frameCount = 600;
        var duration = TimeSpan.FromTicks(
            frameCount * TimeSpan.TicksPerSecond * denominator / numerator);

        var rate = FrameRate.FromFrameCountAndDuration(frameCount, duration);

        Assert.Equal(numerator, rate.Numerator);
        Assert.Equal(denominator, rate.Denominator);
    }

    [Fact]
    public void FromFrameCountAndDuration_LongDurationStaysWithinIntegerRange()
    {
        var duration = TimeSpan.FromHours(3);
        var frameCount = (int)(duration.TotalSeconds * 30);

        var rate = FrameRate.FromFrameCountAndDuration(frameCount, duration);

        Assert.True(rate.IsValid);
        Assert.Equal(30d, rate.Value, 9);
    }

    [Fact]
    public void FromFrameCountAndDuration_AwkwardDurationApproximatesRate()
    {
        var duration = TimeSpan.FromTicks(36_000_000_001L);

        var rate = FrameRate.FromFrameCountAndDuration(108_000, duration);

        Assert.True(rate.IsValid);
        Assert.InRange(rate.Value, 29.999_999, 30.000_001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromFrameCountAndDuration_RejectsNonPositiveFrameCount(int frameCount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameRate.FromFrameCountAndDuration(frameCount, TimeSpan.FromSeconds(1)));

    [Fact]
    public void FromFrameCountAndDuration_RejectsZeroDuration() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameRate.FromFrameCountAndDuration(1, TimeSpan.Zero));

    [Fact]
    public void GetSampleTime_SamplesTheMiddleOfEachFrameInterval()
    {
        var duration = TimeSpan.FromSeconds(1);

        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60), FrameRate.GetSampleTime(0, 30, duration));
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 20), FrameRate.GetSampleTime(1, 30, duration));
    }

    [Fact]
    public void GetSampleTime_StaysStrictlyInsideTheSourceDuration()
    {
        var duration = TimeSpan.FromSeconds(6);

        for (var index = 0; index < 180; index++)
        {
            var sample = FrameRate.GetSampleTime(index, 180, duration);
            Assert.InRange(sample.Ticks, 0, duration.Ticks - 1);
        }
    }

    [Fact]
    public void GetSampleTime_IsStrictlyIncreasing()
    {
        var duration = TimeSpan.FromSeconds(4.004);
        var previous = TimeSpan.MinValue;

        for (var index = 0; index < 120; index++)
        {
            var sample = FrameRate.GetSampleTime(index, 120, duration);
            Assert.True(sample > previous);
            previous = sample;
        }
    }

    [Fact]
    public void GetSampleTime_MapsBackToTheSameFrameIndex()
    {
        const int frameCount = 120;
        var duration = TimeSpan.FromTicks(frameCount * TimeSpan.TicksPerSecond * 1001L / 30000L);

        for (var index = 0; index < frameCount; index++)
        {
            var sample = FrameRate.GetSampleTime(index, frameCount, duration);
            var recovered = (int)(sample.TotalSeconds * 30000d / 1001d);
            Assert.Equal(index, recovered);
        }
    }
}
