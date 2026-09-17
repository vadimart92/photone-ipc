using System.Buffers;
using System.Diagnostics;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

public sealed unsafe class ReaderTests
{
    [Fact]
    public void LateReader_StartsAtHead()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingTestUtil.WriteSequence(buffer, 100, 50);
        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(100, reader.ReadCursor);
        Assert.Equal(0, reader.Available);
        Assert.False(reader.TryRead(1, out _));
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.Equal(10, reader.Available);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(100, chunk.Cursor);
        RingTestUtil.VerifyChunk(chunk);
    }

    [Fact]
    public void SeveralReaders_IndependentCursors()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> r1 = buffer.CreateReader();
        using RingReader<long> r2 = buffer.CreateReader();
        Assert.NotEqual(r1.Slot, r2.Slot);
        Assert.Equal(2, buffer.ActiveReaderCount);
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.True(r1.TryRead(10, out Chunk<long> c1));
        RingTestUtil.VerifyChunk(c1);
        r1.Advance(10);
        Assert.Equal(10, r2.Available);
        Assert.Equal(0, r1.Available);
        RingTestUtil.WriteSequence(buffer, 5, 5);
        Assert.Equal(15, r2.Available);
        Assert.Equal(5, r1.Available);
        Assert.True(r2.TryRead(15, out Chunk<long> c2));
        RingTestUtil.VerifyChunk(c2);
        r2.Advance(15);
        Assert.Equal(10, buffer.FreeSpace - (buffer.Capacity - 15));   // slowest reader (r1 at 10) gates the writer: free = C - (15 - 10)
    }

    [Fact]
    public void TryRead_ExactOrFalse()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 7, 7);
        Assert.False(reader.TryRead(8, out Chunk<long> none));
        Assert.Equal(0, none.Length);
        Assert.True(reader.TryRead(7, out Chunk<long> all));
        Assert.Equal(7, all.Length);
        Assert.True(reader.TryRead(3, out Chunk<long> some));
        Assert.Equal(3, some.Length);
        Assert.Equal(0, some.Cursor);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.TryRead((int)buffer.Capacity + 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.TryRead(-1, out _));
    }

    [Fact]
    public void TryRead_Zero_EmptyChunk()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        Assert.True(reader.TryRead(0, out Chunk<long> chunk));
        Assert.Equal(0, chunk.Length);
        Assert.True(chunk.Data.Span.IsEmpty);
        Assert.Equal(0, chunk.Cursor);
    }

    [Fact]
    public void TryRead_Data_IsTheRingItself()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(10, chunk.Data.Length);
        RingTestUtil.VerifyChunk(chunk);

        using MemoryHandle pin = chunk.Data.Pin();                      // no copy and nothing to pin: the memory is the data region of the mapping
        Assert.Equal((nint)buffer.Data, (nint)pin.Pointer);
        Assert.Equal(7, ((long*)pin.Pointer)[7]);

        reader.Advance(4);                                              // a partial advance leaves the chunk (and its memory) as it was
        Assert.Equal(10, chunk.Length);
        Assert.Equal(0, chunk.Cursor);
        RingTestUtil.VerifyChunk(chunk);
    }

    [Fact]
    public void Advance_Partial()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 100, 100);
        Assert.True(reader.TryRead(100, out _));
        reader.Advance(90);
        Assert.Equal(90, reader.ReadCursor);
        Assert.Equal(10, reader.Available);
        Assert.True(reader.TryRead(10, out Chunk<long> rest));
        Assert.Equal(90, rest.Cursor);
        RingTestUtil.VerifyChunk(rest);
        reader.Advance(10);
        Assert.Equal(0, reader.Available);
    }

    [Fact]
    public void Advance_TooMuch_Throws()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(11));   // cached W is stale (0) until observed...
        Assert.Equal(10, reader.Available);                                      // ...now it is 10
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(-1));
        reader.Advance(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(1));
    }

    [Fact]
    public void Advance_Zero_NoOp()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        reader.Advance(0);
        Assert.Equal(0, reader.ReadCursor);
        Assert.Equal(0, reader.Counters.Signals);
    }

    [Fact]
    public void Writer_BlocksOnSlowReader_ThenUnblocks()
    {
        // a long liveness slice keeps WriterWaiting stable while the test inspects the signal counters
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { LivenessCheckInterval = TimeSpan.FromSeconds(30) });
        using RingReader<long> reader = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);
        Assert.Equal(0, buffer.FreeSpace);

        Task advance = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer in kernel wait");
            Thread.Sleep(200);
            Assert.Equal(c, reader.Available);
            reader.Advance(c / 2);
        });

        var sw = Stopwatch.StartNew();
        using (Bucket<long> b = buffer.GetBucket(c / 2))
        {
            sw.Stop();
            Assert.Equal(c, b.Cursor);
            b.Commit(c / 2);
        }

        advance.Wait(RingTestUtil.Short);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"elapsed {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, buffer.Counters.KernelWaits);
        Assert.Equal(0, buffer.Counters.Evictions);
        Assert.Equal(1, reader.Counters.Signals);
        Assert.False(RingTestUtil.WriterIsWaiting(buffer));
    }

    [Fact]
    public void Writer_SpinPhaseSucceeds_NoKernelWait()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { SpinTime = TimeSpan.FromSeconds(5) });
        using RingReader<long> reader = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);
        Task advance = Task.Run(() =>
        {
            Thread.Sleep(50);
            _ = reader.Available;
            reader.Advance(c);
        });
        using (Bucket<long> b = buffer.GetBucket(c))
        {
            b.Commit(c);
        }

        advance.Wait(RingTestUtil.Short);
        Assert.Equal(0, buffer.Counters.KernelWaits);
        Assert.Equal(0, reader.Counters.Signals);   // writer never published WriterWaiting
    }

    [Fact]
    public void ReaderDispose_WakesBlockedWriter()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingReader<long> reader = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);

        Task dispose = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer in kernel wait");
            reader.Dispose();
        });

        using (Bucket<long> b = buffer.GetBucket(c))
        {
            b.Commit(c);
        }

        dispose.Wait(RingTestUtil.Short);
        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(ReaderStatus.Disposed, reader.Status);
        Assert.Equal(1, buffer.Counters.KernelWaits);
        Assert.Equal(0, buffer.EvictedReaders);
    }

    [Fact]
    public void TooManyReaders_33rd_Throws()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        var readers = new List<RingReader<long>>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                readers.Add(buffer.CreateReader());
            }

            Assert.Equal(32, buffer.ActiveReaderCount);
            Assert.Throws<TooManyReadersException>(() => buffer.CreateReader());
            readers[5].Dispose();
            using RingReader<long> again = buffer.CreateReader();
            Assert.Equal(5, again.Slot);
        }
        finally
        {
            foreach (RingReader<long> r in readers)
            {
                r.Dispose();
            }
        }

        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(0, buffer.EvictedReaders);
    }

    [Fact]
    public void SlotReuse_AfterDispose_SeqIncrements()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingReader<long> r1 = buffer.CreateReader();
        Assert.Equal(0, r1.Slot);
        long w1 = Volatile.Read(ref RingTestUtil.Slot(buffer, 0).Word);
        Assert.Equal(SlotState.Active, SlotWord.State(w1));
        Assert.Equal(Environment.ProcessId, SlotWord.Pid(w1));
        Assert.Equal(1u, SlotWord.Seq(w1));
        r1.Dispose();
        long free = Volatile.Read(ref RingTestUtil.Slot(buffer, 0).Word);
        Assert.Equal(SlotState.Free, SlotWord.State(free));
        Assert.Equal(1u, SlotWord.Seq(free));
        Assert.Equal(0, SlotWord.Pid(free));
        using RingReader<long> r2 = buffer.CreateReader();
        Assert.Equal(0, r2.Slot);
        long w2 = Volatile.Read(ref RingTestUtil.Slot(buffer, 0).Word);
        Assert.Equal(2u, SlotWord.Seq(w2));
        Assert.Equal(SlotState.Active, SlotWord.State(w2));
    }

    [Fact]
    public void SweepDeadSlots_ReclaimsFakeDeadSlot()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        // slot 5: Active, our own PID but a wrong start time => "PID reused" => dead
        ref ReaderSlot s = ref RingTestUtil.Slot(buffer, 5);
        s.ProcessStartTime = 1;
        s.ReadCursor = 0;
        Volatile.Write(ref s.Word, SlotWord.Make(SlotState.Active, 7, Environment.ProcessId));
        // slot 6: Claimed by a PID that cannot exist
        ref ReaderSlot t = ref RingTestUtil.Slot(buffer, 6);
        t.ProcessStartTime = 1;
        t.ClaimTick = 1;
        Volatile.Write(ref t.Word, SlotWord.Make(SlotState.Claimed, 3, 0x7FFF_FFF0));
        Interlocked.Or(ref buffer.Header->ActiveMask, 1UL << 5);

        Assert.Equal(2, buffer.SweepDeadSlots());
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref s.Word)));
        Assert.Equal(7u, SlotWord.Seq(Volatile.Read(ref s.Word)));
        Assert.Equal(EvictReason.Dead, s.EvictReason);
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref t.Word)));
        Assert.Equal(EvictReason.StuckClaim, t.EvictReason);
        Assert.Equal(2, buffer.EvictedReaders);
        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(0, buffer.SweepDeadSlots());
    }

    [Fact]
    public void CreateReader_SweepsWhenAllSlotsLookDead()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        for (int i = 0; i < 32; i++)
        {
            ref ReaderSlot s = ref RingTestUtil.Slot(buffer, i);
            s.ProcessStartTime = 1;
            Volatile.Write(ref s.Word, SlotWord.Make(SlotState.Active, 1, Environment.ProcessId));
        }

        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(0, reader.Slot);
        Assert.Equal(32, buffer.EvictedReaders);
        Assert.Equal(2u, SlotWord.Seq(Volatile.Read(ref RingTestUtil.Slot(buffer, 0).Word)));
    }

    [Fact]
    public void PidReuse_Guard_WriterEvictsStaleSlot()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c / 2, c / 2);
        // a stale Active slot at cursor 0 (own PID, wrong start time): the writer must evict it instead of waiting forever
        ref ReaderSlot s = ref RingTestUtil.Slot(buffer, 3);
        s.ProcessStartTime = 1;
        s.ReadCursor = 0;
        s.WaitFor = long.MaxValue;
        Volatile.Write(ref s.Word, SlotWord.Make(SlotState.Active, 9, Environment.ProcessId));
        Interlocked.Or(ref buffer.Header->ActiveMask, 1UL << 3);

        var sw = Stopwatch.StartNew();
        using (Bucket<long> b = buffer.GetBucket(c))
        {
            b.Commit(c);
        }

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, buffer.EvictedReaders);
        Assert.Equal(1, buffer.Counters.Evictions);
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref s.Word)));
        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    [Fact]
    public void WriterWaitFor_CrossingFilter_OnlyLaggardSignals()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { LivenessCheckInterval = TimeSpan.FromSeconds(30) });
        using RingReader<long> fast = buffer.CreateReader();
        using RingReader<long> slow = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);
        Assert.Equal(c, fast.Available);
        Assert.Equal(c, slow.Available);

        Task writer = Task.Run(() =>
        {
            using Bucket<long> b = buffer.GetBucket(c / 2);      // target = c/2: both readers must reach c/2
            b.Commit(c / 2);
        });

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer in kernel wait");
        Assert.Equal(c / 2, Volatile.Read(ref buffer.Header->WriterWaitFor));

        fast.Advance(10);                                        // 0 -> 10: does not cross c/2 => filtered, no syscall
        Assert.Equal(0, fast.Counters.Signals);
        Assert.True(RingTestUtil.WriterIsWaiting(buffer));

        fast.Advance(c - 10);                                    // crosses => signals; the writer re-scans and sleeps again (slow still at 0)
        Assert.Equal(1, fast.Counters.Signals);
        Assert.False(writer.Wait(100));
        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer back in kernel wait");

        slow.Advance(c / 2 - 1);                                 // 0 -> c/2-1: not crossing
        Assert.Equal(0, slow.Counters.Signals);
        Assert.False(writer.Wait(100));

        slow.Advance(1);                                         // crosses c/2 => the writer resumes
        Assert.True(writer.Wait(RingTestUtil.Short));
        Assert.Equal(1, slow.Counters.Signals);
        Assert.Equal(1, fast.Counters.Signals);
        Assert.Equal(c + c / 2, buffer.WriteCursor);
    }
}
