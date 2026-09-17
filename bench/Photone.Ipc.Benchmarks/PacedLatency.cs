using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Photone.Ipc.Internal;

// CS0436: this project generates its own CsWin32 types while the library's (internal, visible through InternalsVisibleTo) carry the same names;
//         the compiler prefers the ones from this project's source, which is what these helpers want.
#pragma warning disable CS0436

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// <c>--latency</c>: delivery latency and consumer CPU under paced traffic, cross-process.
/// The harness process is the writer: it busy-waits to a fixed schedule (one message every <c>interval</c>) and publishes
/// <c>Stopwatch.GetTimestamp()</c> taken right before <c>Commit</c>. The peer process (<c>--peer paced</c>) is the reader: for every message it
/// records <c>now - stamp</c> (QueryPerformanceCounter is machine-wide, so the difference is meaningful across processes) and, over the
/// measured window, its own process CPU time divided by wall time. This is the metric the wait/signal policy actually trades:
/// latency when the next message arrives after a gap, against CPU burnt while waiting for it.
/// </summary>
internal static class PacedLatency
{

    /// <summary>CPU cycles this process has consumed (exact, unlike the tick-sampled process times that miss or overcharge short wake-ups).</summary>
    private static ulong Cycles()
    {
        PInvoke.QueryProcessCycleTime(PInvoke.GetCurrentProcess(), out ulong c);
        return c;
    }

    /// <summary>Cycles one busy core consumes per Stopwatch tick, measured by spinning for 200 ms.</summary>
    private static double CalibrateCyclesPerTick()
    {
        long t0 = Stopwatch.GetTimestamp();
        ulong c0 = Cycles();
        long end = t0 + Stopwatch.Frequency / 5;
        while (Stopwatch.GetTimestamp() < end)
        {
            Thread.SpinWait(20);
        }

        return (Cycles() - c0) / (double)(Stopwatch.GetTimestamp() - t0);
    }

    private static readonly long[] s_defaultIntervalsUs = [10, 50, 200, 1_000, 5_000];

    /// <summary>Reader configurations, each fully explicit so that a row keeps its meaning when library defaults change.</summary>
    private static readonly string[] s_defaultModes =
        ["sync-block", "sync-20us", "sync-max1ms", "sync-max5ms", "sync-busy", "async-pool-20us", "async-pool-max1ms", "async-inline-20us", "async-inline-max1ms"];

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static int Run(ReadOnlySpan<string> args)
    {
        int coreA = 2;
        int coreB = 4;
        long[] intervals = s_defaultIntervalsUs;
        string[] modes = s_defaultModes;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cores" when i + 1 < args.Length:
                    string[] parts = args[++i].Split(',');
                    coreA = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    coreB = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--modes" when i + 1 < args.Length:
                    modes = args[++i].Split(',');
                    break;
                case "--intervals" when i + 1 < args.Length:
                    intervals = args[++i].Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    break;
            }
        }

        Console.WriteLine(Inv($"photone-ipc paced latency | {RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | {Environment.ProcessorCount} logical cores | writer core {coreA}, reader core {coreB}"));
        Console.WriteLine("cell = delivery latency p50 / p99 (us) and reader process CPU (% of one core, from CPU cycle counts) over the measured window");
        Console.WriteLine();
        Affinity.Pin(coreA, highest: true);
        var total = Stopwatch.StartNew();
        var cells = new string[modes.Length, intervals.Length];
        for (int m = 0; m < modes.Length; m++)
        {
            for (int k = 0; k < intervals.Length; k++)
            {
                Result r = Measure(modes[m], intervals[k], coreA, coreB);
                cells[m, k] = Inv($"{Fmt(r.P50Us)} / {Fmt(r.P99Us)} us, {r.CpuPercent:F0}%");
                Console.WriteLine(Inv($"{modes[m],-24} every {intervals[k],6} us: {r.Samples,6} msgs  p50 {Fmt(r.P50Us)}  p90 {Fmt(r.P90Us)}  p99 {Fmt(r.P99Us)}  p99.9 {Fmt(r.P999Us)}  max {Fmt(r.MaxUs)} us  cpu {r.CpuPercent:F1}%  ({r.Detail})"));
            }
        }

        Console.WriteLine();
        var header = new List<string> { "reader mode" };
        header.AddRange(intervals.Select(i => i >= 1000 ? Inv($"every {i / 1000.0:0.#} ms") : Inv($"every {i} us")));
        Console.WriteLine("| " + string.Join(" | ", header) + " |");
        Console.WriteLine("|" + string.Concat(Enumerable.Repeat("---|", header.Count)));
        for (int m = 0; m < modes.Length; m++)
        {
            var row = new List<string> { modes[m] };
            for (int k = 0; k < intervals.Length; k++)
            {
                row.Add(cells[m, k]);
            }

            Console.WriteLine("| " + string.Join(" | ", row) + " |");
        }

        Console.WriteLine();
        Console.WriteLine(Inv($"total latency run {total.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }

    private static string Fmt(double us) => us < 10 ? us.ToString("F2", CultureInfo.InvariantCulture) : us.ToString("F1", CultureInfo.InvariantCulture);

    private readonly record struct Result(int Samples, double P50Us, double P90Us, double P99Us, double P999Us, double MaxUs, double CpuPercent, string Detail);

    private static Result Measure(string mode, long intervalUs, int coreA, int coreB)
    {
        const double MeasureSeconds = 1.5;
        const double WarmupSeconds = 0.4;
        int measured = (int)Math.Clamp(MeasureSeconds * 1e6 / intervalUs, 300, 60_000);
        int warmup = (int)Math.Clamp(WarmupSeconds * 1e6 / intervalUs, 80, 20_000);
        string name = Inv($"photone-bench-{Environment.ProcessId}-{Guid.NewGuid():N}");
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
        using var peer = QuickHarness.PeerProcess.Start("paced", name, mode, Inv($"{coreA}"), Inv($"{coreB}"), Inv($"{warmup + measured}"), Inv($"{warmup}"));
        peer.Expect("ready");

        long interval = SpinClock.ToTicks(TimeSpan.FromTicks(intervalUs * 10));
        long next = Stopwatch.GetTimestamp() + interval;
        for (int i = 0; i < warmup + measured; i++)
        {
            long now;
            while ((now = Stopwatch.GetTimestamp()) < next)
            {
                Thread.SpinWait(1);                                         // busy pacing: the writer's core never sleeps, the reader's may
            }

            using (Bucket<long> b = buffer.GetBucket(1))
            {
                b.Span[0] = Stopwatch.GetTimestamp();
                b.Commit(1);
            }

            next += interval;
            if (now - next > 20 * interval)
            {
                next = now + interval;                                      // resynchronise after a stall instead of bursting
            }
        }

        string done = peer.Expect("done");
        peer.WaitForExit();
        double F(string key) => double.Parse(Field(done, key), CultureInfo.InvariantCulture);
        return new Result(measured, F("p50"), F("p90"), F("p99"), F("p999"), F("max"), F("cpu"),
            Inv($"kernelWaits={Field(done, "kernelWaits")} spinSuccesses={Field(done, "spinSuccesses")} armSignals={Field(done, "armSignals")}"));
    }

    private static string Field(string line, string key)
    {
        int at = line.IndexOf(" " + key + "=", StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(Inv($"field {key} missing in '{line}'"));
        }

        int start = at + key.Length + 2;
        int end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    // ------------------------------------------------------------------ peer

    /// <summary>Reader options per mode (explicit, independent of library defaults).</summary>
    internal static ReaderOptions Options(string mode)
    {
        TimeSpan us20 = TimeSpan.FromMicroseconds(20);
        TimeSpan us5 = TimeSpan.FromMicroseconds(5);
        return mode switch
        {
            "sync-block" => new ReaderOptions { SpinTime = TimeSpan.Zero, MaxSpinTime = TimeSpan.Zero },
            "sync-20us" => new ReaderOptions { SpinTime = us20, MaxSpinTime = us20 },
            "sync-max1ms" => new ReaderOptions { SpinTime = us20, MaxSpinTime = TimeSpan.FromMilliseconds(1) },
            "sync-max5ms" => new ReaderOptions { SpinTime = us20, MaxSpinTime = TimeSpan.FromMilliseconds(5) },
            "sync-busy" => new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan },
            "async-pool-20us" => new ReaderOptions { SpinTime = us20, MaxSpinTime = us20, AsyncSpinTime = us5, AllowSynchronousContinuations = false },
            "async-pool-max1ms" => new ReaderOptions { SpinTime = us20, MaxSpinTime = TimeSpan.FromMilliseconds(1), AsyncSpinTime = us5, AllowSynchronousContinuations = false },
            "async-inline-20us" => new ReaderOptions { SpinTime = us20, MaxSpinTime = us20, AsyncSpinTime = us5, AllowSynchronousContinuations = true },
            "async-inline-max1ms" => new ReaderOptions { SpinTime = us20, MaxSpinTime = TimeSpan.FromMilliseconds(1), AsyncSpinTime = us5, AllowSynchronousContinuations = true },
            _ => throw new ArgumentException("unknown latency mode " + mode),
        };
    }

    /// <summary><c>paced &lt;name&gt; &lt;mode&gt; &lt;writerCore&gt; &lt;readerCore&gt; &lt;total&gt; &lt;warmup&gt;</c></summary>
    public static int Peer(ReadOnlySpan<string> a)
    {
        string name = a[1];
        string mode = a[2];
        int coreA = int.Parse(a[3], CultureInfo.InvariantCulture);
        int coreB = int.Parse(a[4], CultureInfo.InvariantCulture);
        int total = int.Parse(a[5], CultureInfo.InvariantCulture);
        int warmup = int.Parse(a[6], CultureInfo.InvariantCulture);

        // Same treatment for every mode: every thread of this process (consumer, waiter, thread pool) may run on any core except the writer's
        // physical core, and the whole process runs in the High priority class so that browser/OS noise does not decide the tail.
        // (Pinning only the synchronous consumer would favour the sync modes: the async waiter and pool threads cannot be pinned the same way.)
        _ = coreB;
        long mask = 0;
        for (int c = 0; c < Math.Min(Environment.ProcessorCount, 64); c++)
        {
            if (c / 2 != coreA / 2)
            {
                mask |= 1L << c;
            }
        }

        using (Process self = Process.GetCurrentProcess())
        {
            self.ProcessorAffinity = (nint)mask;
            self.PriorityClass = ProcessPriorityClass.High;
        }

        bool async = mode.StartsWith("async", StringComparison.Ordinal);

        double cyclesPerTick = CalibrateCyclesPerTick();
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        using RingReader<long> reader = buffer.CreateReader(Options(mode));
        Console.Out.WriteLine("ready");
        Console.Out.Flush();

        var samples = new long[total - warmup];
        State st = async ? ConsumeAsync(reader, samples, total, warmup).GetAwaiter().GetResult() : Consume(reader, samples, total, warmup);

        double cpu = (st.Cpu1 - st.Cpu0) / (cyclesPerTick * (st.Wall1 - st.Wall0)) * 100.0;   // whole process, in cores (100% = one core busy)
        Array.Sort(samples);
        double toUs = 1e6 / Stopwatch.Frequency;
        double P(double p) => samples[(int)Math.Min(samples.Length - 1, Math.Round(p * (samples.Length - 1)))] * toUs;
        Counters c2 = reader.Counters;
        Console.Out.WriteLine(Inv($"done p50={P(0.50):F3} p90={P(0.90):F3} p99={P(0.99):F3} p999={P(0.999):F3} max={samples[^1] * toUs:F3} cpu={cpu:F2} kernelWaits={c2.KernelWaits} spinSuccesses={c2.SpinSuccesses} armSignals={c2.ArmSignals}"));
        Console.Out.Flush();
        return 0;
    }

    private struct State
    {
        public ulong Cpu0;
        public ulong Cpu1;
        public long Wall0;
        public long Wall1;
    }

    private static State Consume(RingReader<long> reader, long[] samples, int total, int warmup)
    {
        var st = default(State);
        for (int i = 0; i < total; i++)
        {
            if (i == warmup)
            {
                st.Cpu0 = Cycles();
                st.Wall0 = Stopwatch.GetTimestamp();
            }

            if (!reader.WaitSync(1))
            {
                throw new InvalidOperationException(Inv($"writer went away at message {i} (status {reader.Status})"));
            }

            reader.TryRead(1, out Chunk<long> chunk);
            long latency = Stopwatch.GetTimestamp() - chunk.Span[0];
            reader.Advance(1);
            if (i >= warmup)
            {
                samples[i - warmup] = latency;
            }
        }

        st.Wall1 = Stopwatch.GetTimestamp();
        st.Cpu1 = Cycles();
        return st;
    }

    private static async Task<State> ConsumeAsync(RingReader<long> reader, long[] samples, int total, int warmup)
    {
        var st = default(State);
        for (int i = 0; i < total; i++)
        {
            if (i == warmup)
            {
                st.Cpu0 = Cycles();
                st.Wall0 = Stopwatch.GetTimestamp();
            }

            if (!await reader.Wait(1).ConfigureAwait(false))
            {
                throw new InvalidOperationException(Inv($"writer went away at message {i} (status {reader.Status})"));
            }

            long latency = Take(reader);
            if (i >= warmup)
            {
                samples[i - warmup] = latency;
            }
        }

        st.Wall1 = Stopwatch.GetTimestamp();
        st.Cpu1 = Cycles();
        return st;
    }

    private static long Take(RingReader<long> reader)
    {
        reader.TryRead(1, out Chunk<long> chunk);
        long latency = Stopwatch.GetTimestamp() - chunk.Span[0];
        reader.Advance(1);
        return latency;
    }
}
