using YukkuriMovieMaker.Commons;

namespace ProxyForge.Sources;

internal readonly record struct FrameRate(int Numerator, int Denominator)
{
    public bool IsValid => Numerator > 0 && Denominator > 0;

    public double Value => (double)Numerator / Denominator;

    public int GetFrameIndex(TimeSpan time) => FrameTime.TimeToFrame(time, Numerator, Denominator);

    public TimeSpan GetSampleTime(int frameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        var ticks = (Int128)(2L * frameIndex + 1L) * Denominator * TimeSpan.TicksPerSecond / (2L * Numerator);
        return new TimeSpan((long)ticks);
    }

    public static FrameRate Approximate(int frameCount, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration.Ticks);

        var numerator = frameCount * TimeSpan.TicksPerSecond;
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

    static void Reduce(ref long numerator, ref long denominator)
    {
        var divisor = GreatestCommonDivisor(numerator, denominator);
        if (divisor <= 1)
            return;

        numerator /= divisor;
        denominator /= divisor;
    }

    static long GreatestCommonDivisor(long left, long right)
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
