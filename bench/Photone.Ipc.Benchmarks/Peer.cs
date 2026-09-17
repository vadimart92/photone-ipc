using System.Globalization;
using System.Numerics;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// The cross-process side of the quick harness: the harness spawns <em>this executable</em> with <c>--peer &lt;verb&gt; ...</c>.
/// Every verb prints <c>ready</c> on stdout once it has joined the buffer and <c>done ...</c> (with its <see cref="Counters"/>) at the end.
/// </summary>
internal static class Peer
{
    public static int Run(ReadOnlySpan<string> args)
    {
        try
        {
            return args[0] switch
            {
                "echo" => Echo(args),
                "drain" => Drain(args),
                "drain1" => Drain1(args),
                "paced" => PacedLatency.Peer(args),
                "pipe-echo" => Transports.PipeEcho(args),
                "tcp-echo" => Transports.TcpEcho(args),
                "pipe-drain" => Transports.PipeDrain(args),
                "tcp-drain" => Transports.TcpDrain(args),
                "lifecycle-open" => Lifecycle.PeerOpenLoop(args),
                _ => throw new ArgumentException("unknown peer verb " + args[0]),
            };
        }
        catch (Exception ex)
        {
            Print("error " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static void Print(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>echo &lt;nameIn&gt; &lt;nameOut&gt; &lt;mode&gt; &lt;core&gt;</c>: returns every 8-byte element it receives until it receives -1.</summary>
    private static int Echo(ReadOnlySpan<string> a)
    {
        string nameIn = a[1];
        string nameOut = a[2];
        WakeMode mode = WakeModes.Parse(a[3]);
        int core = int.Parse(a[4], CultureInfo.InvariantCulture);
        Affinity.Pin(core, highest: true);

        using RingBuffer<long> inBuf = RingBuffer<long>.Open(nameIn);
        using RingReader<long> reader = inBuf.CreateReader(WakeModes.Reader(mode));
        using RingBuffer<long> outBuf = RingBuffer<long>.Create(1 << 12, nameOut, WakeModes.Writer(mode));
        Print(Inv($"ready atCreator={inBuf.IsMappedAtCreatorAddress}"));

        long rounds = WakeModes.IsAsync(mode)
            ? EchoLoopAsync(reader, outBuf).GetAwaiter().GetResult()
            : EchoLoop(reader, outBuf);
        if (rounds < 0)
        {
            Print(Inv($"eof status={reader.Status}"));
            return 3;
        }

        Counters c = reader.Counters;
        Print(Inv($"done rounds={rounds} kernelWaits={c.KernelWaits} signals={c.Signals + outBuf.Counters.Signals} spurious={c.SpuriousWakes}"));
        return 0;
    }

    /// <summary>Echoes until the sentinel -1 arrives; returns the number of echoed rounds, or -1 if the writer went away first.</summary>
    internal static long EchoLoop(RingReader<long> reader, RingBuffer<long> outBuf)
    {
        long rounds = 0;
        while (true)
        {
            if (!reader.WaitSync(1))
            {
                return -1;
            }

            reader.TryRead(1, out Chunk<long> chunk);
            long v = chunk.Data.Span[0];
            reader.Advance(1);
            if (v == -1)
            {
                return rounds;
            }

            using Bucket<long> b = outBuf.GetBucket(1);
            b.Span[0] = v;
            b.Commit(1);
            rounds++;
        }
    }

    /// <summary>Same as <see cref="EchoLoop"/> through <c>await reader.Wait(1)</c> (the sketch's primary API).</summary>
    internal static async Task<long> EchoLoopAsync(RingReader<long> reader, RingBuffer<long> outBuf)
    {
        long rounds = 0;
        while (true)
        {
            if (!await reader.Wait(1).ConfigureAwait(false))
            {
                return -1;
            }

            long v;
            {
                reader.TryRead(1, out Chunk<long> chunk);
                v = chunk.Data.Span[0];
                reader.Advance(1);
            }

            if (v == -1)
            {
                return rounds;
            }

            using (Bucket<long> b = outBuf.GetBucket(1))
            {
                b.Span[0] = v;
                b.Commit(1);
            }

            rounds++;
        }
    }

    /// <summary>
    /// <c>drain &lt;name&gt; &lt;bucketElements&gt; &lt;buckets&gt; &lt;mode&gt; &lt;core&gt;</c>: consumes <c>buckets</c> chunks of <c>bucketElements</c> floats,
    /// reading (summing) every element and checking that the first and last element carry the bucket sequence number.
    /// </summary>
    private static int Drain(ReadOnlySpan<string> a)
    {
        string name = a[1];
        int n = int.Parse(a[2], CultureInfo.InvariantCulture);
        long buckets = long.Parse(a[3], CultureInfo.InvariantCulture);
        WakeMode mode = WakeModes.Parse(a[4]);
        int core = int.Parse(a[5], CultureInfo.InvariantCulture);
        Affinity.Pin(core, highest: false);

        using RingBuffer<float> buffer = RingBuffer<float>.Open(name);
        using RingReader<float> reader = buffer.CreateReader(WakeModes.Reader(mode));
        Print(Inv($"ready atCreator={buffer.IsMappedAtCreatorAddress}"));

        (long bad, double sum) = DrainLoop(reader, n, buckets);
        if (bad < 0)
        {
            Print(Inv($"eof status={reader.Status}"));
            return 3;
        }

        Counters c = reader.Counters;
        Print(Inv($"done bad={bad} sum={sum:E3} kernelWaits={c.KernelWaits} signals={c.Signals} spinSuccesses={c.SpinSuccesses}"));
        return bad == 0 ? 0 : 4;
    }

    /// <summary>Reads every bucket completely (vectorised sum) so that the measured throughput includes the consumer's memory traffic.</summary>
    internal static (long Bad, double Sum) DrainLoop(RingReader<float> reader, int n, long buckets)
    {
        long bad = 0;
        double sum = 0;
        for (long i = 0; i < buckets; i++)
        {
            if (!reader.WaitSync(n))
            {
                return (-1, sum);
            }

            reader.TryRead(n, out Chunk<float> chunk);
            ReadOnlySpan<float> span = chunk.Data.Span;
            float expected = i;
            if (span[0] != expected || span[n - 1] != expected)
            {
                bad++;
            }

            sum += Sum(span);
            reader.Advance(n);
        }

        return (bad, sum);
    }

    /// <summary>
    /// <c>drain1 &lt;name&gt; &lt;bucketElements&gt; &lt;buckets&gt; &lt;mode&gt; &lt;core&gt; &lt;work&gt;</c>: consumes <c>buckets</c> chunks of <c>bucketElements</c> ints.
    /// <c>count1</c>: counts the elements equal to 1 and validates the count (the producer writes one every 16 elements);
    /// <c>touch1</c>: counts the ones without validation (the producer touched a single element per bucket);
    /// <c>none</c>: advances without reading the payload (pure protocol cost).
    /// </summary>
    private static int Drain1(ReadOnlySpan<string> a)
    {
        string name = a[1];
        int n = int.Parse(a[2], CultureInfo.InvariantCulture);
        long buckets = long.Parse(a[3], CultureInfo.InvariantCulture);
        WakeMode mode = WakeModes.Parse(a[4]);
        int core = int.Parse(a[5], CultureInfo.InvariantCulture);
        Transports.Work work = Transports.ParseWork(a[6]);
        Affinity.Pin(core, highest: false);

        using RingBuffer<int> buffer = RingBuffer<int>.Open(name);
        using RingReader<int> reader = buffer.CreateReader(WakeModes.Reader(mode));
        Print(Inv($"ready atCreator={buffer.IsMappedAtCreatorAddress}"));

        (long bad, long ones) = DrainLoop1(reader, n, buckets, work);
        if (bad < 0)
        {
            Print(Inv($"eof status={reader.Status}"));
            return 3;
        }

        Counters c = reader.Counters;
        Print(Inv($"done bad={bad} ones={ones} kernelWaits={c.KernelWaits} signals={c.Signals} spinSuccesses={c.SpinSuccesses}"));
        return bad == 0 ? 0 : 4;
    }

    internal static (long Bad, long Ones) DrainLoop1(RingReader<int> reader, int n, long buckets, Transports.Work work)
    {
        long expected = OnesPerBucket(n);
        long bad = 0;
        long ones = 0;
        for (long i = 0; i < buckets; i++)
        {
            if (!reader.WaitSync(n))
            {
                return (-1, ones);
            }

            reader.TryRead(n, out Chunk<int> chunk);
            if (work != Transports.Work.None)
            {
                long c = CountOnes(chunk.Data.Span);
                if (work == Transports.Work.Count1 && c != expected)
                {
                    bad++;
                }

                ones += c;
            }

            reader.Advance(n);
        }

        return (bad, ones);
    }

    /// <summary>The producer pattern for the count-1 rows: every element is <c>i + 2</c> (never 1), then every 16th element is set to 1.</summary>
    internal static void FillPattern(Span<int> span, long i)
    {
        span.Fill(unchecked((int)i + 2));
        for (int j = 0; j < span.Length; j += 16)
        {
            span[j] = 1;
        }
    }

    internal static long OnesPerBucket(int n) => (n + 15) / 16;

    /// <summary>Counts the elements equal to 1 (vectorised: one compare and one subtract per <see cref="Vector{T}.Count"/> ints).</summary>
    internal static long CountOnes(ReadOnlySpan<int> span)
    {
        Vector<int> ones = Vector<int>.One;
        Vector<int> acc = Vector<int>.Zero;
        int i = 0;
        for (; i <= span.Length - Vector<int>.Count; i += Vector<int>.Count)
        {
            acc -= Vector.Equals(new Vector<int>(span.Slice(i, Vector<int>.Count)), ones);   // Equals yields -1 per matching lane
        }

        long count = 0;
        for (int k = 0; k < Vector<int>.Count; k++)
        {
            count += acc[k];
        }

        for (; i < span.Length; i++)
        {
            if (span[i] == 1)
            {
                count++;
            }
        }

        return count;
    }

    internal static float Sum(ReadOnlySpan<float> span)
    {
        Vector<float> acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= span.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            acc += new Vector<float>(span.Slice(i, Vector<float>.Count));
        }

        float sum = Vector.Sum(acc);
        for (; i < span.Length; i++)
        {
            sum += span[i];
        }

        return sum;
    }
}
