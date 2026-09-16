using System.Diagnostics;

namespace Photone.Ipc.Benchmarks;

/// <summary>Latency percentiles over per-sample microsecond values.</summary>
internal readonly record struct LatencyStats(double MinUs, double P50Us, double P90Us, double P99Us, double P999Us, double MaxUs)
{
    /// <summary>
    /// Percentiles of per-round latency where every sample is the mean of a batch of <paramref name="roundsPerBatch"/> rounds. Batching is
    /// what makes sub-microsecond numbers meaningful: QueryPerformanceCounter ticks every 100 ns and costs ~20 ns per call, so a single
    /// 200 ns round trip cannot be timed on its own. With batch = 1 this is a plain per-sample histogram.
    /// </summary>
    public static LatencyStats FromBatches(long[] batchTicks, int roundsPerBatch)
    {
        var us = new double[batchTicks.Length];
        double toUs = 1e6 / Stopwatch.Frequency / roundsPerBatch;
        for (int i = 0; i < us.Length; i++)
        {
            us[i] = batchTicks[i] * toUs;
        }

        Array.Sort(us);
        return new LatencyStats(us[0], Percentile(us, 0.50), Percentile(us, 0.90), Percentile(us, 0.99), Percentile(us, 0.999), us[^1]);
    }

    private static double Percentile(double[] sorted, double p)
    {
        int idx = (int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)));
        return sorted[idx];
    }
}

/// <summary>Spin/blocking mode of the waiting side, mapped onto <c>SpinTime</c> (the v1 signaling layer has a single backend).</summary>
internal enum WakeMode
{
    /// <summary>Spin forever; no kernel object is ever touched.</summary>
    Spin,

    /// <summary>Library defaults (20 µs spin, then a kernel wait).</summary>
    Default,

    /// <summary>Block immediately (<c>SpinTime = 0</c>): every wait is a kernel wait, every wake a <c>SetEvent</c>.</summary>
    Block,

    /// <summary>The sketch's <c>await reader.Wait(n)</c> with the library defaults (adaptive spin, continuation inline on the reader's waiter thread).</summary>
    Async,

    /// <summary><c>await reader.Wait(n)</c> with no spin and thread-pool continuations: every wait suspends through the waiter thread and the pool (the v1 async path).</summary>
    AsyncPool,
}

internal static class WakeModes
{
    public static WakeMode Parse(string s) => s switch
    {
        "spin" => WakeMode.Spin,
        "default" => WakeMode.Default,
        "block" => WakeMode.Block,
        "async" => WakeMode.Async,
        "async-pool" => WakeMode.AsyncPool,
        _ => throw new ArgumentException("unknown wake mode " + s),
    };

    public static string Name(WakeMode m) => m switch
    {
        WakeMode.Spin => "spin",
        WakeMode.Default => "default",
        WakeMode.Block => "block",
        WakeMode.Async => "async",
        _ => "async-pool",
    };

    /// <summary>Awaiting rows (the ping-pong loops use <c>await reader.Wait</c>).</summary>
    public static bool IsAsync(WakeMode m) => m is WakeMode.Async or WakeMode.AsyncPool;

    public static TimeSpan SpinTime(WakeMode m) => m switch
    {
        WakeMode.Spin => Timeout.InfiniteTimeSpan,
        WakeMode.Default or WakeMode.Async => TimeSpan.FromMicroseconds(20),
        _ => TimeSpan.Zero,
    };

    /// <summary>Rounds per timing batch: kernel-wake modes are slow enough to be timed one by one.</summary>
    public static int Batch(WakeMode m) => m is WakeMode.Spin or WakeMode.Default or WakeMode.Async ? 100 : 1;

    public static ReaderOptions Reader(WakeMode m) => m switch
    {
        WakeMode.Async => new ReaderOptions(),
        WakeMode.AsyncPool => new ReaderOptions { SpinTime = TimeSpan.Zero, AsyncSpinTime = TimeSpan.Zero, AllowSynchronousContinuations = false },
        _ => new ReaderOptions { SpinTime = SpinTime(m), AsyncSpinTime = SpinTime(m) },
    };

    public static RingBufferOptions Writer(WakeMode m) => m == WakeMode.Async ? new RingBufferOptions() : new RingBufferOptions { SpinTime = SpinTime(m) };
}
