namespace Photone.Ipc.Internal;

/// <summary>
/// Two-phase waiting with an adaptive spin window: spin for <see cref="Window"/>, then block in the kernel.
/// The window is derived from the waits this party actually observes (their duration from start to satisfaction, whether spun or blocked):
/// <list type="bullet">
/// <item><b>Whether to spin at all</b>: an exponentially weighted share of recent waits that completed within <c>max</c> (weight 1/8). Spinning is on while
/// at least half of them did. A single short wait in a stream of long ones (wake-up jitter) cannot switch spinning on, and a single long gap in a dense
/// stream cannot switch it off.</item>
/// <item><b>How long to spin</b>: twice a slowly decaying maximum of recent short waits, clamped to [<c>initial</c>, <c>max</c>], so a steady gap is covered
/// with room for jitter while a spin that finds its data early still ends early.</item>
/// </list>
/// With <c>max == initial</c> (the default) the window is either the configured spin time or zero: the reader keeps its sub-microsecond latency while most
/// gaps are shorter than that, and stops wasting the spin when they are not. With <c>max &gt; initial</c> it spins through any steady gap up to <c>max</c>
/// (latency for CPU). A negative initial spin means spin forever; zero for both means never spin. Not thread-safe: one instance per waiting party.
/// </summary>
internal struct SpinPolicy
{
    private const int One = 256;                // fixed-point 1.0 of the short-wait share
    private const int Half = One / 2;

    private readonly long _initial;
    private readonly long _max;
    private readonly bool _fixed;               // spin forever, or never spin: nothing to learn
    private long _window;
    private long _envelope;                     // decaying maximum of recent short waits (ticks)
    private int _shortShare;                    // EWMA of "the wait completed within _max", 0..One

    public SpinPolicy(TimeSpan initial, TimeSpan? max)
    {
        if (initial < TimeSpan.Zero)
        {
            _initial = _max = _window = long.MaxValue;
            _fixed = true;
            _envelope = 0;
            _shortShare = One;
            return;
        }

        _initial = SpinClock.ToTicks(initial);
        long cap = max is { } m ? (m < TimeSpan.Zero ? long.MaxValue / 4 : SpinClock.ToTicks(m)) : _initial;
        _max = Math.Max(cap, _initial);
        _fixed = _max == 0;
        _window = _initial;
        _envelope = _initial / 2;
        _shortShare = One;                      // optimistic start: the first waits spin the configured time
    }

    /// <summary>Current spin budget in Stopwatch ticks; <see cref="long.MaxValue"/> = spin forever; 0 = block immediately.</summary>
    public readonly long Window => _window;

    /// <summary>Upper bound of the window in Stopwatch ticks.</summary>
    public readonly long Max => _max;

    /// <summary><see langword="true"/> when the window can change (callers skip the clock reads otherwise).</summary>
    public readonly bool IsAdaptive => !_fixed;

    /// <summary>Records a wait that was satisfied <paramref name="waitedTicks"/> after it started (spun or blocked; not timed out).</summary>
    public void OnSatisfied(long waitedTicks)
    {
        if (_fixed)
        {
            return;
        }

        bool isShort = waitedTicks <= _max;
        _shortShare += ((isShort ? One : 0) - _shortShare) >> 3;
        if (isShort)
        {
            long decayed = _envelope - (_envelope >> 3);
            _envelope = waitedTicks > decayed ? waitedTicks : decayed;
        }

        if (_shortShare < Half)
        {
            _window = 0;
            return;
        }

        long w = _envelope * 2;
        _window = w < _initial ? _initial : w > _max ? _max : w;
    }
}
