using System.Diagnostics;
using System.Globalization;
using Photone.Ipc.Internal;
using Xunit.Sdk;

namespace Photone.Ipc.Tests;

/// <summary>End-to-end tests with real child processes (TestChild verbs; DESIGN §9 tests 21–38).</summary>
[Collection("ipc")]
public sealed class CrossProcessRingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Expect(ChildProcess child, string prefix)
    {
        string line = child.ReadLine(s_timeout);
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new XunitException($"child printed '{line}', expected a line starting with '{prefix}'");
        }

        return line;
    }

    // ------------------------------------------------------------------ 21 / 22 / 33: child readers

    [Fact]
    public void CrossProcess_ReaderSeesData()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 16, name);
        using ChildProcess child = ChildProcess.Start("reader", name, "1000000");
        string ready = Expect(child, "ready ");
        Assert.Equal(1, buffer.ActiveReaderCount);
        RingTestUtil.WriteSequence(buffer, 1_000_000, 4096, new Random(5));
        string done = Expect(child, "done ");
        output.WriteLine(ready + " | " + done + " | writer " + Inv($"kernelWaits={buffer.Counters.KernelWaits} signals={buffer.Counters.Signals}"));
        Assert.StartsWith("done ok", done, StringComparison.Ordinal);
        Assert.Equal(0, child.WaitForExit(s_timeout));
        Assert.Equal(0, buffer.EvictedReaders);
        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    [Fact]
    public void CrossProcess_SameBaseAddress_BestEffort()
    {
        int atCreator = 0;
        for (int i = 0; i < 5; i++)
        {
            string name = TestNames.Unique();
            using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
            using ChildProcess child = ChildProcess.Start("reader", name, "1000");
            string ready = Expect(child, "ready ");
            if (ready.Contains("atCreator=True", StringComparison.Ordinal))
            {
                atCreator++;
            }

            RingTestUtil.WriteSequence(buffer, 1000, 100);
            Assert.StartsWith("done ok", Expect(child, "done "), StringComparison.Ordinal);
            Assert.Equal(0, child.WaitForExit(s_timeout));
        }

        output.WriteLine(Inv($"same base address in {atCreator} of 5 spawns"));
        Assert.True(atCreator >= 1, "expected the child to map at the creator's address at least once in 5 spawns");
    }

    [Fact]
    public void CrossProcess_MultipleReaderProcesses_Broadcast()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 14, name);
        var children = new List<ChildProcess>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                ChildProcess c = ChildProcess.Start("reader", name, "300000");
                children.Add(c);
                Expect(c, "ready ");
            }

            Assert.Equal(3, buffer.ActiveReaderCount);
            RingTestUtil.WriteSequence(buffer, 300_000, 3000, new Random(6));
            foreach (ChildProcess c in children)
            {
                Assert.StartsWith("done ok", Expect(c, "done "), StringComparison.Ordinal);
                Assert.Equal(0, c.WaitForExit(s_timeout));
            }

            Assert.Equal(0, buffer.EvictedReaders);
        }
        finally
        {
            children.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public void CrossProcess_OpenByDuplicatedHandle()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using ChildProcess child = ChildProcess.Start("reader", "--stdin-handle", "5000");
        nint dup = buffer.DuplicateSectionHandleTo(child.Id);
        child.WriteLine("handle " + dup.ToString("X", CultureInfo.InvariantCulture));
        string ready = Expect(child, "ready ");
        Assert.Contains("name=null", ready, StringComparison.Ordinal);
        RingTestUtil.WriteSequence(buffer, 5000, 500);
        Assert.StartsWith("done ok", Expect(child, "done "), StringComparison.Ordinal);
        Assert.Equal(0, child.WaitForExit(s_timeout));
    }

    // ------------------------------------------------------------------ 29 / 30: child writer

    [Fact]
    public void CrossProcess_WriterCloses_ReaderCompletes()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("writer", name, "200000");
        Expect(child, "ready ");
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        Assert.False(buffer.IsWriter);
        using RingReader<long> reader = buffer.CreateReader();
        child.WriteLine("go");
        RingTestUtil.ReadSequence(reader, 200_000, 3000, new Random(7));
        Expect(child, "committed ");
        Assert.False(reader.WaitSync(1));                                    // blocks until the child disposes, then false
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.True(reader.IsCompleted);
        Assert.Equal("closed", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));
    }

    [Fact]
    public void CrossProcess_WriterKilled_ReaderDrainsThenTerminated()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("writer", name, "1000", "--crash");
        Expect(child, "ready ");
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        using RingReader<long> reader = buffer.CreateReader();
        child.WriteLine("go");
        Assert.True(reader.WaitSync(1000, TimeSpan.FromSeconds(30)));
        Expect(child, "committed ");
        child.WaitForExit(s_timeout);                                        // FailFast
        var sw = Stopwatch.StartNew();
        Assert.False(reader.WaitSync(1001, TimeSpan.FromSeconds(30)));      // 1001 will never arrive
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms to notice the dead writer");
        Assert.Equal(ReaderStatus.WriterTerminated, reader.Status);
        Assert.Equal(1000, reader.Available);
        Assert.False(reader.IsCompleted);
        RingTestUtil.ReadSequence(reader, 1000, 100);                        // everything published before the crash is drainable
        Assert.False(reader.WaitSync(1, TimeSpan.FromSeconds(5)));
        Assert.True(reader.IsCompleted);
    }

    [Fact]
    public void CrossProcess_WriterKilled_PollingReaderNoticesViaStatus()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("writer", name, "1000", "--crash");
        Expect(child, "ready ");
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        using RingReader<long> reader = buffer.CreateReader();
        child.WriteLine("go");
        RingTestUtil.WaitUntil(() => reader.Available >= 1000, "1000 elements published");
        Expect(child, "committed ");
        child.WaitForExit(s_timeout);                                        // FailFast
        var sw = Stopwatch.StartNew();
        // never blocks: TryRead/Available/Status only (a polling consumer). Status must still flip to WriterTerminated.
        RingTestUtil.WaitUntil(() => reader.Status == ReaderStatus.WriterTerminated, "Status == WriterTerminated", TimeSpan.FromSeconds(10));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms to notice the dead writer by polling");
        Assert.False(reader.IsCompleted);
        Assert.True(reader.TryRead(1000, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
        reader.Advance(1000);
        Assert.True(reader.IsCompleted);
        Assert.False(reader.WaitSync(1, TimeSpan.Zero));
    }

    [Fact]
    public async Task CrossProcess_WriterKilled_AsyncWaitReturnsFalse()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("writer", name, "500", "--crash");
        Expect(child, "ready ");
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        using RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> pending = reader.Wait(501);                          // more than the child will ever publish
        child.WriteLine("go");
        Expect(child, "committed ");
        var sw = Stopwatch.StartNew();
        Assert.False(await pending);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ReaderStatus.WriterTerminated, reader.Status);
        Assert.Equal(500, reader.Available);
    }

    [Fact]
    public void CrossProcess_WriterDeadAtJoin_WaitReturnsImmediately()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("writer", name, "100", "--crash");
        Expect(child, "ready ");
        using RingBuffer<long> keeper = RingBuffer<long>.Open(name);          // keeps the section alive across the crash
        child.WriteLine("go");
        Expect(child, "committed ");
        child.WaitForExit(s_timeout);
        using RingReader<long> reader = keeper.CreateReader();               // joins after the writer died: starts at head (100), nothing to read
        var sw = Stopwatch.StartNew();
        Assert.False(reader.WaitSync(1, TimeSpan.FromSeconds(10)));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ReaderStatus.WriterTerminated, reader.Status);
    }

    // ------------------------------------------------------------------ 26 / 27 / 28 / 35: dead readers

    [Fact]
    public void CrossProcess_ReaderKilled_WriterResumes()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
        int c = (int)buffer.Capacity;
        using ChildProcess child = ChildProcess.Start("crash-reader", name);
        string ready = Expect(child, "ready slot=");
        Assert.Equal(1, buffer.ActiveReaderCount);
        RingTestUtil.WriteSequence(buffer, c, c);                            // ring full; the child never advances
        Assert.False(buffer.TryGetBucket(1, out _));

        Task killer = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer in kernel wait");
            Thread.Sleep(100);
            child.Kill();
        });

        var sw = Stopwatch.StartNew();
        using (Bucket<long> b = buffer.GetBucket(c))
        {
            sw.Stop();
            b.Commit(c);
        }

        killer.Wait(s_timeout);
        output.WriteLine(ready + Inv($" | resumed {sw.ElapsedMilliseconds} ms after the kill was scheduled"));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"GetBucket took {sw.ElapsedMilliseconds} ms after the reader was killed");
        Assert.Equal(1, buffer.EvictedReaders);
        Assert.Equal(1, buffer.Counters.Evictions);
        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(EvictReason.Dead, RingTestUtil.Slot(buffer, 0).EvictReason);
        using RingReader<long> reuse = buffer.CreateReader();               // the slot is reusable
        Assert.Equal(0, reuse.Slot);
        Assert.Equal(2 * c, reuse.ReadCursor);
    }

    [Fact]
    public void CrossProcess_ReaderKilledBeforeBlocking_WriterEvictsOnEntry()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
        int c = (int)buffer.Capacity;
        using ChildProcess child = ChildProcess.Start("crash-reader", name);
        Expect(child, "ready slot=");
        RingTestUtil.WriteSequence(buffer, c, c);
        child.Kill();
        child.WaitForExit(s_timeout);
        var sw = Stopwatch.StartNew();
        using (Bucket<long> b = buffer.GetBucket(c))
        {
            b.Commit(c);
        }

        Assert.True(sw.ElapsedMilliseconds < 2000, $"GetBucket took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, buffer.EvictedReaders);
    }

    [Fact]
    public void CrossProcess_DeadReaderAhead_LiveLaggardAdvances_WriterResumes()
    {
        // A: live, at 0; B: advanced 10 then killed. target = c/2 => both block the writer; B is evicted, A must advance.
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
        int c = (int)buffer.Capacity;
        using ChildProcess a = ChildProcess.Start("step-reader", name);
        using ChildProcess b = ChildProcess.Start("step-reader", name);
        Expect(a, "ready slot=");
        Expect(b, "ready slot=");
        RingTestUtil.WriteSequence(buffer, c, c);
        b.WriteLine("advance 10");
        Expect(b, "advanced cursor=10");
        b.Kill();
        b.WaitForExit(s_timeout);

        Task advance = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer in kernel wait");
            Thread.Sleep(50);
            Assert.Equal(1, buffer.EvictedReaders);                          // B was evicted while the writer waited
            a.WriteLine("advance " + (c / 2).ToString(CultureInfo.InvariantCulture));
            Expect(a, "advanced cursor=");
        });

        using (Bucket<long> bucket = buffer.GetBucket(c / 2))
        {
            bucket.Commit(c / 2);
        }

        advance.Wait(s_timeout);
        Assert.Equal(1, buffer.EvictedReaders);
        Assert.Equal(1, buffer.ActiveReaderCount);
        a.WriteLine("exit");
        Assert.Equal("exiting", a.ReadLine(s_timeout));
        Assert.Equal(0, a.WaitForExit(s_timeout));
    }

    [Fact]
    public void CrossProcess_ClaimAndDie_Reclaimed()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, name);
        using ChildProcess child = ChildProcess.Start("claim-and-die", name);
        Expect(child, "opened ");
        int exit = child.WaitForExit(s_timeout);
        long word = Volatile.Read(ref RingTestUtil.Slot(buffer, 0).Word);
        output.WriteLine(Inv($"child exit=0x{exit:X} slot0 word=0x{word:X16} state={SlotWord.State(word)} pid={SlotWord.Pid(word)} evicted={buffer.EvictedReaders} transcript={string.Join(" | ", child.Transcript)}"));
        Assert.Equal(SlotState.Claimed, SlotWord.State(word));
        var readers = new List<RingReader<long>>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                readers.Add(buffer.CreateReader());                          // the 32nd finds no Free slot, sweeps, reclaims slot 0
            }

            Assert.Equal(0, readers[31].Slot);                               // the reclaimed slot went to the sweeper
            Assert.Equal(1, buffer.EvictedReaders);
            Assert.Equal(child.Id, RingTestUtil.LastEvictedPid(buffer));
            Assert.Equal(32, buffer.ActiveReaderCount);
        }
        finally
        {
            readers.ForEach(r => r.Dispose());
        }

        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    // ------------------------------------------------------------------ 31 / 32: init and name lifetime

    [Fact]
    public void CrossProcess_TornInit_CreatorDies()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("slow-init", name, "500", "--die");
        Assert.Equal("initializing", child.ReadLine(s_timeout));
        var sw = Stopwatch.StartNew();
        RingBufferInitializationException ex = Assert.Throws<RingBufferInitializationException>(() => RingBuffer<long>.Open(name));
        Assert.Contains("died", ex.Message, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Open took {sw.ElapsedMilliseconds} ms (default timeout is 5 s; the creator died after 0.5 s)");
    }

    [Fact]
    public void CrossProcess_SlowInit_OpenerWaits()
    {
        string name = TestNames.Unique();
        using ChildProcess child = ChildProcess.Start("slow-init", name, "300");
        Assert.Equal("initializing", child.ReadLine(s_timeout));
        var sw = Stopwatch.StartNew();
        using RingBuffer<long> buffer = RingBuffer<long>.Open(name);
        Assert.True(sw.ElapsedMilliseconds >= 200, $"Open returned after {sw.ElapsedMilliseconds} ms");
        Assert.Equal("ready", child.ReadLine(s_timeout));
        child.WriteLine("bye");
        Assert.Equal(0, child.WaitForExit(s_timeout));
    }

    [Fact]
    public void CrossProcess_NameLifetime()
    {
        string name = TestNames.Unique();
        RingBuffer<long> creator = RingBuffer<long>.Create(1 << 12, name);
        using ChildProcess holder = ChildProcess.Start("hold-name", name);
        Assert.Equal("holding", holder.ReadLine(s_timeout));
        creator.Dispose();
        using (RingBuffer<long> third = RingBuffer<long>.Open(name))         // the child's handle keeps the name alive
        {
            Assert.True(third.IsWriterClosed);
        }

        holder.WriteLine("release");
        Assert.Equal("released", holder.ReadLine(s_timeout));
        Assert.Equal(0, holder.WaitForExit(s_timeout));
        Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(name));
    }

    // ------------------------------------------------------------------ 34 / 25 / 38: signaling acceptance and latency

    [Fact]
    public void CrossProcess_NoSyscallWhenNobodyWaits()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 16, name, new RingBufferOptions { SpinTime = TimeSpan.FromSeconds(10) });
        using ChildProcess child = ChildProcess.Start("spin-reader", name, "1000000");
        Expect(child, "ready ");
        RingTestUtil.WriteSequence(buffer, 1_000_000, 1024, new Random(8));
        string done = Expect(child, "done ");
        output.WriteLine(done + Inv($" | writer kernelWaits={buffer.Counters.KernelWaits} signals={buffer.Counters.Signals} scans={buffer.Counters.Scans}"));
        Assert.StartsWith("done ok", done, StringComparison.Ordinal);
        Assert.Contains("kernelWaits=0", done, StringComparison.Ordinal);
        Assert.Equal(0, buffer.Counters.Signals);
        Assert.Equal(0, buffer.Counters.KernelWaits);
        Assert.Equal(0, child.WaitForExit(s_timeout));
    }

    [Fact]
    public void CrossProcess_Echo_RoundTrip()
    {
        string nameIn = TestNames.Unique();
        string nameOut = TestNames.Unique();
        const int Rounds = 20_000;
        using RingBuffer<long> outbound = RingBuffer<long>.Create(1 << 12, nameIn, new RingBufferOptions { SpinTime = TimeSpan.FromMilliseconds(1) });
        using ChildProcess child = ChildProcess.Start("echo", nameIn, nameOut, Rounds.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("ready", child.ReadLine(s_timeout));
        using RingBuffer<long> inbound = RingBuffer<long>.Open(nameOut);
        using RingReader<long> reader = inbound.CreateReader(new ReaderOptions { SpinTime = TimeSpan.FromMilliseconds(1) });

        var rtt = new long[Rounds];
        for (int i = 0; i < Rounds; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            using (Bucket<long> b = outbound.GetBucket(1))
            {
                b.Span[0] = i * 3 + 1;
                b.Commit(1);
            }

            Assert.True(reader.WaitSync(1, s_timeout), $"round {i}: status={reader.Status}");
            Assert.True(reader.TryRead(1, out Chunk<long> chunk));
            Assert.Equal(i * 3 + 1, chunk.Data.Span[0]);
            reader.Advance(1);
            rtt[i] = Stopwatch.GetTimestamp() - t0;
        }

        Assert.Equal("done", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));
        Array.Sort(rtt);
        double toUs = 1e6 / Stopwatch.Frequency;
        double p50 = rtt[Rounds / 2] * toUs;
        double p99 = rtt[Rounds * 99 / 100] * toUs;
        output.WriteLine(Inv($"echo RTT over {Rounds} rounds: p50={p50:F2} us p99={p99:F2} us max={rtt[^1] * toUs:F1} us; reader kernelWaits={reader.Counters.KernelWaits} spinSuccesses={reader.Counters.SpinSuccesses}"));
        Assert.True(p50 < 500, $"p50 RTT {p50:F1} us is above the loose regression bound");
    }

    [Fact]
    public void CrossProcess_JoinStorm()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 14, name, new RingBufferOptions { LivenessCheckInterval = TimeSpan.FromMilliseconds(2) });
        var children = new List<ChildProcess>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                ChildProcess c = ChildProcess.Start("join-storm", name, "500");
                children.Add(c);
                Assert.Equal("storming", c.ReadLine(s_timeout));
            }

            RingTestUtil.WriteSequence(buffer, 2_000_000, 1500, new Random(9));
            foreach (ChildProcess c in children)
            {
                string done = Expect(c, "done ");
                output.WriteLine(done);
                Assert.Equal(0, c.WaitForExit(s_timeout));
            }

            Assert.Equal(0, buffer.EvictedReaders);
        }
        finally
        {
            children.ForEach(c => c.Dispose());
        }
    }
}
