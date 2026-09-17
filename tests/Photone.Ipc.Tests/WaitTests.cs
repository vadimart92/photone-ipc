using System.Diagnostics;

namespace Photone.Ipc.Tests;

public sealed class WaitTests
{
    private static RingBuffer<long> NewBuffer() => RingBuffer<long>.Create(1 << 12);

    [Fact]
    public void Reader_WaitSync_UnblocksOnCommit()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        Task writer = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
            Thread.Sleep(100);
            RingTestUtil.WriteSequence(buffer, 10, 10);
        });
        var sw = Stopwatch.StartNew();
        Assert.True(reader.WaitSync(10));
        Assert.True(sw.ElapsedMilliseconds >= 80, $"elapsed {sw.ElapsedMilliseconds} ms");
        writer.Wait(RingTestUtil.Short);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
        Assert.Equal(1, reader.Counters.KernelWaits);
        Assert.Equal(1, buffer.Counters.Signals);
        Assert.False(RingTestUtil.ReaderBitSet(buffer, reader.Slot));
    }

    [Fact]
    public void Reader_WaitSync_AlreadyAvailable_NoKernel()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.True(reader.WaitSync(10));
        Assert.True(reader.WaitSync(5, TimeSpan.Zero));
        Assert.True(reader.WaitSync(0));
        Assert.Equal(0, reader.Counters.KernelWaits);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.WaitSync((int)buffer.Capacity + 1));
    }

    [Fact]
    public void Reader_WaitSync_Timeout_False()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        var sw = Stopwatch.StartNew();
        Assert.False(reader.WaitSync(1, TimeSpan.FromMilliseconds(100)));
        Assert.True(sw.ElapsedMilliseconds >= 80, $"elapsed {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ReaderStatus.Active, reader.Status);
        Assert.False(RingTestUtil.ReaderBitSet(buffer, reader.Slot));
    }

    [Fact]
    public void Reader_WaitSync_ZeroTimeout_Polls()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        Assert.False(reader.WaitSync(1, TimeSpan.Zero));
        RingTestUtil.WriteSequence(buffer, 1, 1);
        Assert.True(reader.WaitSync(1, TimeSpan.Zero));
        Assert.Equal(0, reader.Counters.KernelWaits);
    }

    [Fact]
    public async Task Reader_Wait_Async_CompletesOnCommit()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> wait = reader.Wait(10);
        Assert.False(wait.IsCompleted);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.True(await wait);
        Assert.Equal(10, reader.Available);
        Assert.Equal(1, buffer.Counters.Signals);
        Assert.Equal(1, reader.Counters.KernelWaits);
        Assert.True(await reader.Wait(10));                                   // fast path
        Assert.Equal(1, reader.Counters.KernelWaits);
        {
            Assert.True(reader.TryRead(10, out Chunk<long> chunk));
            RingTestUtil.VerifyChunk(chunk);
            reader.Advance(10);
        }
    }

    [Fact]
    public async Task Reader_Wait_Async_ReusableAcrossManyRounds()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot) || reader.Available >= 5, "reader waiting");
                RingTestUtil.WriteSequence(buffer, 5, 5);
                Thread.Sleep(1);
            }
        });
        for (int i = 0; i < 200; i++)
        {
            Assert.True(await reader.Wait(5));
            {
                Assert.True(reader.TryRead(5, out Chunk<long> chunk));
                RingTestUtil.VerifyChunk(chunk);
                reader.Advance(5);
            }
        }

        await writer.WaitAsync(RingTestUtil.Long);
        Assert.Equal(1000, reader.ReadCursor);
    }

    [Fact]
    public async Task Reader_Wait_Async_Timeout_False()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        var sw = Stopwatch.StartNew();
        Assert.False(await reader.Wait(1, TimeSpan.FromMilliseconds(100)));
        Assert.True(sw.ElapsedMilliseconds >= 80, $"elapsed {sw.ElapsedMilliseconds} ms");
        Assert.False(await reader.Wait(1, TimeSpan.Zero));
        Assert.True(await reader.Wait(0, TimeSpan.Zero));
        Assert.Equal(ReaderStatus.Active, reader.Status);
    }

    [Fact]
    public async Task Reader_Wait_Async_Cancel_ThrowsOCE()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        using var cts = new CancellationTokenSource();
        ValueTask<bool> wait = reader.Wait(1, cts.Token);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        Assert.False(RingTestUtil.ReaderBitSet(buffer, reader.Slot));
        // the reader is still usable
        RingTestUtil.WriteSequence(buffer, 1, 1);
        Assert.True(await reader.Wait(1));
        // pre-cancelled token
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.Wait(2, pre.Token));
    }

    [Fact]
    public async Task Reader_Wait_Concurrent_Throws()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> wait = reader.Wait(1);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        Assert.Throws<InvalidOperationException>(() => reader.WaitSync(1, TimeSpan.FromMilliseconds(50)));
        Assert.Throws<InvalidOperationException>(() => reader.Wait(1, TimeSpan.FromMilliseconds(50)));
        RingTestUtil.WriteSequence(buffer, 1, 1);
        Assert.True(await wait);
    }

    [Fact]
    public async Task Reader_Wait_NotWokenBelowThreshold()
    {
        using RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> wait = reader.Wait(100);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        RingTestUtil.WriteSequence(buffer, 50, 50);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.True(RingTestUtil.ReaderBitSet(buffer, reader.Slot));
        Assert.Equal(0, buffer.Counters.Signals);
        RingTestUtil.WriteSequence(buffer, 50, 50);
        Assert.True(await wait);
        Assert.Equal(1, buffer.Counters.Signals);
        Assert.Equal(0, reader.Counters.SpuriousWakes);
    }

    [Fact]
    public void WriterClose_ReadersDrain_ThenFalse_WithStatusClosed()
    {
        RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        Assert.Equal(ReaderStatus.Active, reader.Status);
        buffer.Dispose();
        Assert.True(buffer.IsWriterClosed);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.False(reader.IsCompleted);
        Assert.True(reader.WaitSync(10));
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
        reader.Advance(10);
        Assert.False(reader.WaitSync(1));
        Assert.False(reader.WaitSync(1, TimeSpan.FromSeconds(10)));
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.True(reader.IsCompleted);
    }

    [Fact]
    public async Task WriterClose_WakesPendingAsyncWait()
    {
        RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> wait = reader.Wait(5);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        RingTestUtil.WriteSequence(buffer, 3, 3);
        buffer.Dispose();
        Assert.False(await wait);                                              // only 3 of 5 will ever arrive
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.Equal(3, reader.Available);
        Assert.True(await reader.Wait(3));
        Assert.False(reader.IsCompleted);
        reader.Advance(3);
        Assert.True(reader.IsCompleted);
    }

    [Fact]
    public void WriterClose_WakesPendingSyncWait_OnOtherThread()
    {
        RingBuffer<long> buffer = NewBuffer();
        using RingReader<long> reader = buffer.CreateReader();
        Task<bool> wait = Task.Run(() => reader.WaitSync(1));
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        buffer.Dispose();
        Assert.False(wait.Result);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
    }

    [Fact]
    public async Task ReaderDispose_WithPendingAsyncWait_CompletesWithObjectDisposed()
    {
        using RingBuffer<long> buffer = NewBuffer();
        RingReader<long> reader = buffer.CreateReader();
        ValueTask<bool> wait = reader.Wait(1);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        int slot = reader.Slot;
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait);
        Assert.Equal(ReaderStatus.Disposed, reader.Status);
        Assert.False(RingTestUtil.ReaderBitSet(buffer, slot));
        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Throws<ObjectDisposedException>(() => reader.Wait(1));
        Assert.Throws<ObjectDisposedException>(() => reader.TryRead(1, out _));
        reader.Dispose();                                                      // idempotent
    }

    [Fact]
    public void ReaderDispose_WhileWaitSyncOnOtherThread_Unblocks()
    {
        using RingBuffer<long> buffer = NewBuffer();
        RingReader<long> reader = buffer.CreateReader();
        Task<bool> wait = Task.Run(() => reader.WaitSync(1));
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "reader bit set");
        reader.Dispose();
        AggregateException ex = Assert.Throws<AggregateException>(() => wait.Wait(RingTestUtil.Short));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
        Assert.Equal(0, buffer.ActiveReaderCount);
    }

    [Fact]
    public void ReaderDispose_WhileSpinningForever_Unblocks()
    {
        using RingBuffer<long> buffer = NewBuffer();
        RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan });
        Task<bool> wait = Task.Run(() => reader.WaitSync(1));
        Thread.Sleep(50);
        Assert.False(wait.IsCompleted);
        reader.Dispose();
        AggregateException ex = Assert.Throws<AggregateException>(() => wait.Wait(RingTestUtil.Short));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [Fact]
    public void SpinOnlyReader_NoKernelWaits_NoSignals()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { SpinTime = TimeSpan.FromSeconds(10) });
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan });
        const long Total = 200_000;
        Task consumer = Task.Run(() => RingTestUtil.ReadSequence(reader, Total, 512, new Random(1)));
        RingTestUtil.WriteSequence(buffer, Total, 512, new Random(2));
        consumer.Wait(RingTestUtil.Long);
        Assert.Equal(0, buffer.Counters.Signals);
        Assert.Equal(0, buffer.Counters.KernelWaits);
        Assert.Equal(0, reader.Counters.KernelWaits);
        Assert.Equal(0, reader.Counters.Signals);
        Assert.Equal(Total, reader.ReadCursor);
    }
}
