using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

public sealed class FrameRateResolverTests
{
    static Func<TimeSpan, int> HostFunction(int numerator, int denominator)
        => time => FrameTime.TimeToFrame(time, numerator, denominator);

    static TimeSpan DurationOf(int frameCount, int numerator, int denominator)
        => TimeSpan.FromTicks((long)Math.Round((double)frameCount * denominator * TimeSpan.TicksPerSecond / numerator));

    [Theory]
    [InlineData(30, 1)]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(60, 1)]
    [InlineData(120, 1)]
    [InlineData(240, 1)]
    [InlineData(15, 1)]
    [InlineData(1, 1)]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    [InlineData(2997, 100)]
    [InlineData(1000000, 33367)]
    [InlineData(2, 3)]
    public void RecoversTheExactRateFromTheHostConversion(int numerator, int denominator)
    {
        var function = HostFunction(numerator, denominator);
        var duration = DurationOf(1000, numerator, denominator);

        var rate = FrameRateResolver.Resolve(function, function(duration), duration);

        Assert.Equal(new FrameRate(numerator, denominator), rate);
    }

    [Theory]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    [InlineData(24000, 1001)]
    public void RecoversTheExactRateForAVeryShortClip(int numerator, int denominator)
    {
        var function = HostFunction(numerator, denominator);
        var duration = DurationOf(3, numerator, denominator);

        var rate = FrameRateResolver.Resolve(function, function(duration), duration);

        Assert.Equal(new FrameRate(numerator, denominator), rate);
    }

    [Fact]
    public void RecoversTheExactRateForAThreeHourClip()
    {
        var function = HostFunction(30000, 1001);
        var duration = TimeSpan.FromHours(3);

        var rate = FrameRateResolver.Resolve(function, function(duration), duration);

        Assert.Equal(new FrameRate(30000, 1001), rate);
    }

    [Fact]
    public void RecoversTheExactRateWhenTheDurationHasAnAudioTail()
    {
        var function = HostFunction(30000, 1001);
        var duration = DurationOf(1000, 30000, 1001) + TimeSpan.FromMilliseconds(300);

        var rate = FrameRateResolver.Resolve(function, function(duration), duration);

        Assert.Equal(new FrameRate(30000, 1001), rate);
    }

    [Fact]
    public void RecoversTheExactRateWhereTheApproximationFails()
    {
        var function = HostFunction(30000, 1001);
        var duration = DurationOf(1000, 30000, 1001);
        var frameCount = function(duration);

        Assert.NotEqual(new FrameRate(30000, 1001), FrameRate.Approximate(frameCount, duration));
        Assert.Equal(new FrameRate(30000, 1001), FrameRateResolver.Resolve(function, frameCount, duration));
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFunctionClampsAtTheEnd()
    {
        var function = HostFunction(30, 1);
        var duration = TimeSpan.FromSeconds(10);
        var frameCount = function(duration);

        var rate = FrameRateResolver.Resolve(time => Math.Min(function(time), frameCount), frameCount, duration);

        Assert.Equal(FrameRate.Approximate(frameCount, duration), rate);
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFunctionThrowsOutsideTheClip()
    {
        var function = HostFunction(30, 1);
        var duration = TimeSpan.FromSeconds(10);
        var frameCount = function(duration);

        var rate = FrameRateResolver.Resolve(
            time => time > duration ? throw new ArgumentOutOfRangeException(nameof(time)) : function(time),
            frameCount,
            duration);

        Assert.Equal(FrameRate.Approximate(frameCount, duration), rate);
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFunctionIsConstant()
    {
        var duration = TimeSpan.FromSeconds(10);

        var rate = FrameRateResolver.Resolve(_ => 0, 1, duration);

        Assert.Equal(FrameRate.Approximate(1, duration), rate);
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFunctionDisagreesInsideTheClip()
    {
        var function = HostFunction(30, 1);
        var duration = TimeSpan.FromSeconds(10);
        var frameCount = function(duration);
        var disagreement = new TimeSpan(duration.Ticks / 7 * 3);

        var rate = FrameRateResolver.Resolve(
            time => time == disagreement ? function(time) + 1 : function(time),
            frameCount,
            duration);

        Assert.Equal(FrameRate.Approximate(frameCount, duration), rate);
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFunctionTruncates()
    {
        var duration = TimeSpan.FromSeconds(10);
        Func<TimeSpan, int> function = time => (int)Math.Floor(time.TotalSeconds * 30000d / 1001d);
        var frameCount = function(duration);

        var rate = FrameRateResolver.Resolve(function, frameCount, duration);

        Assert.Equal(FrameRate.Approximate(frameCount, duration), rate);
    }

    [Fact]
    public void FallsBackToTheApproximationWhenTheFrameCountContradictsTheFunction()
    {
        var function = HostFunction(30, 1);
        var duration = TimeSpan.FromSeconds(10);

        var rate = FrameRateResolver.Resolve(function, 299, duration);

        Assert.Equal(FrameRate.Approximate(299, duration), rate);
    }
}
