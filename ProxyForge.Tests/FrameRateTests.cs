using ProxyForge.Sources;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Tests;

public sealed class FrameRateTests
{
    [Fact]
    public void ApproximateReproducesAnIntegerRate()
    {
        var rate = FrameRate.Approximate(180, TimeSpan.FromSeconds(6));

        Assert.Equal(30, rate.Numerator);
        Assert.Equal(1, rate.Denominator);
    }

    [Fact]
    public void ApproximateReproducesTheNtscRateWhenTheDurationIsExact()
    {
        var duration = TimeSpan.FromTicks(120L * TimeSpan.TicksPerSecond * 1001L / 30000L);

        var rate = FrameRate.Approximate(120, duration);

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
    public void ApproximateRoundTripsCommonRates(int numerator, int denominator)
    {
        const int frameCount = 600;
        var duration = TimeSpan.FromTicks(frameCount * TimeSpan.TicksPerSecond * denominator / numerator);

        var rate = FrameRate.Approximate(frameCount, duration);

        Assert.Equal(numerator, rate.Numerator);
        Assert.Equal(denominator, rate.Denominator);
    }

    [Fact]
    public void ApproximateStaysWithinIntegerRangeForLongDurations()
    {
        var duration = TimeSpan.FromHours(3);
        var frameCount = (int)(duration.TotalSeconds * 30);

        var rate = FrameRate.Approximate(frameCount, duration);

        Assert.True(rate.IsValid);
        Assert.Equal(30d, rate.Value, 9);
    }

    [Fact]
    public void ApproximateApproximatesAnAwkwardDuration()
    {
        var rate = FrameRate.Approximate(108_000, TimeSpan.FromTicks(36_000_000_001L));

        Assert.True(rate.IsValid);
        Assert.InRange(rate.Value, 29.999_999, 30.000_001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ApproximateRejectsANonPositiveFrameCount(int frameCount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FrameRate.Approximate(frameCount, TimeSpan.FromSeconds(1)));

    [Fact]
    public void ApproximateRejectsAZeroDuration()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FrameRate.Approximate(1, TimeSpan.Zero));

    [Theory]
    [InlineData(30, 1, true)]
    [InlineData(30000, 1001, true)]
    [InlineData(0, 1, false)]
    [InlineData(30, 0, false)]
    [InlineData(-30, 1, false)]
    [InlineData(30, -1, false)]
    public void IsValidRequiresPositiveTerms(int numerator, int denominator, bool expected)
        => Assert.Equal(expected, new FrameRate(numerator, denominator).IsValid);

    [Fact]
    public void GetFrameIndexMatchesTheHostConversion()
    {
        var rate = new FrameRate(30000, 1001);

        for (var index = 0; index < 500; index++)
        {
            var time = TimeSpan.FromTicks(index * 137_913L);
            Assert.Equal(FrameTime.TimeToFrame(time, 30000, 1001), rate.GetFrameIndex(time));
        }
    }

    [Fact]
    public void GetSampleTimeIsTheMiddleOfEachFrame()
    {
        var rate = new FrameRate(30, 1);

        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60), rate.GetSampleTime(0));
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 20), rate.GetSampleTime(1));
    }

    [Theory]
    [InlineData(30, 1, 180)]
    [InlineData(30000, 1001, 120)]
    [InlineData(24000, 1001, 97)]
    [InlineData(1, 1, 3)]
    public void GetSampleTimeStaysInsideItsOwnFrame(int numerator, int denominator, int frameCount)
    {
        var rate = new FrameRate(numerator, denominator);

        for (var index = 0; index < frameCount; index++)
        {
            var sample = rate.GetSampleTime(index);
            var start = (Int128)index * denominator * TimeSpan.TicksPerSecond / numerator;
            var end = (Int128)(index + 1) * denominator * TimeSpan.TicksPerSecond / numerator;
            Assert.True(start <= sample.Ticks && sample.Ticks < end);
        }
    }

    [Fact]
    public void GetSampleTimeIsStrictlyIncreasing()
    {
        var rate = new FrameRate(30000, 1001);
        var previous = TimeSpan.MinValue;

        for (var index = 0; index < 120; index++)
        {
            var sample = rate.GetSampleTime(index);
            Assert.True(sample > previous);
            previous = sample;
        }
    }

    [Fact]
    public void GetSampleTimeDoesNotOverflowForTheLastFrameOfALongVideo()
    {
        var rate = new FrameRate(60000, 1001);

        var sample = rate.GetSampleTime(int.MaxValue - 1);

        Assert.InRange(sample.TotalHours, 9_900d, 10_000d);
    }

    [Fact]
    public void GetSampleTimeRejectsANegativeIndex()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(30, 1).GetSampleTime(-1));

    [Theory]
    [InlineData(30, 1, 0L, 0)]
    [InlineData(30, 1, 3_500_000L, 10)]
    [InlineData(30, 1, 3_666_666L, 10)]
    [InlineData(30, 1, 3_666_667L, 11)]
    [InlineData(30000, 1001, 100_100_000L, 300)]
    [InlineData(30000, 1001, 100_099_999L, 299)]
    [InlineData(30, 1, -1L, 0)]
    [InlineData(30, 1, long.MaxValue, int.MaxValue)]
    public void GetContainingFrameFloorsLikeTheHostDecoders(int numerator, int denominator, long ticks, int expected)
        => Assert.Equal(expected, new FrameRate(numerator, denominator).GetContainingFrame(new TimeSpan(ticks)));

    [Fact]
    public void TheContainingFrameDiffersFromTheRoundedIndexInTheUpperHalf()
    {
        var rate = new FrameRate(30, 1);
        var sample = rate.GetSampleTime(10);

        Assert.Equal(10, rate.GetContainingFrame(sample));
        Assert.Equal(11, rate.GetFrameIndex(sample));
    }

    [Theory]
    [InlineData(30, 1, 300, 100_000_000L)]
    [InlineData(30000, 1001, 300, 100_100_000L)]
    [InlineData(24000, 1001, 1, 417_083L)]
    [InlineData(60, 1, 0, 0L)]
    public void GetFrameStartIsTheExactBeginningOfTheFrame(int numerator, int denominator, int frame, long expectedTicks)
    {
        var rate = new FrameRate(numerator, denominator);

        var start = rate.GetFrameStart(frame);

        Assert.Equal(expectedTicks, start.Ticks);
        Assert.Equal(frame, rate.GetContainingFrame(start + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void GetFrameStartRejectsANegativeIndex()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(30, 1).GetFrameStart(-1));
}
