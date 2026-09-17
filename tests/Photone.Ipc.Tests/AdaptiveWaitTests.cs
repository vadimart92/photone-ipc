using System.Diagnostics;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

/// <summary>The adaptive spin policy, the waiter thread's arm handshake and synchronous continuations (DESIGN §14).</summary>
[Collection("ipc")]   // timing-sensitive, and its blocking tests inject thread-pool threads: run in isolation
public sealed class AdaptiveWaitTests
{
    private static long Ticks(double microseconds) => SpinClock.ToTicks(TimeSpan.FromTicks((long)(microseconds * 10)));

    // ------------------------------------------------------------------ SpinPolicy (pure)

    [Fact]
    public void Policy_Default_StopsSpinningAfterLongGaps_IgnoresSingleJitter_AndResumes()
    {
        var p = new SpinPolicy(TimeSpan.FromMicroseconds(20), null);
        Assert.Equal(Ticks(20), p.Window);
        Assert.Equal(Ticks(20), p.Max);
        p.OnSatisfied(Ticks(1000));
        Assert.Equal(Ticks(20), p.Window);                              // one long gap in a dense stream does not switch spinning off
        for (int i = 0; i < 20; i++)
        {
            p.OnSatisfied(Ticks(1000));                                 // a steady stream of 1 ms gaps: spinning 20 µs first is pure waste
        }

        Assert.Equal(0, p.Window);
        p.OnSatisfied(Ticks(8));
        Assert.Equal(0, p.Window);                                      // one early wake-up (jitter) does not switch spinning back on
        for (int i = 0; i < 6; i++)
        {
            p.OnSatisfied(Ticks(8));                                    // the stream really became dense
        }

        Assert.Equal(Ticks(20), p.Window);                              // on again, at the configured spin time (the window is initial or zero)
    }

    [Fact]
    public void Policy_WithMax_CoversASteadyGap_AndSwitchesOffBeyondTheBound()
    {
        var p = new SpinPolicy(TimeSpan.FromMicroseconds(20), TimeSpan.FromMilliseconds(1));
        p.OnSatisfied(Ticks(200));
        Assert.Equal(Ticks(400), p.Window);                             // twice the observed gap
        for (int i = 0; i < 5; i++)
        {
            p.OnSatisfied(Ticks(100));                                  // shorter gaps: the envelope decays slowly, the window never drops below them
        }

        Assert.InRange(p.Window, Ticks(200), Ticks(400));
        p.OnSatisfied(Ticks(900));
        Assert.Equal(Ticks(1000), p.Window);                            // capped at max
        p.OnSatisfied(Ticks(5000));
        Assert.Equal(Ticks(1000), p.Window);                            // a single gap beyond the bound changes nothing
        for (int i = 0; i < 8; i++)
        {
            p.OnSatisfied(Ticks(5000));
        }

        Assert.Equal(0, p.Window);                                      // steady gaps beyond the bound: block immediately
        var q = new SpinPolicy(TimeSpan.FromMicroseconds(20), TimeSpan.FromMilliseconds(1));
        q.OnSatisfied(Ticks(2));
        Assert.Equal(Ticks(20), q.Window);                              // never below the configured spin time while spinning
    }

    [Fact]
    public void Policy_ZeroMeansNeverSpin_NegativeMeansSpinForever()
    {
        var never = new SpinPolicy(TimeSpan.Zero, null);
        never.OnSatisfied(Ticks(1));
        Assert.Equal(0, never.Window);

        var neverWithZeroMax = new SpinPolicy(TimeSpan.Zero, TimeSpan.Zero);
        neverWithZeroMax.OnSatisfied(Ticks(1));
        Assert.Equal(0, neverWithZeroMax.Window);

        var forever = new SpinPolicy(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(1));
        forever.OnSatisfied(Ticks(10_000_000));
        Assert.Equal(long.MaxValue, forever.Window);
    }

    [Fact]
    public void Policy_MaxBelowInitial_IsRaisedToInitial()
    {
        var p = new SpinPolicy(TimeSpan.FromMicroseconds(50), TimeSpan.FromMicroseconds(10));
        Assert.Equal(Ticks(50), p.Max);
    }

    // ------------------------------------------------------------------ the policy inside real waits

    [Fact]
    public void WaitSync_WithMaxSpinTime_StopsBlockingOnceTheGapIsLearned()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.Zero, MaxSpinTime = TimeSpan.FromMilliseconds(50) });
        const int Messages = 60;
        Task writer = Task.Run(() => PacedWrites(buffer, Messages, TimeSpan.FromMilliseconds(2)));
        long kernelWaitsAfterWarmup = 0;
        for (int i = 0; i < Messages; i++)
        {
            Assert.True(reader.WaitSync(1, RingTestUtil.Long));
            reader.Advance(1);
            if (i == 10)
            {
                kernelWaitsAfterWarmup = reader.Counters.KernelWaits;
            }
        }

        writer.Wait(RingTestUtil.Long);
        long late = reader.Counters.KernelWaits - kernelWaitsAfterWarmup;
        Assert.True(kernelWaitsAfterWarmup >= 1, "the first wait must block: the budget starts at zero");
        Assert.True(late <= 10, $"{late} kernel waits after warm-up: the reader should spin through 2 ms gaps with a 50 ms bound");
    }

    [Fact]
    public void WaitSync_DefaultOptions_StopsSpinningWhenGapsAreLong()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        const int Messages = 20;
        Task writer = Task.Run(() => PacedWrites(buffer, Messages, TimeSpan.FromMilliseconds(5)));
        for (int i = 0; i < Messages; i++)
        {
            Assert.True(reader.WaitSync(1, RingTestUtil.Long));
            reader.Advance(1);
        }

        writer.Wait(RingTestUtil.Long);
        Assert.Equal(0, reader.SpinWindowTicks);                        // 5 ms gaps with a 20 µs bound: spinning stopped altogether
        Assert.True(reader.Counters.KernelWaits >= Messages - 3, $"{reader.Counters.KernelWaits} kernel waits for {Messages} widely spaced messages");
    }

    private static void PacedWrites(RingBuffer<long> buffer, int count, TimeSpan interval)
    {
        long step = SpinClock.ToTicks(interval);
        long next = Stopwatch.GetTimestamp() + step;
        for (int i = 0; i < count; i++)
        {
            while (Stopwatch.GetTimestamp() < next)
            {
                Thread.SpinWait(10);
            }

            RingTestUtil.WriteSequence(buffer, 1, 1);
            next = Math.Max(next + step, Stopwatch.GetTimestamp() + step / 2);   // no bursts after a stall: every gap is at least half a step
        }
    }

    // ------------------------------------------------------------------ writer

    [Fact]
    public void Writer_WithMaxSpinTime_SpinsThroughShortBackpressure()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { SpinTime = TimeSpan.Zero, MaxSpinTime = TimeSpan.FromMilliseconds(50) });
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan });
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, c);                       // full
        using var stop = new CancellationTokenSource();
        Task drain = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (reader.WaitSync(c, TimeSpan.FromMilliseconds(100)))
                {
                    Thread.Sleep(1);                                    // ~1-2 ms per full ring: the writer waits that long for space each time
                    reader.Advance(c);
                }
            }
        });

        for (int i = 0; i < 30; i++)
        {
            using Bucket<long> b = buffer.GetBucket(c);
            b.Commit(c);
            if (i == 5)
            {
                Assert.True(buffer.Counters.KernelWaits >= 1, "the first backpressure wait must block: the budget starts at zero");
            }
        }

        long total = buffer.Counters.KernelWaits;
        stop.Cancel();
        drain.Wait(RingTestUtil.Long);
        Assert.True(total <= 12, $"{total} kernel waits in 30 full-ring waits of ~1-2 ms with a 50 ms bound");
    }

    // ------------------------------------------------------------------ async waiter

    /// <summary>
    /// Suspends one <c>Wait(1)</c> and completes it from another thread only after the continuation has certainly been registered, so the code after
    /// the returned task continues on the reader's waiter thread. Consumes the element. Never awaits anything else (that would leave the thread).
    /// </summary>
    private static async Task HopOntoWaiterAsync(RingBuffer<long> buffer, RingReader<long> reader)
    {
        ValueTask<bool> vt = reader.Wait(1);
        Assert.False(vt.IsCompleted, "the hop needs a suspended wait: nothing may be readable yet");
        Task writer = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
            Thread.Sleep(30);                                           // the awaiting side registered its continuation long ago
            RingTestUtil.WriteSequence(buffer, 1, 1);
        });
        Assert.True(await vt.ConfigureAwait(false));
        writer.Wait(RingTestUtil.Long);                                 // synchronously: stay on this thread
        reader.Advance(1);
        Assert.StartsWith("photone-wait-r", Thread.CurrentThread.Name);
    }

    [Fact]
    public Task AsyncInline_ContinuationRunsOnTheWaiterThread_AndLaterWaitsCompleteSynchronously() => Task.Run(AsyncInline_ContinuationRunsOnTheWaiterThread_AndLaterWaitsCompleteSynchronouslyCore);

    private static async Task AsyncInline_ContinuationRunsOnTheWaiterThread_AndLaterWaitsCompleteSynchronouslyCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        await HopOntoWaiterAsync(buffer, reader).ConfigureAwait(false);
        int threadId = Environment.CurrentManagedThreadId;

        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 199 && !stop.IsCancellationRequested; i++)
            {
                RingTestUtil.WriteSequence(buffer, 1, 1);
                Thread.Sleep(1);                                        // gaps longer than the spin budget: most waits block in the kernel
            }
        });

        int synchronous = 0;
        try
        {
            for (int i = 1; i < 200; i++)
            {
                ValueTask<bool> vt = reader.Wait(1, RingTestUtil.Long);
                if (vt.IsCompleted)
                {
                    synchronous++;
                }

                Assert.True(await vt.ConfigureAwait(false));
                Assert.Equal(threadId, Environment.CurrentManagedThreadId);
                {
                    Assert.True(reader.TryRead(1, out Chunk<long> chunk));
                    RingTestUtil.VerifyChunk(chunk);
                    reader.Advance(1);
                }
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(RingTestUtil.Long);
        }

        Assert.Equal(199, synchronous);
        Assert.Equal(0, reader.Counters.ArmSignals);
        Assert.True(reader.Counters.KernelWaits > 100, $"only {reader.Counters.KernelWaits} kernel waits: the synchronous waits should block with 1 ms gaps");
    }

    [Fact]
    public Task AsyncInline_ZeroAlloc_OnTheWaiterThread() => Task.Run(AsyncInline_ZeroAlloc_OnTheWaiterThreadCore);

    private static async Task AsyncInline_ZeroAlloc_OnTheWaiterThreadCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.Zero });   // every wait blocks in the kernel
        await HopOntoWaiterAsync(buffer, reader).ConfigureAwait(false);

        using var stop = new CancellationTokenSource();
        Task producer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (RingTestUtil.ReaderBitSet(buffer, reader.Slot))
                {
                    RingTestUtil.WriteSequence(buffer, 4, 4);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }
        });

        long delta;
        long kernelWaits;
        try
        {
            for (int i = 0; i < 200; i++)
            {
                Assert.True(await reader.Wait(4).ConfigureAwait(false));
                reader.Advance(4);
            }

            long kernelBefore = reader.Counters.KernelWaits;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                bool ok = await reader.Wait(4).ConfigureAwait(false);
                if (!ok)
                {
                    break;
                }

                reader.Advance(4);
            }

            delta = GC.GetAllocatedBytesForCurrentThread() - before;
            kernelWaits = reader.Counters.KernelWaits - kernelBefore;
        }
        finally
        {
            stop.Cancel();
            producer.Wait(RingTestUtil.Long);
        }

        Assert.True(kernelWaits > 1000, $"only {kernelWaits} kernel waits");
        Assert.Equal(0, delta);
    }

    [Fact]
    public Task AsyncInline_DisposeFromTheContinuation_DoesNotDeadlock() => Task.Run(AsyncInline_DisposeFromTheContinuation_DoesNotDeadlockCore);

    private static async Task AsyncInline_DisposeFromTheContinuation_DoesNotDeadlockCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingReader<long> reader = buffer.CreateReader();
        await HopOntoWaiterAsync(buffer, reader).ConfigureAwait(false);

        // on the waiter thread: a synchronous wait with a timeout, then dispose right here
        Assert.False(await reader.Wait(1000, TimeSpan.FromMilliseconds(20)).ConfigureAwait(false));
        var sw = Stopwatch.StartNew();
        reader.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Dispose on the waiter thread took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ReaderStatus.Disposed, reader.Status);
        Assert.Throws<ObjectDisposedException>(() => reader.Wait(1));
        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    [Fact]
    public Task AsyncInline_CancellationOnTheWaiterThread() => Task.Run(AsyncInline_CancellationOnTheWaiterThreadCore);

    private static async Task AsyncInline_CancellationOnTheWaiterThreadCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        await HopOntoWaiterAsync(buffer, reader).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var sw = Stopwatch.StartNew();
        ValueTask<bool> cancelled = reader.Wait(1, cts.Token);
        Assert.True(cancelled.IsCompleted, "a wait on the waiter thread completes synchronously, here by cancellation");
        Assert.True(cancelled.IsCanceled);
        Assert.True(sw.ElapsedMilliseconds >= 30 && sw.ElapsedMilliseconds < 2000, $"cancellation took {sw.ElapsedMilliseconds} ms");
        Assert.False(RingTestUtil.ReaderBitSet(buffer, reader.Slot));

        // the reader is still usable afterwards, on this thread
        RingTestUtil.WriteSequence(buffer, 2, 2);
        Assert.True(await reader.Wait(2).ConfigureAwait(false));
    }

    [Fact]
    public Task AsyncInline_PendingWaitArmedInTheContinuation_ThenDispose_CompletesWithObjectDisposed() => Task.Run(AsyncInline_PendingWaitArmedInTheContinuation_ThenDisposeCore);

    private static async Task AsyncInline_PendingWaitArmedInTheContinuation_ThenDisposeCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        RingReader<long> reader = buffer.CreateReader(new ReaderOptions { AllowSynchronousContinuations = false });

        // A pool-mode reader: its continuation runs on the pool. Arm a wait from a thread that is NOT the waiter, then dispose from yet another thread.
        ValueTask<bool> pending = reader.Wait(1);
        Assert.False(pending.IsCompleted);
        Task dispose = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
            reader.Dispose();
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending.ConfigureAwait(false)).ConfigureAwait(false);
        dispose.Wait(RingTestUtil.Long);
        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    [Fact]
    public Task AsyncPool_WaiterSpinsAndNeedsNoArmSignal_WhenTheConsumerReArmsQuickly() => Task.Run(AsyncPool_WaiterSpinsAndNeedsNoArmSignal_WhenTheConsumerReArmsQuicklyCore);

    private static async Task AsyncPool_WaiterSpinsAndNeedsNoArmSignal_WhenTheConsumerReArmsQuicklyCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions
        {
            SpinTime = TimeSpan.FromMilliseconds(20),
            MaxSpinTime = TimeSpan.FromMilliseconds(20),
            AsyncSpinTime = TimeSpan.Zero,
            AllowSynchronousContinuations = false,
        });
        using var stop = new CancellationTokenSource();
        Task producer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                RingTestUtil.WriteSequence(buffer, 1, 1);
                Thread.Sleep(2);
            }
        });

        for (int i = 0; i < 100; i++)
        {
            Assert.True(await reader.Wait(1).ConfigureAwait(false));
            {
                Assert.True(reader.TryRead(1, out Chunk<long> chunk));
                RingTestUtil.VerifyChunk(chunk);
                reader.Advance(1);
            }
        }

        stop.Cancel();
        await producer.WaitAsync(RingTestUtil.Long).ConfigureAwait(false);
        Assert.True(reader.Counters.ArmSignals <= 10, $"{reader.Counters.ArmSignals} arm system calls: the waiter should still be spinning when the next wait arrives");
        Assert.True(reader.Counters.KernelWaits <= 10, $"{reader.Counters.KernelWaits} kernel waits with a 20 ms spin budget and 2 ms gaps");
    }

    [Fact]
    public Task AsyncPool_ManyShortWaits_WithRandomGaps_NeverHang() => Task.Run(AsyncPool_ManyShortWaits_WithRandomGaps_NeverHangCore);

    private static async Task AsyncPool_ManyShortWaits_WithRandomGaps_NeverHangCore()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions
        {
            SpinTime = TimeSpan.FromMicroseconds(50),
            MaxSpinTime = TimeSpan.FromMicroseconds(200),
            AsyncSpinTime = TimeSpan.Zero,
            AllowSynchronousContinuations = false,
        });
        const int Messages = 20_000;
        Task producer = Task.Run(() =>
        {
            var rng = new Random(7);
            for (int i = 0; i < Messages; i++)
            {
                RingTestUtil.WriteSequence(buffer, 1, 1);
                int r = rng.Next(100);
                if (r < 2)
                {
                    Thread.Sleep(1);                                    // park the waiter now and then: exercises the arm handshake
                }
                else if (r < 30)
                {
                    Thread.SpinWait(rng.Next(1, 2000));
                }
            }
        });

        for (int i = 0; i < Messages; i++)
        {
            Assert.True(await reader.Wait(1, RingTestUtil.Long).ConfigureAwait(false), $"wait {i} timed out");
            {
                Assert.True(reader.TryRead(1, out Chunk<long> chunk));
                RingTestUtil.VerifyChunk(chunk);
                reader.Advance(1);
            }
        }

        await producer.WaitAsync(RingTestUtil.Long).ConfigureAwait(false);
        Assert.Equal(Messages, reader.ReadCursor);
    }
}
