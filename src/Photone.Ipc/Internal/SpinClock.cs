using System.Diagnostics;
using Windows.Win32;

namespace Photone.Ipc.Internal;

/// <summary>Stopwatch-bounded spin budget helper (DESIGN §5.3, §5.5). A negative budget (<see cref="Timeout.InfiniteTimeSpan"/>) never expires.</summary>
internal struct SpinClock
{
    private readonly long _deadline;
    private long _lastTick;

    private SpinClock(long deadline, long now)
    {
        _deadline = deadline;
        _lastTick = now;
    }

    /// <summary>Starts a clock that expires after <paramref name="budget"/> (never, when negative).</summary>
    public static SpinClock Start(TimeSpan budget)
    {
        long now = Stopwatch.GetTimestamp();
        long deadline = budget < TimeSpan.Zero ? long.MaxValue : now + ToTicks(budget);
        return new SpinClock(deadline, now);
    }

    /// <summary><see langword="true"/> once the budget has elapsed.</summary>
    public readonly bool Expired => _deadline != long.MaxValue && Stopwatch.GetTimestamp() >= _deadline;

    /// <summary>Returns <see langword="true"/> at most once per <paramref name="interval"/> (used to rate-limit re-scans inside a spin loop).</summary>
    public bool Tick(TimeSpan interval)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastTick < ToTicks(interval))
        {
            return false;
        }

        _lastTick = now;
        return true;
    }

    /// <summary>Converts a time span to Stopwatch ticks (saturating).</summary>
    public static long ToTicks(TimeSpan span)
    {
        double ticks = span.Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    /// <summary>Absolute Stopwatch deadline for <paramref name="timeout"/>; <see cref="long.MaxValue"/> for an infinite timeout.</summary>
    public static long ToDeadline(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            return long.MaxValue;
        }

        long ticks = ToTicks(timeout);
        long now = Stopwatch.GetTimestamp();
        return ticks >= long.MaxValue - now ? long.MaxValue : now + ticks;
    }

    /// <summary>Milliseconds remaining until <paramref name="deadline"/> (rounded up), or <see cref="Windows.Win32.PInvoke.INFINITE"/> for an infinite deadline.</summary>
    public static uint RemainingMs(long deadline)
    {
        if (deadline == long.MaxValue)
        {
            return PInvoke.INFINITE;
        }

        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return 0;
        }

        double ms = remaining * 1000.0 / Stopwatch.Frequency;
        return ms >= PInvoke.INFINITE - 1 ? PInvoke.INFINITE - 1 : (uint)Math.Ceiling(ms);
    }
}
