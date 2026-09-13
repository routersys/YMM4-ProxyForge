namespace ProxyForge.Sources;

internal static class FrameRateResolver
{
    const long ProbeSeconds = 10_000_000L;
    const long MaxProbeIndex = 1_000_000_000L;
    const long BoundaryTolerance = 2L;
    const int VerificationSamples = 7;

    public static FrameRate Resolve(Func<TimeSpan, int> frameIndexOf, int frameCount, TimeSpan duration)
    {
        var fallback = FrameRate.Approximate(frameCount, duration);
        try
        {
            return TryResolveExact(frameIndexOf, fallback.Value, frameCount, duration, out var exact) ? exact : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    static bool TryResolveExact(Func<TimeSpan, int> frameIndexOf, double estimate, int frameCount, TimeSpan duration, out FrameRate rate)
    {
        rate = default;
        if (!double.IsFinite(estimate) || estimate <= 0d)
            return false;

        var target = (long)Math.Clamp(Math.Floor(estimate * ProbeSeconds), 1d, MaxProbeIndex);
        if (!TryFindBoundary(frameIndexOf, target, estimate, out var boundary))
            return false;

        var numerator = (Int128)(2L * target - 1L) * TimeSpan.TicksPerSecond;
        var lowDenominator = 2 * (Int128)(boundary + 1L);
        var highDenominator = 2 * (Int128)(boundary - BoundaryTolerance);
        if (highDenominator <= 0)
            return false;

        var (candidateNumerator, candidateDenominator) = SimplestBetween(numerator, lowDenominator, numerator, highDenominator);
        if (candidateNumerator <= 0 || candidateDenominator <= 0 || candidateNumerator > int.MaxValue || candidateDenominator > int.MaxValue)
            return false;

        var candidate = new FrameRate((int)candidateNumerator, (int)candidateDenominator);
        if (!Verify(frameIndexOf, candidate, frameCount, duration, target, boundary))
            return false;

        rate = candidate;
        return true;
    }

    static bool TryFindBoundary(Func<TimeSpan, int> frameIndexOf, long target, double estimate, out long boundary)
    {
        boundary = 0L;
        if (frameIndexOf(TimeSpan.Zero) >= target)
            return false;

        var limit = TimeSpan.MaxValue.Ticks / 4L;
        var high = (long)Math.Ceiling(target / estimate * TimeSpan.TicksPerSecond) + TimeSpan.TicksPerSecond;
        while (frameIndexOf(new TimeSpan(high)) < target)
        {
            if (high > limit)
                return false;
            high *= 2L;
        }

        var low = 0L;
        while (high - low > 1L)
        {
            var middle = low + (high - low) / 2L;
            if (frameIndexOf(new TimeSpan(middle)) >= target)
                high = middle;
            else
                low = middle;
        }

        boundary = high;
        return true;
    }

    static bool Verify(Func<TimeSpan, int> frameIndexOf, FrameRate candidate, int frameCount, TimeSpan duration, long target, long boundary)
    {
        if (candidate.GetFrameIndex(duration) != frameCount)
            return false;
        if (candidate.GetFrameIndex(new TimeSpan(boundary)) < target)
            return false;
        if (candidate.GetFrameIndex(new TimeSpan(boundary - 1L)) >= target)
            return false;

        for (var sample = 1; sample < VerificationSamples; sample++)
        {
            var time = new TimeSpan(duration.Ticks / VerificationSamples * sample);
            if (frameIndexOf(time) != candidate.GetFrameIndex(time))
                return false;
        }

        return true;
    }

    static (Int128 Numerator, Int128 Denominator) SimplestBetween(Int128 lowNumerator, Int128 lowDenominator, Int128 highNumerator, Int128 highDenominator)
    {
        var floor = lowNumerator / lowDenominator;
        var remainder = lowNumerator - floor * lowDenominator;
        if (remainder == 0)
            return (floor, 1);
        if ((floor + 1) * highDenominator <= highNumerator)
            return (floor + 1, 1);

        var (numerator, denominator) = SimplestBetween(highDenominator, highNumerator - floor * highDenominator, lowDenominator, remainder);
        return (floor * numerator + denominator, numerator);
    }
}
