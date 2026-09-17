using System.Diagnostics;
using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

/// <summary>Regression tests for the defects confirmed in the post-implementation review (see docs/REVIEW-NOTES.md).</summary>
public sealed class ReviewRegressionTests
{
    // ------------------------------------------------------------------ writer blocked + Dispose from another thread

    [Fact]
    public void Writer_DisposedFromAnotherThread_WhileBlocked_ThrowsObjectDisposed()
    {
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, null, new RingBufferOptions { SpinTime = TimeSpan.Zero });
        using RingReader<long> reader = buffer.CreateReader();                // never advances: the ring fills up
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);
        Assert.Equal(0, buffer.FreeSpace);

        Exception? caught = null;
        var entered = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try
            {
                entered.Set();
                using Bucket<long> b = buffer.GetBucket(1);                  // blocks (kernel wait) until disposed
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        })
        { IsBackground = true };
        t.Start();
        entered.Wait();
        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "writer blocked in the kernel");

        var sw = Stopwatch.StartNew();
        buffer.Dispose();                                                    // the supported way to abort a blocked writer
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "the blocked GetBucket did not return");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms to unblock");
        Assert.IsType<ObjectDisposedException>(caught);
        Assert.True(buffer.IsWriterClosed);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);             // the mapping is still alive for the reader
        Assert.Equal(c, reader.Available);
    }

    [Fact]
    public void BufferProperties_AfterDispose_Throw()
    {
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        buffer.Dispose();
        Assert.True(buffer.IsWriterClosed);
        Assert.Throws<ObjectDisposedException>(() => buffer.WriteCursor);
        Assert.Throws<ObjectDisposedException>(() => buffer.ActiveReaderCount);
        Assert.Throws<ObjectDisposedException>(() => buffer.EvictedReaders);
        Assert.Throws<ObjectDisposedException>(() => buffer.FreeSpace);
        Assert.Equal(10, reader.Available);                                  // readers keep working until they are disposed
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Available);
    }

    // ------------------------------------------------------------------ slot identity hygiene / sweeper

    [Fact]
    public void ReaderDispose_ZeroesSlotIdentity()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingReader<long> reader = buffer.CreateReader();
        ref ReaderSlot s = ref RingTestUtil.Slot(buffer, reader.Slot);
        Assert.NotEqual(0, s.ProcessStartTime);
        Assert.NotEqual(0UL, s.ClaimTick);
        reader.Dispose();
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref s.Word)));
        Assert.Equal(0, s.ProcessStartTime);
        Assert.Equal(0UL, s.ClaimTick);
        Assert.Equal(long.MaxValue, s.WaitFor);
    }

    [Fact]
    public void Evict_ZeroesSlotIdentity()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        ref ReaderSlot s = ref RingTestUtil.Slot(buffer, 3);
        s.ProcessStartTime = 1;
        s.ClaimTick = 1;
        long word = SlotWord.Make(SlotState.Active, 9, Environment.ProcessId);
        Volatile.Write(ref s.Word, word);
        Assert.True(buffer.Evict(3, word, EvictReason.Dead));
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref s.Word)));
        Assert.Equal(0, s.ProcessStartTime);
        Assert.Equal(0UL, s.ClaimTick);
    }

    [Fact]
    public void SweepDeadSlots_NeverReclaimsAClaimOfALiveProcess()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        // slot 4: a fresh claim whose identity fields are not written yet (the window right after the claim CAS)
        ref ReaderSlot fresh = ref RingTestUtil.Slot(buffer, 4);
        fresh.ProcessStartTime = 0;
        fresh.ClaimTick = 0;
        long freshWord = SlotWord.Make(SlotState.Claimed, 2, Environment.ProcessId);
        Volatile.Write(ref fresh.Word, freshWord);
        // slot 5: a claim by this (live) process that is very old
        ref ReaderSlot old = ref RingTestUtil.Slot(buffer, 5);
        old.ProcessStartTime = ProcessLiveness.OwnStartTime;
        old.ClaimTick = 1;
        long oldWord = SlotWord.Make(SlotState.Claimed, 2, Environment.ProcessId);
        Volatile.Write(ref old.Word, oldWord);

        Assert.Equal(0, buffer.SweepDeadSlots());
        Assert.Equal(freshWord, Volatile.Read(ref fresh.Word));
        Assert.Equal(oldWord, Volatile.Read(ref old.Word));
        Assert.Equal(0, buffer.EvictedReaders);

        // and a claim whose process really is gone is still reclaimed
        old.ProcessStartTime = 1;                                            // PID reused => the original claimant is dead
        Assert.Equal(1, buffer.SweepDeadSlots());
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref old.Word)));
        Assert.Equal(EvictReason.StuckClaim, old.EvictReason);
    }

    // ------------------------------------------------------------------ abandoned reader

    [Fact]
    public void AbandonedReader_FinalizerFreesTheSlot()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        int slot = CreateAndAbandon(buffer);
        Assert.Equal(1, buffer.ActiveReaderCount);
        for (int i = 0; i < 5 && buffer.ActiveReaderCount != 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref RingTestUtil.Slot(buffer, slot).Word)));
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, 2L * c, c);                       // the writer is not held back by a ghost
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CreateAndAbandon(RingBuffer<long> buffer)
    {
        RingReader<long> reader = buffer.CreateReader();
        return reader.Slot;
    }

    // ------------------------------------------------------------------ async wait edge cases

    [Fact]
    public async Task AsyncWait_ZeroTimeout_DoesNotSpin()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { AsyncSpinTime = TimeSpan.FromSeconds(2) });
        var sw = Stopwatch.StartNew();
        Assert.False(await reader.Wait(1, TimeSpan.Zero));
        Assert.True(sw.ElapsedMilliseconds < 500, $"Wait(1, Zero) spun for {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Wait_UsesCachedWriteCursor_WhenItAlreadySatisfiesTheRequest()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 100, 100);
        Assert.Equal(100, reader.Available);                                 // caches W = 100
        Assert.True(reader.WaitSync(50, TimeSpan.Zero));
        Assert.True(reader.Wait(100, TimeSpan.Zero).Result);
        Assert.False(reader.WaitSync(101, TimeSpan.Zero));
    }

    [Fact]
    public async Task ReaderDispose_RacingWait_NeverCrashes()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        for (int i = 0; i < 40; i++)
        {
            RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.Zero, AsyncSpinTime = TimeSpan.Zero });
            var go = new ManualResetEventSlim(false);
            Task waiter = Task.Run(async () =>
            {
                go.Wait();
                try
                {
                    if ((i & 1) == 0)
                    {
                        await reader.Wait(1);
                    }
                    else
                    {
                        reader.WaitSync(1);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            });
            go.Set();
            if ((i & 3) == 0)
            {
                Thread.SpinWait(i * 50);
            }

            reader.Dispose();
            await waiter.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ReaderStatus.Disposed, reader.Status);
        }

        Assert.Equal(0, buffer.ActiveReaderCount);
    }
}
