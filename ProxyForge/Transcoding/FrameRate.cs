namespace ProxyForge.Transcoding;

internal readonly record struct FrameRate(int Numerator, int Denominator)
{
    internal bool IsValid => Numerator > 0 && Denominator > 0;

    internal double Value => (double)Numerator / Denominator;

    internal static FrameRate FromFrameCountAndDuration(int frameCount, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration.Ticks);

        var numerator = frameCount * (long)TimeSpan.TicksPerSecond;
        var denominator = duration.Ticks;

        Reduce(ref numerator, ref denominator);

        while (numerator > int.MaxValue || denominator > int.MaxValue)
        {
            numerator = (numerator + 1) / 2;
            denominator = (denominator + 1) / 2;
            Reduce(ref numerator, ref denominator);
        }

        return new FrameRate((int)numerator, (int)denominator);
    }

    internal static TimeSpan GetSampleTime(int frameIndex, int frameCount, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        var ticks = (2L * frameIndex + 1L) * duration.Ticks / (2L * frameCount);
        return new TimeSpan(ticks);
    }

    private static void Reduce(ref long numerator, ref long denominator)
    {
        var divisor = GreatestCommonDivisor(numerator, denominator);
        if (divisor <= 1)
            return;

        numerator /= divisor;
        denominator /= divisor;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }
}
