using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// <c>--lifecycle</c>: what it costs to create, open and release buffers one after another, without and with a <see cref="RingBufferPool"/> (DESIGN §15).
/// For each data size, two scenarios, each measured without a pool and with one (on both sides):
/// <list type="number">
/// <item>in-process: <c>Create</c> then <c>Dispose</c>, nobody else involved;</item>
/// <item>cross-process: the harness creates a buffer, a peer process (this executable, <c>--peer lifecycle-open</c>) opens it by name, joins as a
/// reader, reads one element and releases everything, then the harness disposes its buffer. Both sides time their own calls.</item>
/// </list>
/// Every buffer is pre-faulted (the default), so "create" includes touching every page once unless the pool supplies pages that are already resident.
/// A few warm-up rounds run first (they also fill the pool); the table shows p50 and p90 over the measured rounds.
/// </summary>
internal static class Lifecycle
{
    private static readonly long[] s_sizes = [1L << 16, 1L << 20, 1L << 24, 1L << 26];

    private sealed record Row(string Size, string Scenario, string Mode, int Rounds, string[] Cells);

    public static int Run(ReadOnlySpan<string> args)
    {
        _ = args;
        Console.WriteLine(Inv($"photone-ipc lifecycle harness | {RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | {Environment.ProcessorCount} logical cores"));
        Console.WriteLine();
        var total = Stopwatch.StartNew();
        var rows = new List<Row>();
        foreach (long size in s_sizes)
        {
            int rounds = size <= 1L << 20 ? 300 : size <= 1L << 24 ? 60 : 20;
            rows.Add(InProcess(size, rounds, pooled: false));
            rows.Add(InProcess(size, rounds, pooled: true));
            rows.Add(CrossProcess(size, rounds, pooled: false));
            rows.Add(CrossProcess(size, rounds, pooled: true));
        }

        Console.WriteLine();
        Console.WriteLine("| data size | scenario | pool | rounds | creator: Create | creator: Dispose | peer: Open + CreateReader | peer: release |");
        Console.WriteLine("|---|---|---|---:|---|---|---|---|");
        foreach (Row r in rows)
        {
            Console.WriteLine(Inv($"| {r.Size} | {r.Scenario} | {r.Mode} | {r.Rounds} | {string.Join(" | ", r.Cells)} |"));
        }

        Console.WriteLine();
        Console.WriteLine(Inv($"total lifecycle time {total.Elapsed.TotalSeconds:F1} s (cells: p50 / p90)"));
        return 0;
    }

    private static Row InProcess(long size, int rounds, bool pooled)
    {
        using RingBufferPool? pool = pooled ? new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan }) : null;
        var options = new RingBufferOptions { Pool = pool };
        var create = new List<double>(rounds);
        var dispose = new List<double>(rounds);
        for (int i = -3; i < rounds; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            RingBuffer<byte> buffer = RingBuffer<byte>.Create(size, null, options);
            long t1 = Stopwatch.GetTimestamp();
            buffer.Dispose();
            long t2 = Stopwatch.GetTimestamp();
            if (i >= 0)
            {
                create.Add(Us(t0, t1));
                dispose.Add(Us(t1, t2));
            }
        }

        var row = new Row(SizeLabel(size), "in-process", pooled ? "yes" : "no", rounds, [Cell(create), Cell(dispose), "", ""]);
        Console.WriteLine(Inv($"{row.Size,8} in-process    pool={row.Mode,-3}: Create {row.Cells[0]}, Dispose {row.Cells[1]}"));
        return row;
    }

    private static Row CrossProcess(long size, int rounds, bool pooled)
    {
        using RingBufferPool? pool = pooled ? new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan }) : null;
        var options = new RingBufferOptions { Pool = pool };
        using QuickHarness.PeerProcess peer = QuickHarness.PeerProcess.Start("lifecycle-open", pooled ? "pool" : "nopool");
        peer.Expect("ready");
        var create = new List<double>(rounds);
        var dispose = new List<double>(rounds);
        var open = new List<double>(rounds);
        var release = new List<double>(rounds);
        long revived = 0;
        for (int i = -3; i < rounds; i++)
        {
            string name = Inv($"photone-bench-{Environment.ProcessId}-{Guid.NewGuid():N}");
            long t0 = Stopwatch.GetTimestamp();
            RingBuffer<byte> buffer = RingBuffer<byte>.Create(size, name, options);
            long t1 = Stopwatch.GetTimestamp();
            peer.WriteLine("open " + name);
            string joined = peer.Expect("joined ");
            using (Bucket<byte> bucket = buffer.GetBucket(1))
            {
                bucket.Span[0] = 42;
                bucket.Commit(1);
            }

            string released = peer.Expect("released ");
            long t2 = Stopwatch.GetTimestamp();
            buffer.Dispose();
            long t3 = Stopwatch.GetTimestamp();
            if (i >= 0)
            {
                create.Add(Us(t0, t1));
                dispose.Add(Us(t2, t3));
                open.Add(double.Parse(Field(joined, "us"), CultureInfo.InvariantCulture));
                release.Add(double.Parse(Field(released, "us"), CultureInfo.InvariantCulture));
                revived = long.Parse(Field(released, "revived"), CultureInfo.InvariantCulture);
            }
        }

        peer.WriteLine("exit");
        peer.Expect("done");
        peer.WaitForExit();
        var row = new Row(SizeLabel(size), "cross-process", pooled ? "yes" : "no", rounds, [Cell(create), Cell(dispose), Cell(open), Cell(release)]);
        Console.WriteLine(Inv($"{row.Size,8} cross-process pool={row.Mode,-3}: Create {row.Cells[0]}, Dispose {row.Cells[1]}, peer Open {row.Cells[2]}, peer release {row.Cells[3]} (peer revived {revived}, creator reused {pool?.ReusedCount ?? 0})"));
        return row;
    }

    /// <summary>
    /// Peer side (<c>--peer lifecycle-open pool|nopool</c>): for every "open &lt;name&gt;" line opens the buffer (with its own pool when asked), creates a
    /// reader and prints "joined us=..", waits for one element, releases reader and buffer and prints "released us=.. revived=..".
    /// </summary>
    public static int PeerOpenLoop(ReadOnlySpan<string> args)
    {
        using RingBufferPool? pool = args.Length > 1 && args[1] == "pool" ? new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan }) : null;
        var options = new RingBufferOptions { Pool = pool };
        Print("ready");
        while (Console.In.ReadLine() is string line && line != "exit")
        {
            string name = line["open ".Length..];
            long t0 = Stopwatch.GetTimestamp();
            RingBuffer<byte> buffer = RingBuffer<byte>.Open(name, options);
            RingReader<byte> reader = buffer.CreateReader();
            long t1 = Stopwatch.GetTimestamp();
            Print(Inv($"joined us={Us(t0, t1):F1}"));
            if (!reader.WaitSync(1) || !reader.TryRead(1, out Chunk<byte> chunk) || chunk.Span[0] != 42)
            {
                throw new InvalidOperationException("the element did not arrive");
            }

            reader.Advance(1);
            long t2 = Stopwatch.GetTimestamp();
            reader.Dispose();
            buffer.Dispose();
            long t3 = Stopwatch.GetTimestamp();
            Print(Inv($"released us={Us(t2, t3):F1} revived={pool?.RevivedCount ?? 0}"));
        }

        Print("done");
        return 0;
    }

    private static void Print(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static double Us(long from, long to) => (to - from) * 1e6 / Stopwatch.Frequency;

    private static string Cell(List<double> us)
    {
        us.Sort();
        return Inv($"{Fmt(us[us.Count / 2])} / {Fmt(us[(int)Math.Min(us.Count - 1, Math.Round(0.9 * (us.Count - 1)))])}");
    }

    private static string Fmt(double us) => us >= 1000
        ? (us / 1000).ToString("F1", CultureInfo.InvariantCulture) + " ms"
        : us.ToString(us < 10 ? "F2" : "F0", CultureInfo.InvariantCulture) + " us";

    private static string SizeLabel(long bytes) => bytes >= 1L << 20 ? Inv($"{bytes >> 20} MiB") : Inv($"{bytes >> 10} KiB");

    private static string Field(string line, string key)
    {
        foreach (string token in line.Split(' '))
        {
            if (token.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return token[(key.Length + 1)..];
            }
        }

        throw new InvalidOperationException(Inv($"field {key} missing in '{line}'"));
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
