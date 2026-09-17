using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// Plain-Stopwatch harness (<c>--quick</c>). Measures, in this order, and prints one compact table at the end:
/// <list type="number">
/// <item>in-process ping-pong latency (two buffers, two threads on two pinned cores) for spin / default / block / async wake modes;</item>
/// <item>cross-process ping-pong latency (the peer is this executable spawned with <c>--peer echo</c>) for the same modes;</item>
/// <item>in-process and cross-process throughput (writer fills, reader sums every element) with buckets of 1000 and 16000 floats
/// through a 64 MiB ring; the sizes do not divide the ring, so buckets regularly wrap through the mirror.</item>
/// </list>
/// Methodology: warm-up is time-based (>= 500 ms, past the JIT tiering delay); spin/default rows time batches of 100 round trips and
/// report per-round percentiles of the batch means (QueryPerformanceCounter ticks every 100 ns and costs ~20 ns per call, so single
/// sub-microsecond rounds cannot be timed individually); kernel-wake rows are timed per round.
/// "spin" vs "block" is the event-wake vs spin-only comparison: v1 has one signaling backend (named events), so the
/// comparison is made through <c>SpinTime</c> (∞ = never touch the kernel; 0 = every wait is a kernel wait).
/// </summary>
internal static class QuickHarness
{
    private const long PingPongRing = 1 << 12;             // elements; one in flight, never full
    private const long ThroughputRing = 1L << 24;          // 16 M floats = 64 MiB
    private const long ThroughputBytes = 2L << 30;         // 2 GiB per run
    private static readonly TimeSpan s_warmup = TimeSpan.FromMilliseconds(500);

    private static int s_coreA = 2;
    private static int s_coreB = 4;

    private sealed record Row(string Scenario, string Mode, string Rounds, string Result, string Detail);

    public static int Run(ReadOnlySpan<string> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cores" && i + 1 < args.Length)
            {
                string[] parts = args[++i].Split(',');
                s_coreA = int.Parse(parts[0], CultureInfo.InvariantCulture);
                s_coreB = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }

        Console.WriteLine(Inv($"photone-ipc quick harness | {RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | {Environment.ProcessorCount} logical cores | cores {s_coreA},{s_coreB}"));
        Console.WriteLine();
        Affinity.Pin(s_coreA, highest: false);
        var total = Stopwatch.StartNew();
        var rows = new List<Row>();

        foreach ((WakeMode mode, int rounds) in new[] { (WakeMode.Spin, 200_000), (WakeMode.Default, 100_000), (WakeMode.Block, 10_000), (WakeMode.Async, 100_000), (WakeMode.AsyncPool, 10_000) })
        {
            rows.Add(InProcessPingPong(mode, rounds));
        }

        foreach ((WakeMode mode, int rounds) in new[] { (WakeMode.Spin, 100_000), (WakeMode.Default, 50_000), (WakeMode.Block, 10_000), (WakeMode.Async, 100_000), (WakeMode.AsyncPool, 10_000) })
        {
            rows.Add(CrossProcessPingPong(mode, rounds));
        }

        foreach (int bucketElements in new[] { 1000, 16000 })
        {
            rows.Add(InProcessThroughput(bucketElements));
            rows.Add(CrossProcessThroughput(bucketElements));
        }

        Console.WriteLine();
        PrintTable(rows);
        Console.WriteLine();
        Console.WriteLine(Inv($"total harness time {total.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }

    /// <summary>
    /// <c>--compare</c>: the cross-process rows of <see cref="Run"/> next to the same measurements over a named pipe and a TCP loopback socket
    /// (<see cref="Transports"/>), all in one session with the same cores and the same peer-process discipline.
    /// </summary>
    public static int RunCompare(ReadOnlySpan<string> args)
    {
        ParseCores(args);
        Console.WriteLine(Inv($"photone-ipc transport comparison | {RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | {Environment.ProcessorCount} logical cores | cores {s_coreA},{s_coreB}"));
        Console.WriteLine();
        Affinity.Pin(s_coreA, highest: false);
        var total = Stopwatch.StartNew();
        var rows = new List<Row>();

        foreach ((WakeMode mode, int rounds) in new[] { (WakeMode.Spin, 100_000), (WakeMode.Default, 50_000), (WakeMode.Block, 10_000), (WakeMode.Async, 100_000), (WakeMode.AsyncPool, 10_000) })
        {
            rows.Add(CrossProcessPingPong(mode, rounds));
        }

        rows.Add(TransportPingPong("named pipe", 10_000, static (rounds, core) => Transports.PipePingPong(rounds, core)));
        rows.Add(TransportPingPong("tcp loopback", 10_000, static (rounds, core) => Transports.TcpPingPong(rounds, core)));

        foreach (int bucketElements in new[] { 1000, 16000 })
        {
            rows.Add(CrossProcessThroughput(bucketElements));
            rows.Add(TransportThroughput("named pipe", bucketElements, Transports.Work.Sum, static (n, buckets, core, w) => Transports.PipeThroughput(n, buckets, core, w)));
            rows.Add(TransportThroughput("tcp loopback", bucketElements, Transports.Work.Sum, static (n, buckets, core, w) => Transports.TcpThroughput(n, buckets, core, w)));
        }

        // a lighter consumer: int buckets, the reader only counts the elements equal to 1 (one vector compare per 8 ints)
        foreach (int bucketElements in new[] { 1000, 16000, 256000 })
        {
            rows.Add(Best(3, () => CrossProcessThroughputInt(bucketElements, Transports.Work.Count1)));
            rows.Add(Best(3, () => TransportThroughput("named pipe", bucketElements, Transports.Work.Count1, static (n, buckets, core, w) => Transports.PipeThroughput(n, buckets, core, w))));
            rows.Add(Best(3, () => TransportThroughput("tcp loopback", bucketElements, Transports.Work.Count1, static (n, buckets, core, w) => Transports.TcpThroughput(n, buckets, core, w))));
        }

        // the transport itself: the producer touches one int per bucket, the consumer makes one vector pass (count the ones)
        foreach (int bucketElements in new[] { 1000, 16000, 256000 })
        {
            rows.Add(Best(3, () => CrossProcessThroughputInt(bucketElements, Transports.Work.Touch1)));
            rows.Add(Best(3, () => TransportThroughput("named pipe", bucketElements, Transports.Work.Touch1, static (n, buckets, core, w) => Transports.PipeThroughput(n, buckets, core, w))));
            rows.Add(Best(3, () => TransportThroughput("tcp loopback", bucketElements, Transports.Work.Touch1, static (n, buckets, core, w) => Transports.TcpThroughput(n, buckets, core, w))));
        }

        // pure protocol: nobody touches the payload (ring buffer only)
        foreach (int bucketElements in new[] { 1000, 16000, 256000 })
        {
            rows.Add(Best(3, () => CrossProcessThroughputInt(bucketElements, Transports.Work.None)));
        }

        Console.WriteLine();
        PrintTable(rows);
        Console.WriteLine();
        Console.WriteLine(Inv($"total comparison time {total.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }

    /// <summary>Runs a throughput row <paramref name="times"/> times and keeps the fastest (the laptop's clocks and thermals make single runs vary by 30 %).</summary>
    private static Row Best(int times, Func<Row> run)
    {
        Row? best = null;
        double bestGbps = -1;
        for (int i = 0; i < times; i++)
        {
            Row r = run();
            double gbps = double.Parse(r.Result[..r.Result.IndexOf(' ', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
            if (gbps > bestGbps)
            {
                bestGbps = gbps;
                best = r;
            }
        }

        return best! with { Rounds = best.Rounds + Inv($" x{times}") };
    }

    private static string WorkLabel(Transports.Work work) => work switch
    {
        Transports.Work.Count1 => "int, fill+count1",
        Transports.Work.Touch1 => "int, touch1+count1",
        Transports.Work.None => "int, protocol only",
        _ => "fill+sum",
    };

    private static void ParseCores(ReadOnlySpan<string> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cores" && i + 1 < args.Length)
            {
                string[] parts = args[++i].Split(',');
                s_coreA = int.Parse(parts[0], CultureInfo.InvariantCulture);
                s_coreB = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }
    }

    private static Row TransportPingPong(string transport, int rounds, Func<int, int, (long[] Ticks, string Detail)> run)
    {
        ThreadPriority old = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        (long[] ticks, string detail) = run(rounds, s_coreB);
        Thread.CurrentThread.Priority = old;
        LatencyStats s = LatencyStats.FromBatches(ticks, 1);
        Console.WriteLine(Inv($"cross-process ping-pong [{transport,-12}] {rounds,7} rounds: RTT {LatencyCell(s, 1)}  ({detail})"));
        return new Row("cross-process ping-pong RTT", transport, rounds.ToString("N0", CultureInfo.InvariantCulture), LatencyCell(s, 1), detail);
    }

    private static Row TransportThroughput(string transport, int bucketElements, Transports.Work work, Func<int, long, int, Transports.Work, (TimeSpan Elapsed, string Detail)> run)
    {
        long buckets = ThroughputBytes / (bucketElements * sizeof(float));
        (TimeSpan elapsed, string detail) = run(bucketElements, buckets, s_coreB, work);
        double bytes = buckets * (double)bucketElements * sizeof(float);
        double gbps = bytes / elapsed.TotalSeconds / 1e9;
        double commitsPerSec = buckets / elapsed.TotalSeconds;
        string mode = Inv($"{bucketElements * sizeof(float) / 1024.0:F1} KiB buckets, {WorkLabel(work)}, {transport}");
        string result = Inv($"{gbps:F2} GB/s, {commitsPerSec / 1e6:F2} M writes/s");
        Console.WriteLine(Inv($"{"cross-process throughput",-24} [{mode}] {buckets,9} buckets in {elapsed.TotalSeconds:F2} s: {result}  ({detail})"));
        return new Row("cross-process throughput", mode, buckets.ToString("N0", CultureInfo.InvariantCulture), result, detail);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(double us) => us < 10 ? us.ToString("F2", CultureInfo.InvariantCulture) : us.ToString("F1", CultureInfo.InvariantCulture);

    private static string LatencyCell(LatencyStats s, int batch)
        => Inv($"p50 {Fmt(s.P50Us)} / p99 {Fmt(s.P99Us)} / p99.9 {Fmt(s.P999Us)} / max {Fmt(s.MaxUs)} us") + (batch > 1 ? Inv($" (batches of {batch})") : string.Empty);

    // ------------------------------------------------------------------ ping-pong

    /// <summary>Thread A sends element i and waits for it to come back; thread B (pinned to the other core) echoes until the sentinel.</summary>
    private static Row InProcessPingPong(WakeMode mode, int rounds)
    {
        using RingBuffer<long> ab = RingBuffer<long>.Create(PingPongRing, null, WakeModes.Writer(mode));
        using RingBuffer<long> ba = RingBuffer<long>.Create(PingPongRing, null, WakeModes.Writer(mode));
        using RingReader<long> fromB = ba.CreateReader(WakeModes.Reader(mode));
        long echoKernelWaits = 0;
        long echoSignals = 0;
        var ready = new ManualResetEventSlim(false);

        var echo = new Thread(() =>
        {
            Affinity.Pin(s_coreB, highest: true);
            using RingReader<long> fromA = ab.CreateReader(WakeModes.Reader(mode));
            ready.Set();                                          // a reader only sees commits made after it joined
            if (WakeModes.IsAsync(mode))
            {
                Peer.EchoLoopAsync(fromA, ba).GetAwaiter().GetResult();
            }
            else
            {
                Peer.EchoLoop(fromA, ba);
            }

            echoKernelWaits = fromA.Counters.KernelWaits;
            echoSignals = fromA.Counters.Signals + ba.Counters.Signals;
        })
        { IsBackground = true, Name = "pingpong-echo" };
        echo.Start();
        ready.Wait();

        ThreadPriority old = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        int batch = WakeModes.Batch(mode);
        long[] ticks = WakeModes.IsAsync(mode)
            ? RunPingPongAsync(ab, fromB, rounds, batch).GetAwaiter().GetResult()
            : RunPingPong(ab, fromB, rounds, batch);
        Thread.CurrentThread.Priority = old;
        echo.Join();

        LatencyStats s = LatencyStats.FromBatches(ticks, batch);
        string detail = Inv($"kernel waits A={fromB.Counters.KernelWaits} B={echoKernelWaits}; signals A={ab.Counters.Signals + fromB.Counters.Signals} B={echoSignals}");
        Console.WriteLine(Inv($"in-process ping-pong  [{WakeModes.Name(mode),-7}] {rounds,7} rounds: RTT {LatencyCell(s, batch)}  ({detail})"));
        return new Row("in-process ping-pong RTT", WakeModes.Name(mode), rounds.ToString("N0", CultureInfo.InvariantCulture), LatencyCell(s, batch), detail);
    }

    /// <summary>Same as <see cref="InProcessPingPong"/>, but the echo side is a second process pinned to the other core.</summary>
    private static Row CrossProcessPingPong(WakeMode mode, int rounds)
    {
        string nameIn = Unique();
        string nameOut = Unique();
        using RingBuffer<long> outbound = RingBuffer<long>.Create(PingPongRing, nameIn, WakeModes.Writer(mode));
        using var peer = PeerProcess.Start("echo", nameIn, nameOut, WakeModes.Name(mode), Inv($"{s_coreB}"));
        string ready = peer.Expect("ready");
        using RingBuffer<long> inbound = RingBuffer<long>.Open(nameOut);
        using RingReader<long> reader = inbound.CreateReader(WakeModes.Reader(mode));

        ThreadPriority old = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        int batch = WakeModes.Batch(mode);
        long[] ticks = WakeModes.IsAsync(mode)
            ? RunPingPongAsync(outbound, reader, rounds, batch).GetAwaiter().GetResult()
            : RunPingPong(outbound, reader, rounds, batch);
        Thread.CurrentThread.Priority = old;

        string done = peer.Expect("done");
        peer.WaitForExit();
        LatencyStats s = LatencyStats.FromBatches(ticks, batch);
        string detail = Inv($"kernel waits A={reader.Counters.KernelWaits}; signals A={outbound.Counters.Signals + reader.Counters.Signals}; peer: {done["done ".Length..]}; {ready["ready ".Length..]}");
        Console.WriteLine(Inv($"cross-process ping-pong [{WakeModes.Name(mode),-7}] {rounds,7} rounds: RTT {LatencyCell(s, batch)}  ({detail})"));
        return new Row("cross-process ping-pong RTT", WakeModes.Name(mode), rounds.ToString("N0", CultureInfo.InvariantCulture), LatencyCell(s, batch), detail);
    }

    private static void Send(RingBuffer<long> outbound, long value)
    {
        using Bucket<long> b = outbound.GetBucket(1);
        b.Span[0] = value;
        b.Commit(1);
    }

    private static void Receive(RingReader<long> inbound, long expected)
    {
        inbound.TryRead(1, out Chunk<long> chunk);
        if (chunk.Data.Span[0] != expected)
        {
            throw new InvalidOperationException(Inv($"ping-pong: got {chunk.Data.Span[0]}, expected {expected}"));
        }

        inbound.Advance(1);
    }

    /// <summary>Warm-up for at least <see cref="s_warmup"/>, then <paramref name="rounds"/> timed rounds in batches; sends the sentinel -1 at the end.</summary>
    private static long[] RunPingPong(RingBuffer<long> outbound, RingReader<long> inbound, int rounds, int batch)
    {
        long i = 0;
        long warmupEnd = Stopwatch.GetTimestamp() + SpinClock.ToTicks(s_warmup);
        while (i < 2000 || Stopwatch.GetTimestamp() < warmupEnd)
        {
            Send(outbound, i);
            WaitReply(inbound, i);
            Receive(inbound, i);
            i++;
        }

        var ticks = new long[rounds / batch];
        for (int b = 0; b < ticks.Length; b++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < batch; k++)
            {
                Send(outbound, i);
                WaitReply(inbound, i);
                Receive(inbound, i);
                i++;
            }

            ticks[b] = Stopwatch.GetTimestamp() - t0;
        }

        Send(outbound, -1);
        return ticks;
    }

    private static void WaitReply(RingReader<long> inbound, long i)
    {
        if (!inbound.WaitSync(1, TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException(Inv($"ping-pong round {i}: no echo within 30 s (status {inbound.Status})"));
        }
    }

    /// <summary>Same as <see cref="RunPingPong"/> through <c>await inbound.Wait(1)</c>; the continuation runs on the thread pool (not pinned), which is part of what this row measures.</summary>
    private static async Task<long[]> RunPingPongAsync(RingBuffer<long> outbound, RingReader<long> inbound, int rounds, int batch)
    {
        long i = 0;
        long warmupEnd = Stopwatch.GetTimestamp() + SpinClock.ToTicks(s_warmup);
        while (i < 2000 || Stopwatch.GetTimestamp() < warmupEnd)
        {
            Send(outbound, i);
            await WaitReplyAsync(inbound, i).ConfigureAwait(false);
            Receive(inbound, i);
            i++;
        }

        var ticks = new long[rounds / batch];
        for (int b = 0; b < ticks.Length; b++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < batch; k++)
            {
                Send(outbound, i);
                await WaitReplyAsync(inbound, i).ConfigureAwait(false);
                Receive(inbound, i);
                i++;
            }

            ticks[b] = Stopwatch.GetTimestamp() - t0;
        }

        Send(outbound, -1);
        return ticks;
    }

    private static async ValueTask WaitReplyAsync(RingReader<long> inbound, long i)
    {
        if (!await inbound.Wait(1, TimeSpan.FromSeconds(30)).ConfigureAwait(false))
        {
            throw new InvalidOperationException(Inv($"ping-pong round {i}: no echo within 30 s (status {inbound.Status})"));
        }
    }

    // ------------------------------------------------------------------ throughput

    /// <summary>Writer on this thread fills every bucket (<c>Span.Fill</c>); a reader thread on the other core sums every element and validates the bucket edges.</summary>
    private static Row InProcessThroughput(int bucketElements)
    {
        long buckets = ThroughputBytes / (bucketElements * sizeof(float));
        using RingBuffer<float> buffer = RingBuffer<float>.Create(ThroughputRing, null);
        long bad = -1;
        long readerKernelWaits = 0;
        long readerSignals = 0;
        var ready = new ManualResetEventSlim(false);
        var drain = new Thread(() =>
        {
            Affinity.Pin(s_coreB, highest: false);
            using RingReader<float> reader = buffer.CreateReader();
            ready.Set();
            (bad, _) = Peer.DrainLoop(reader, bucketElements, buckets);
            readerKernelWaits = reader.Counters.KernelWaits;
            readerSignals = reader.Counters.Signals;
        })
        { IsBackground = true, Name = "throughput-drain" };
        drain.Start();
        ready.Wait();

        var sw = Stopwatch.StartNew();
        WriteBuckets(buffer, bucketElements, buckets);
        drain.Join();
        sw.Stop();

        return ThroughputRow("in-process throughput", bucketElements, buckets, sw.Elapsed, bad, buffer, readerKernelWaits, readerSignals, string.Empty);
    }

    /// <summary>Same as <see cref="InProcessThroughput"/> with the reader in a second process (<c>--peer drain</c>).</summary>
    private static Row CrossProcessThroughput(int bucketElements)
    {
        long buckets = ThroughputBytes / (bucketElements * sizeof(float));
        string name = Unique();
        using RingBuffer<float> buffer = RingBuffer<float>.Create(ThroughputRing, name);
        using var peer = PeerProcess.Start("drain", name, Inv($"{bucketElements}"), Inv($"{buckets}"), "default", Inv($"{s_coreB}"));
        string ready = peer.Expect("ready");

        var sw = Stopwatch.StartNew();
        WriteBuckets(buffer, bucketElements, buckets);
        string done = peer.Expect("done");
        sw.Stop();
        peer.WaitForExit();

        long bad = long.Parse(Field(done, "bad"), CultureInfo.InvariantCulture);
        long kw = long.Parse(Field(done, "kernelWaits"), CultureInfo.InvariantCulture);
        long sig = long.Parse(Field(done, "signals"), CultureInfo.InvariantCulture);
        return ThroughputRow("cross-process throughput", bucketElements, buckets, sw.Elapsed, bad, buffer, kw, sig, ready["ready ".Length..]);
    }

    private static void WriteBuckets(RingBuffer<float> buffer, int bucketElements, long buckets)
    {
        for (long i = 0; i < buckets; i++)
        {
            using Bucket<float> b = buffer.GetBucket(bucketElements);
            b.Span.Fill(i);
            b.Commit(bucketElements);
        }
    }

    /// <summary>Ring buffer of <see cref="int"/> with the peer process as consumer (<c>--peer drain1</c>); the producer side depends on <paramref name="work"/>.</summary>
    private static Row CrossProcessThroughputInt(int bucketElements, Transports.Work work)
    {
        long buckets = ThroughputBytes / (bucketElements * sizeof(int));
        if (work == Transports.Work.None)
        {
            buckets = Math.Max(buckets, 4_000_000);                              // nothing is touched: measure enough commits that the peer handshake (~1 ms) does not matter
        }

        string name = Unique();
        using RingBuffer<int> buffer = RingBuffer<int>.Create(ThroughputRing, name);
        using var peer = PeerProcess.Start("drain1", name, Inv($"{bucketElements}"), Inv($"{buckets}"), "default", Inv($"{s_coreB}"), Transports.WorkName(work));
        string ready = peer.Expect("ready");

        var sw = Stopwatch.StartNew();
        for (long i = 0; i < buckets; i++)
        {
            using Bucket<int> b = buffer.GetBucket(bucketElements);
            switch (work)
            {
                case Transports.Work.Count1:
                    Peer.FillPattern(b.Span, i);
                    break;
                case Transports.Work.Touch1:
                    b.Span[0] = 1;
                    break;
                default:
                    break;                                                       // None: the payload is never touched
            }

            b.Commit(bucketElements);
        }

        string done = peer.Expect("done");
        sw.Stop();
        peer.WaitForExit();

        long bad = long.Parse(Field(done, "bad"), CultureInfo.InvariantCulture);
        long kw = long.Parse(Field(done, "kernelWaits"), CultureInfo.InvariantCulture);
        long sig = long.Parse(Field(done, "signals"), CultureInfo.InvariantCulture);
        return ThroughputRow("cross-process throughput", bucketElements, buckets, sw.Elapsed, bad, work, buffer.Counters, kw, sig, ready["ready ".Length..]);
    }

    private static Row ThroughputRow(string scenario, int bucketElements, long buckets, TimeSpan elapsed, long bad, RingBuffer<float> buffer, long readerKernelWaits, long readerSignals, string extra)
        => ThroughputRow(scenario, bucketElements, buckets, elapsed, bad, Transports.Work.Sum, buffer.Counters, readerKernelWaits, readerSignals, extra);

    private static Row ThroughputRow(string scenario, int bucketElements, long buckets, TimeSpan elapsed, long bad, Transports.Work work, Counters writer, long readerKernelWaits, long readerSignals, string extra)
    {
        if (bad != 0)
        {
            throw new InvalidOperationException(Inv($"{scenario}: {bad} corrupted buckets"));
        }

        double bytes = buckets * (double)bucketElements * sizeof(float);
        double gbps = bytes / elapsed.TotalSeconds / 1e9;
        double commitsPerSec = buckets / elapsed.TotalSeconds;
        string mode = Inv($"{bucketElements * sizeof(float) / 1024.0:F1} KiB buckets, {WorkLabel(work)}");
        string result = work == Transports.Work.None
            ? Inv($"{gbps:F2} GB/s nominal, {commitsPerSec / 1e6:F2} M commits/s = {1e9 / commitsPerSec:F0} ns/commit")
            : Inv($"{gbps:F2} GB/s, {commitsPerSec / 1e6:F2} M commits/s");
        string detail = Inv($"writer kernel waits={writer.KernelWaits} signals={writer.Signals}; reader kernel waits={readerKernelWaits} signals={readerSignals}{(extra.Length > 0 ? "; " + extra : string.Empty)}");
        Console.WriteLine(Inv($"{scenario,-24} [{mode}] {buckets,9} buckets in {elapsed.TotalSeconds:F2} s: {result}  ({detail})"));
        return new Row(scenario, mode, buckets.ToString("N0", CultureInfo.InvariantCulture), result, detail);
    }

    // ------------------------------------------------------------------ helpers

    private static string Unique() => Inv($"photone-bench-{Environment.ProcessId}-{Guid.NewGuid():N}");

    private static string Field(string line, string key)
    {
        int at = line.IndexOf(key + "=", StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(Inv($"field {key} missing in '{line}'"));
        }

        int start = at + key.Length + 1;
        int end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static void PrintTable(List<Row> rows)
    {
        int w0 = Math.Max("scenario".Length, rows.Max(r => r.Scenario.Length));
        int w1 = Math.Max("mode".Length, rows.Max(r => r.Mode.Length));
        int w2 = Math.Max("rounds/buckets".Length, rows.Max(r => r.Rounds.Length));
        int w3 = Math.Max("result".Length, rows.Max(r => r.Result.Length));
        Console.WriteLine(Inv($"| {"scenario".PadRight(w0)} | {"mode".PadRight(w1)} | {"rounds/buckets".PadLeft(w2)} | {"result".PadRight(w3)} | counters |"));
        Console.WriteLine(Inv($"|{new string('-', w0 + 2)}|{new string('-', w1 + 2)}|{new string('-', w2 + 1)}:|{new string('-', w3 + 2)}|----------|"));
        foreach (Row r in rows)
        {
            Console.WriteLine(Inv($"| {r.Scenario.PadRight(w0)} | {r.Mode.PadRight(w1)} | {r.Rounds.PadLeft(w2)} | {r.Result.PadRight(w3)} | {r.Detail} |"));
        }
    }

    /// <summary>Spawns this executable with <c>--peer</c> and talks to it over stdout lines.</summary>
    internal sealed class PeerProcess : IDisposable
    {
        private readonly Process _process;

        private PeerProcess(Process process)
        {
            _process = process;
        }

        public static PeerProcess Start(params string[] args)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.Benchmarks.exe");
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (File.Exists(exe))
            {
                psi.FileName = exe;
            }
            else
            {
                psi.FileName = "dotnet";
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.Benchmarks.dll"));
            }

            psi.ArgumentList.Add("--peer");
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            return new PeerProcess(Process.Start(psi) ?? throw new InvalidOperationException("could not start the peer"));
        }

        public string Expect(string prefix)
        {
            Task<string?> t = _process.StandardOutput.ReadLineAsync();
            if (!t.Wait(TimeSpan.FromSeconds(120)))
            {
                throw new TimeoutException("peer printed nothing within 120 s");
            }

            string line = t.Result ?? throw new InvalidOperationException("peer closed stdout: " + _process.StandardError.ReadToEnd());
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Inv($"peer: expected '{prefix}...', got '{line}'"));
            }

            return line;
        }

        public void WriteLine(string line)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }

        public void WaitForExit()
        {
            if (!_process.WaitForExit(30_000))
            {
                throw new TimeoutException("peer did not exit");
            }

            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException(Inv($"peer exited with {_process.ExitCode}: {_process.StandardError.ReadToEnd()}"));
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }
}
