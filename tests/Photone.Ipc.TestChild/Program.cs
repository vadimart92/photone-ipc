using System.Globalization;
using Windows.Win32.System.Memory;
using Photone.Ipc.Internal;

namespace Photone.Ipc.TestChild;

/// <summary>
/// Cross-process test helper. Every verb prints a readiness line on stdout as soon as it is ready and never sleeps (except <c>slow-init</c>).
/// Element type is always <see cref="long"/>; the deterministic stream stores the absolute cursor in every element (<c>data[c] == c</c>).
/// Verbs (DESIGN §9): reader | echo | writer | crash-reader | claim-and-die | slow-init | hold-name | spin-reader | pool-reader-loop (§15), plus Stage A's map-region.
/// </summary>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: Photone.Ipc.TestChild <verb> [args]");
                return 2;
            }

            return args[0] switch
            {
                "map-region" => MapRegion(args),
                "reader" => Reader(args, spinOnly: false),
                "spin-reader" => Reader(args, spinOnly: true),
                "writer" => Writer(args),
                "echo" => Echo(args),
                "crash-reader" => CrashReader(args),
                "step-reader" => StepReader(args),
                "join-storm" => JoinStorm(args),
                "claim-and-die" => ClaimAndDie(args),
                "slow-init" => SlowInit(args),
                "hold-name" => HoldName(args),
                "pool-reader-loop" => PoolReaderLoop(),
                "tag-writer" => WriteTags(args),
                "tag-reader" => ReadTags(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine("error " + ex.GetType().Name + ": " + ex.Message);
            Console.Out.Flush();
            return 1;
        }
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine("unknown verb: " + verb);
        return 2;
    }

    private static void Print(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ reader / spin-reader

    /// <summary><c>reader &lt;name&gt; &lt;count&gt;</c>: opens, creates a reader, prints "ready ...", consumes count elements verifying data[c] == c, prints "done ok|mismatch".</summary>
    private static int Reader(string[] args, bool spinOnly)
    {
        string name = args[1];
        long count = long.Parse(args[2], CultureInfo.InvariantCulture);
        RingBuffer<long> buffer;
        if (name == "--stdin-handle")
        {
            // the parent duplicated its section handle into this process and sends "handle <hex>"
            string? line = Console.In.ReadLine();
            if (line is null || !line.StartsWith("handle ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("expected 'handle <hex>' on stdin, got " + line);
            }

            nint h = (nint)long.Parse(line["handle ".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            buffer = RingBuffer<long>.Open(new SafeSectionHandle(h, ownsHandle: true));
        }
        else
        {
            buffer = RingBuffer<long>.Open(name);
        }

        using RingBuffer<long> owned = buffer;
        ReaderOptions opts = spinOnly ? new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan, AsyncSpinTime = Timeout.InfiniteTimeSpan } : new ReaderOptions();
        using RingReader<long> reader = buffer.CreateReader(opts);
        Print(Inv($"ready slot={reader.Slot} atCreator={buffer.IsMappedAtCreatorAddress} cursor={reader.ReadCursor} name={buffer.Name ?? "null"}"));

        long remaining = count;
        var rng = new Random(12345);
        long mismatchAt = -1;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, rng.Next(1, 4097));
            if (!reader.WaitSync(n))
            {
                Print(Inv($"eof status={reader.Status} available={reader.Available} remaining={remaining}"));
                return 3;
            }

            if (!reader.TryRead(n, out Chunk<long> chunk))
            {
                Print("error TryRead returned false after WaitSync");
                return 3;
            }

            ReadOnlySpan<long> span = chunk.Span;
            for (int j = 0; j < span.Length; j++)
            {
                if (span[j] != chunk.Cursor + j)
                {
                    mismatchAt = chunk.Cursor + j;
                    break;
                }
            }

            if (mismatchAt >= 0)
            {
                break;
            }

            reader.Advance(n);
            remaining -= n;
        }

        Counters c = reader.Counters;
        Print(mismatchAt >= 0
            ? Inv($"done mismatch at={mismatchAt}")
            : Inv($"done ok cursor={reader.ReadCursor} kernelWaits={c.KernelWaits} signals={c.Signals} spinSuccesses={c.SpinSuccesses}"));
        return mismatchAt >= 0 ? 4 : 0;
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// <c>writer &lt;name&gt; &lt;count&gt; [--crash-after k] [--crash] [--capacity c]</c>: creates the buffer, prints "ready", waits for "go" on stdin,
    /// streams count elements (data[c] == c), then prints "committed" and either FailFasts (--crash) or disposes and prints "closed".
    /// With --crash-after k it FailFasts once at least k elements are published.
    /// </summary>
    private static int Writer(string[] args)
    {
        string name = args[1];
        long count = long.Parse(args[2], CultureInfo.InvariantCulture);
        long crashAfter = -1;
        bool crash = false;
        long capacity = 1 << 16;
        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--crash-after":
                    crashAfter = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--crash":
                    crash = true;
                    break;
                case "--capacity":
                    capacity = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException("unknown option " + args[i]);
            }
        }

        RingBuffer<long> buffer = RingBuffer<long>.Create(capacity, name);
        Print(Inv($"ready pid={Environment.ProcessId} capacity={buffer.Capacity}"));
        string? line = Console.In.ReadLine();
        if (line != "go")
        {
            Print("error expected go, got " + line);
            return 3;
        }

        var rng = new Random(777);
        long written = 0;
        while (written < count)
        {
            int n = (int)Math.Min(count - written, rng.Next(1, 4097));
            using Bucket<long> bucket = buffer.GetBucket(n);
            Span<long> span = bucket.Span;
            for (int j = 0; j < span.Length; j++)
            {
                span[j] = bucket.Cursor + j;
            }

            bucket.Commit(n);
            written += n;
            if (crashAfter >= 0 && written >= crashAfter)
            {
                Print(Inv($"crashing after={written}"));
                Environment.FailFast("test crash");
            }
        }

        Counters c = buffer.Counters;
        Print(Inv($"committed written={written} kernelWaits={c.KernelWaits} signals={c.Signals} evictions={c.Evictions}"));
        if (crash)
        {
            Environment.FailFast("test crash");
        }

        buffer.Dispose();
        Print("closed");
        return 0;
    }

    // ------------------------------------------------------------------ tags (DESIGN §16)

    /// <summary>
    /// <c>tag-writer &lt;name&gt; &lt;count&gt; [--label-padding chars]</c>: creates a buffer with cross-process tags, prints "ready", waits for "go", writes count
    /// elements with the <see cref="TagPlan"/> tags (labels padded), prints "committed written=N ringSwitches=S committedTagBytes=B", disposes and prints "closed".
    /// </summary>
    private static int WriteTags(string[] args)
    {
        string name = args[1];
        long count = long.Parse(args[2], CultureInfo.InvariantCulture);
        int padding = 0;
        for (int i = 3; i < args.Length; i++)
        {
            padding = args[i] == "--label-padding" ? int.Parse(args[++i], CultureInfo.InvariantCulture) : throw new ArgumentException("unknown option " + args[i]);
        }

        var options = new RingBufferOptions { Tags = TagMode.CrossProcess, TagSerializer = TagPlan.CreateSerializer() };
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 16, name, options);
        Print(Inv($"ready pid={Environment.ProcessId} tags={buffer.Tags}"));
        string? line = Console.In.ReadLine();
        if (line != "go")
        {
            Print("error expected go, got " + line);
            return 3;
        }

        long written = TagPlan.Write(buffer, count, 128, new Random(4242), padding);
        SharedTagLog shared = buffer.TagWriter!.Shared!;
        Print(Inv($"committed written={written} ringSwitches={shared.RingSwitches} committedTagBytes={shared.CommittedBytes}"));
        buffer.Dispose();
        Print("closed");
        return 0;
    }

    /// <summary>
    /// <c>tag-reader &lt;name&gt; &lt;count&gt;</c>: opens the buffer, joins, checks <c>ReadLastTagValues</c> at the start cursor against the plan and prints
    /// "ready cursor=R stateKeys=K", then reads count elements in random chunks, checking data, tags and state after every advance; prints "done ok tags=T"
    /// or "mismatch ...".
    /// </summary>
    private static int ReadTags(string[] args)
    {
        string name = args[1];
        long count = long.Parse(args[2], CultureInfo.InvariantCulture);
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        using RingReader<long> reader = buffer.CreateReader();
        ReadOnlySpan<ITag> start = reader.ReadLastTagValues();
        string? error = TagPlan.CheckState(start, reader.ReadCursor);
        if (error is not null)
        {
            Print("mismatch " + error);
            return 4;
        }

        Print(Inv($"ready cursor={reader.ReadCursor} stateKeys={start.Length}"));
        var rng = new Random(99);
        long remaining = count;
        long tags = 0;
        while (remaining > 0)
        {
            if (!reader.WaitSync(1))
            {
                Print(Inv($"eof status={reader.Status} remaining={remaining}"));
                return 3;
            }

            int n = (int)Math.Min(Math.Min(remaining, reader.Available), rng.Next(1, 700));
            if (!reader.TryRead(n, out Chunk<long> chunk))
            {
                Print("error TryRead returned false");
                return 3;
            }

            error = TagPlan.CheckChunk(chunk);
            if (error is not null)
            {
                Print("mismatch " + error);
                return 4;
            }

            tags += chunk.Tags.Length;
            int advance = rng.Next(3) == 0 ? rng.Next(1, n + 1) : n;
            reader.Advance(advance);
            if (tags % 7 == 0 || remaining - advance <= 0)
            {
                error = TagPlan.CheckState(reader.ReadLastTagValues(), reader.ReadCursor);
                if (error is not null)
                {
                    Print("mismatch " + error);
                    return 4;
                }
            }

            remaining -= advance;
        }

        Print(Inv($"done ok tags={tags} cursor={reader.ReadCursor}"));
        return 0;
    }

    // ------------------------------------------------------------------ echo

    /// <summary><c>echo &lt;nameIn&gt; &lt;nameOut&gt; &lt;n&gt;</c>: reads one element from nameIn, writes it to nameOut (created here), n times.</summary>
    private static int Echo(string[] args)
    {
        string nameIn = args[1];
        string nameOut = args[2];
        long n = long.Parse(args[3], CultureInfo.InvariantCulture);
        using RingBuffer<long> inBuf = RingBuffer<long>.Open(nameIn);
        using RingReader<long> reader = inBuf.CreateReader(new ReaderOptions { SpinTime = TimeSpan.FromMilliseconds(1) });
        using RingBuffer<long> outBuf = RingBuffer<long>.Create(1 << 12, nameOut, new RingBufferOptions { SpinTime = TimeSpan.FromMilliseconds(1) });
        Print("ready");
        for (long i = 0; i < n; i++)
        {
            if (!reader.WaitSync(1))
            {
                Print(Inv($"eof status={reader.Status} at={i}"));
                return 3;
            }

            reader.TryRead(1, out Chunk<long> chunk);
            long v = chunk.Span[0];
            reader.Advance(1);
            using Bucket<long> b = outBuf.GetBucket(1);
            b.Span[0] = v;
            b.Commit(1);
        }

        Print("done");
        return 0;
    }

    // ------------------------------------------------------------------ fault injection

    /// <summary><c>crash-reader &lt;name&gt;</c>: opens, creates a reader that never advances, prints "ready slot=i", waits for stdin, then FailFasts.</summary>
    private static int CrashReader(string[] args)
    {
        RingBuffer<long> buffer = RingBuffer<long>.Open(args[1]);
        RingReader<long> reader = buffer.CreateReader();
        Print(Inv($"ready slot={reader.Slot} pid={Environment.ProcessId}"));
        Console.In.ReadLine();
        Environment.FailFast("test crash");
        return 0;
    }

    /// <summary>
    /// <c>step-reader &lt;name&gt;</c>: opens, creates a reader, prints "ready slot=i", then obeys stdin: "advance N" (waits for N, reads, verifies,
    /// advances, prints "advanced cursor=c"), "die" (FailFast), "exit" (disposes, prints "exiting").
    /// </summary>
    private static int StepReader(string[] args)
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Open(args[1]);
        using RingReader<long> reader = buffer.CreateReader();
        Print(Inv($"ready slot={reader.Slot} pid={Environment.ProcessId}"));
        while (true)
        {
            string? line = Console.In.ReadLine();
            if (line is null || line == "exit")
            {
                Print("exiting");
                return 0;
            }

            if (line == "die")
            {
                Environment.FailFast("step-reader die");
            }

            if (line.StartsWith("advance ", StringComparison.Ordinal))
            {
                int n = int.Parse(line["advance ".Length..], CultureInfo.InvariantCulture);
                if (!reader.WaitSync(n))
                {
                    Print(Inv($"eof status={reader.Status}"));
                    continue;
                }

                reader.TryRead(n, out Chunk<long> chunk);
                for (int j = 0; j < chunk.Length; j++)
                {
                    if (chunk.Span[j] != chunk.Cursor + j)
                    {
                        Print(Inv($"mismatch at={chunk.Cursor + j}"));
                        return 4;
                    }
                }

                reader.Advance(n);
                Print(Inv($"advanced cursor={reader.ReadCursor}"));
            }
        }
    }

    /// <summary><c>join-storm &lt;name&gt; &lt;iterations&gt;</c>: creates and disposes a reader `iterations` times, reading and verifying a little each time.</summary>
    private static int JoinStorm(string[] args)
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Open(args[1]);
        int iterations = int.Parse(args[2], CultureInfo.InvariantCulture);
        Print("storming");
        var rng = new Random(Environment.ProcessId);
        long verified = 0;
        int evictedSeen = 0;
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                using RingReader<long> r = buffer.CreateReader();
                int n = rng.Next(1, 512);
                if (r.WaitSync(n, TimeSpan.FromMilliseconds(rng.Next(0, 5))))
                {
                    r.TryRead(n, out Chunk<long> chunk);
                    for (int j = 0; j < chunk.Length; j++)
                    {
                        if (chunk.Span[j] != chunk.Cursor + j)
                        {
                            Print(Inv($"mismatch at={chunk.Cursor + j}"));
                            return 4;
                        }
                    }

                    r.Advance(rng.Next(0, n + 1));
                    verified += n;
                }
            }
            catch (ReaderEvictedException)
            {
                evictedSeen++;
            }
        }

        Print(Inv($"done iterations={iterations} verified={verified} evicted={evictedSeen}"));
        return 0;
    }

    /// <summary><c>claim-and-die &lt;name&gt;</c>: dies between the two claim CASes (slot left in Claimed).</summary>
    private static int ClaimAndDie(string[] args)
    {
        RingBuffer<long> buffer = RingBuffer<long>.Open(args[1]);
        Print(Inv($"opened pid={Environment.ProcessId}"));
        TestHooks.AfterClaim = static () => Environment.FailFast("claim-and-die");
        buffer.CreateReader();
        Print("error survived");
        return 3;
    }

    /// <summary><c>slow-init &lt;name&gt; &lt;ms&gt; [--die]</c>: creates the buffer but sleeps ms before InitState = 1 (or dies there).</summary>
    private static int SlowInit(string[] args)
    {
        string name = args[1];
        int ms = int.Parse(args[2], CultureInfo.InvariantCulture);
        bool die = args.Length > 3 && args[3] == "--die";
        TestHooks.BeforeInitState = () =>
        {
            Print("initializing");
            Thread.Sleep(ms);
            if (die)
            {
                Environment.FailFast("slow-init --die");
            }
        };
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 14, name);
        Print("ready");
        Console.In.ReadLine();
        return 0;
    }

    /// <summary><c>hold-name &lt;name&gt;</c>: opens the buffer and keeps it open until a line arrives on stdin.</summary>
    private static int HoldName(string[] args)
    {
        RingBuffer<long> buffer = RingBuffer<long>.Open(args[1]);
        Print("holding");
        Console.In.ReadLine();
        buffer.Dispose();
        Print("released");
        return 0;
    }

    /// <summary>
    /// <c>pool-reader-loop</c>: keeps one <see cref="RingBufferPool"/> (no expiry), prints "looping", then obeys stdin: "open &lt;name&gt; &lt;count&gt;" opens
    /// the buffer with the pool, creates a reader, prints "ready base=0x.. revived=n", consumes count elements verifying data[c] == c, disposes the reader
    /// and the buffer (the mapping is parked) and prints "done ok idle=n" or "done mismatch at=c"; "exit" disposes the pool and exits.
    /// </summary>
    private static int PoolReaderLoop()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan });
        Print("looping");
        while (true)
        {
            string? line = Console.In.ReadLine();
            if (line is null || line == "exit")
            {
                Print("exiting");
                return 0;
            }

            string[] parts = line.Split(' ');
            if (parts.Length != 3 || parts[0] != "open")
            {
                Print("error unknown command " + line);
                return 3;
            }

            long count = long.Parse(parts[2], CultureInfo.InvariantCulture);
            long mismatchAt = -1;
            using (RingBuffer<long> buffer = RingBuffer<long>.Open(parts[1], new RingBufferOptions { Pool = pool }))
            using (RingReader<long> reader = buffer.CreateReader())
            {
                Print(Inv($"ready base=0x{buffer.BaseAddress:X} revived={pool.RevivedCount} cursor={reader.ReadCursor}"));
                long remaining = count;
                while (remaining > 0 && mismatchAt < 0)
                {
                    int n = (int)Math.Min(remaining, 4096);
                    if (!reader.WaitSync(n))
                    {
                        Print(Inv($"eof status={reader.Status} available={reader.Available} remaining={remaining}"));
                        return 3;
                    }

                    reader.TryRead(n, out Chunk<long> chunk);
                    for (int j = 0; j < chunk.Length; j++)
                    {
                        if (chunk.Span[j] != chunk.Cursor + j)
                        {
                            mismatchAt = chunk.Cursor + j;
                            break;
                        }
                    }

                    reader.Advance(n);
                    remaining -= n;
                }
            }

            Print(mismatchAt >= 0 ? Inv($"done mismatch at={mismatchAt}") : Inv($"done ok idle={pool.IdleCount}"));
            if (mismatchAt >= 0)
            {
                return 4;
            }
        }
    }

    // ------------------------------------------------------------------ Stage A: map-region

    /// <summary>
    /// Opens the named section, maps it (creator base first, then anywhere), reports the placement, verifies the 16-byte marker the parent
    /// wrote across the data/mirror boundary, writes its own marker back (across the boundary via the mirror alias), then waits for a line
    /// on stdin before unmapping and exiting.
    /// </summary>
    private static int MapRegion(string[] args)
    {
        string name = args[1];
        long dataBytes = long.Parse(args[2], CultureInfo.InvariantCulture);
        ulong creatorBase = ulong.Parse(args[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        bool occupy = args.Length > 4 && args[4] == "--occupy";

        if (occupy && creatorBase != 0)
        {
            // Pre-reserve the creator's range so Open must fall back to a system-chosen address.
            void* blocker = Kernel.VirtualAlloc2(0, (void*)creatorBase, 65536, VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE, PAGE_PROTECTION_FLAGS.PAGE_NOACCESS, null, 0);
            if (blocker == null)
            {
                throw new InvalidOperationException("could not occupy the creator's range: " + Kernel.LastError());
            }
        }

        SafeSectionHandle section = MirroredSection.OpenSection(name);
        ReadOnlySpan<ulong> candidates = creatorBase == 0 ? [] : [creatorBase];
        using MirroredSection region = MirroredSection.Open(section, dataBytes, candidates);

        Print(Inv($"mapped base=0x{region.BaseAddress:X} atCreator={region.AtRequestedAddress}"));

        // The parent wrote 0..15 into data[D-8 .. D+8) (crossing into the mirror). Read it through the mirror alias at mirror[-8 .. 8).
        nuint d = region.DataBytes;
        bool ok = true;
        for (int i = 0; i < 16; i++)
        {
            byte expected = (byte)i;
            byte viaData = region.Data[(long)d - 8 + i];
            byte viaMirror = region.Mirror[i - 8];
            ok &= viaData == expected && viaMirror == expected;
        }

        // Write our own marker (100..115) across the boundary via the MIRROR pointer; the parent verifies it via DATA.
        var mine = new Span<byte>(region.Mirror - 8, 16);
        for (int i = 0; i < 16; i++)
        {
            mine[i] = (byte)(100 + i);
        }

        Print(ok ? "verified" : "mismatch");
        Console.In.ReadLine();
        Print("exiting");
        return 0;
    }
}
