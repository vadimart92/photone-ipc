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
                "pipe-echo" => Transports.PipeEcho(args),
                "tcp-echo" => Transports.TcpEcho(args),
                "pipe-drain" => Transports.PipeDrain(args),
                "tcp-drain" => Transports.TcpDrain(args),
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

        long rounds = mode == WakeMode.Async
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
            long v = chunk.Span[0];
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
                v = chunk.Span[0];
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
            ReadOnlySpan<float> span = chunk.Span;
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
