using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// The competitors for <c>--compare</c>: a byte-mode synchronous named pipe and a TCP loopback socket (<c>NoDelay</c>), driven with the
/// same shape as the ring-buffer rows (8-byte ping-pong round trips; 2 GiB streamed in fixed buckets that the consumer fills/sums).
/// Both are the fastest plain-.NET ways to talk between two Windows processes without shared memory, so they are the honest baseline.
/// The harness is the server/listener; the peer process (<c>--peer pipe-echo|tcp-echo|pipe-drain|tcp-drain</c>) connects.
/// </summary>
internal static class Transports
{
    /// <summary>Pipe / socket buffer size used for the throughput rows (the default 4 KiB pipe buffer would measure the pipe's buffer, not the copy).</summary>
    public const int StreamBufferBytes = 1 << 20;

    private static readonly TimeSpan s_warmup = TimeSpan.FromMilliseconds(500);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ harness side

    public static (long[] Ticks, string Detail) PipePingPong(int rounds, int coreB)
    {
        string name = "photone-bench-pipe-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 65536, 65536);
        using var peer = QuickHarness.PeerProcess.Start("pipe-echo", name, Inv($"{coreB}"));
        server.WaitForConnection();
        peer.Expect("ready");
        long[] ticks = PingPongLoop(server, rounds);
        string done = peer.Expect("done");
        peer.WaitForExit();
        return (ticks, "named pipe, byte mode, synchronous Read/Write; " + done["done ".Length..]);
    }

    public static (long[] Ticks, string Detail) TcpPingPong(int rounds, int coreB)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var peer = QuickHarness.PeerProcess.Start("tcp-echo", Inv($"{port}"), Inv($"{coreB}"));
        using Socket socket = listener.Accept();
        socket.NoDelay = true;
        peer.Expect("ready");
        using var stream = new NetworkStream(socket, ownsSocket: false);
        long[] ticks = PingPongLoop(stream, rounds);
        string done = peer.Expect("done");
        peer.WaitForExit();
        return (ticks, "TCP loopback, NoDelay, synchronous Send/Receive; " + done["done ".Length..]);
    }

    /// <summary>Workload of the throughput rows.</summary>
    public enum Work
    {
        /// <summary>Producer fills every float, consumer sums every float (memory-bandwidth bound on both sides).</summary>
        Sum,

        /// <summary>Producer fills every int (pattern with a 1 every 16 elements), consumer counts the ones and validates the count.</summary>
        Count1,

        /// <summary>Producer touches one int per bucket, consumer counts the ones over the whole bucket (one vector pass; no validation). Measures the transport, not the workload.</summary>
        Touch1,

        /// <summary>Nothing touches the payload (ring buffer only: commit + advance). Pure protocol cost.</summary>
        None,
    }

    public static string WorkName(Work w) => w switch
    {
        Work.Count1 => "count1",
        Work.Touch1 => "touch1",
        Work.None => "none",
        _ => "sum",
    };

    public static Work ParseWork(string s) => s switch
    {
        "count1" => Work.Count1,
        "touch1" => Work.Touch1,
        "none" => Work.None,
        _ => Work.Sum,
    };

    public static (TimeSpan Elapsed, string Detail) PipeThroughput(int bucketElements, long buckets, int coreB, Work work)
    {
        string name = "photone-bench-pipe-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, StreamBufferBytes, StreamBufferBytes);
        using var peer = QuickHarness.PeerProcess.Start("pipe-drain", name, Inv($"{bucketElements}"), Inv($"{buckets}"), Inv($"{coreB}"), WorkName(work));
        server.WaitForConnection();
        peer.Expect("ready");
        var sw = Stopwatch.StartNew();
        WriteBuckets(server, bucketElements, buckets, work);
        string done = peer.Expect("done");
        sw.Stop();
        peer.WaitForExit();
        Check(done);
        return (sw.Elapsed, Inv($"named pipe, {StreamBufferBytes >> 10} KiB pipe buffer; ") + done["done ".Length..]);
    }

    public static (TimeSpan Elapsed, string Detail) TcpThroughput(int bucketElements, long buckets, int coreB, Work work)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var peer = QuickHarness.PeerProcess.Start("tcp-drain", Inv($"{port}"), Inv($"{bucketElements}"), Inv($"{buckets}"), Inv($"{coreB}"), WorkName(work));
        using Socket socket = listener.Accept();
        socket.NoDelay = true;
        socket.SendBufferSize = StreamBufferBytes;
        peer.Expect("ready");
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var sw = Stopwatch.StartNew();
        WriteBuckets(stream, bucketElements, buckets, work);
        string done = peer.Expect("done");
        sw.Stop();
        peer.WaitForExit();
        Check(done);
        return (sw.Elapsed, Inv($"TCP loopback, NoDelay, {StreamBufferBytes >> 10} KiB socket buffers; ") + done["done ".Length..]);
    }


    private static void Check(string done)
    {
        int at = done.IndexOf("bad=", StringComparison.Ordinal);
        if (at < 0 || !done.AsSpan(at + 4).StartsWith("0 ") && !done.AsSpan(at + 4).SequenceEqual("0"))
        {
            throw new InvalidOperationException("consumer reported corrupted buckets: " + done);
        }
    }

    /// <summary>Warm-up for <see cref="s_warmup"/>, then <paramref name="rounds"/> timed rounds (one 8-byte message each way); ends with the sentinel -1.</summary>
    private static long[] PingPongLoop(Stream stream, int rounds)
    {
        Span<byte> buf = stackalloc byte[8];
        long i = 0;
        long warmupEnd = Stopwatch.GetTimestamp() + SpinClock.ToTicks(s_warmup);
        while (i < 2000 || Stopwatch.GetTimestamp() < warmupEnd)
        {
            Round(stream, buf, i++);
        }

        var ticks = new long[rounds];
        for (int r = 0; r < rounds; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Round(stream, buf, i++);
            ticks[r] = Stopwatch.GetTimestamp() - t0;
        }

        BitConverter.TryWriteBytes(buf, -1L);
        stream.Write(buf);
        stream.Flush();
        return ticks;
    }

    private static void Round(Stream stream, Span<byte> buf, long value)
    {
        BitConverter.TryWriteBytes(buf, value);
        stream.Write(buf);
        stream.ReadExactly(buf);
        long echoed = BitConverter.ToInt64(buf);
        if (echoed != value)
        {
            throw new InvalidOperationException(Inv($"ping-pong: got {echoed}, expected {value}"));
        }
    }

    private static void WriteBuckets(Stream stream, int bucketElements, long buckets, Work work)
    {
        var buffer = new byte[bucketElements * sizeof(int)];                    // sizeof(float) == sizeof(int)
        Span<float> floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
        Span<int> ints = MemoryMarshal.Cast<byte, int>(buffer.AsSpan());
        for (long i = 0; i < buckets; i++)
        {
            switch (work)
            {
                case Work.Count1:
                    Peer.FillPattern(ints, i);
                    break;
                case Work.Touch1:
                case Work.None:
                    ints[0] = 1;                                                    // one store per bucket; the rest of the buffer stays as it is
                    break;
                default:
                    floats.Fill(i);
                    break;
            }

            stream.Write(buffer);
        }

        stream.Flush();
    }

    // ------------------------------------------------------------------ peer side

    public static int PipeEcho(ReadOnlySpan<string> a)
    {
        string name = a[1];
        int core = int.Parse(a[2], CultureInfo.InvariantCulture);
        Affinity.Pin(core, highest: true);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        client.Connect(30_000);
        Print("ready pipe");
        long rounds = EchoLoop(client);
        Print(Inv($"done rounds={rounds}"));
        return 0;
    }

    public static int TcpEcho(ReadOnlySpan<string> a)
    {
        int port = int.Parse(a[1], CultureInfo.InvariantCulture);
        int core = int.Parse(a[2], CultureInfo.InvariantCulture);
        Affinity.Pin(core, highest: true);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        socket.NoDelay = true;
        using var stream = new NetworkStream(socket, ownsSocket: false);
        Print("ready tcp");
        long rounds = EchoLoop(stream);
        Print(Inv($"done rounds={rounds}"));
        return 0;
    }

    public static int PipeDrain(ReadOnlySpan<string> a)
    {
        string name = a[1];
        int n = int.Parse(a[2], CultureInfo.InvariantCulture);
        long buckets = long.Parse(a[3], CultureInfo.InvariantCulture);
        int core = int.Parse(a[4], CultureInfo.InvariantCulture);
        Work work = ParseWork(a[5]);
        Affinity.Pin(core, highest: false);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.None);
        client.Connect(30_000);
        Print("ready pipe");
        (long bad, double result) = DrainLoop(client, n, buckets, work);
        Print(Inv($"done bad={bad} {WorkName(work)}={result:E3}"));
        return bad == 0 ? 0 : 4;
    }

    public static int TcpDrain(ReadOnlySpan<string> a)
    {
        int port = int.Parse(a[1], CultureInfo.InvariantCulture);
        int n = int.Parse(a[2], CultureInfo.InvariantCulture);
        long buckets = long.Parse(a[3], CultureInfo.InvariantCulture);
        int core = int.Parse(a[4], CultureInfo.InvariantCulture);
        Work work = ParseWork(a[5]);
        Affinity.Pin(core, highest: false);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        socket.NoDelay = true;
        socket.ReceiveBufferSize = StreamBufferBytes;
        using var stream = new NetworkStream(socket, ownsSocket: false);
        Print("ready tcp");
        (long bad, double result) = DrainLoop(stream, n, buckets, work);
        Print(Inv($"done bad={bad} {WorkName(work)}={result:E3}"));
        return bad == 0 ? 0 : 4;
    }

    private static long EchoLoop(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        long rounds = 0;
        while (true)
        {
            stream.ReadExactly(buf);
            if (BitConverter.ToInt64(buf) == -1)
            {
                return rounds;
            }

            stream.Write(buf);
            rounds++;
        }
    }

    /// <summary>
    /// Streaming consumer: reads as much as the transport has (up to 1 MiB per call) and parses whole buckets out of the receive buffer, so a
    /// read syscall covers many buckets when the producer is ahead (the way a real consumer of a byte stream works; one read per bucket
    /// would measure the wake-up cost per bucket instead of the transport).
    /// </summary>
    private static (long Bad, double Result) DrainLoop(Stream stream, int n, long buckets, Work work)
    {
        int bucketBytes = n * sizeof(float);
        var buffer = new byte[Math.Max(StreamBufferBytes, bucketBytes * 2)];
        int start = 0;
        int end = 0;
        long bad = 0;
        double sum = 0;
        long expectedOnes = Peer.OnesPerBucket(n);
        for (long i = 0; i < buckets; i++)
        {
            if (end - start < bucketBytes)
            {
                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);   // at most one partial bucket moves
                    end -= start;
                    start = 0;
                }

                while (end - start < bucketBytes)
                {
                    int read = stream.Read(buffer, end, buffer.Length - end);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException(Inv($"stream ended after {i} of {buckets} buckets"));
                    }

                    end += read;
                }
            }

            if (work is Work.Count1 or Work.Touch1 or Work.None)
            {
                long c = Peer.CountOnes(MemoryMarshal.Cast<byte, int>(buffer.AsSpan(start, bucketBytes)));
                if (work == Work.Count1 && c != expectedOnes)
                {
                    bad++;
                }

                sum += c;
            }
            else
            {
                ReadOnlySpan<float> span = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(start, bucketBytes));
                float expected = i;
                if (span[0] != expected || span[n - 1] != expected)
                {
                    bad++;
                }

                sum += Peer.Sum(span);
            }

            start += bucketBytes;
        }

        return (bad, sum);
    }

    private static void Print(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
