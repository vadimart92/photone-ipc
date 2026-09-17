using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

public sealed class LifetimeTests
{
    [Fact]
    public void Create_Anonymous_HasGuidName()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(100);
        Assert.NotNull(buffer.Name);
        Assert.StartsWith("Local\\photone.", buffer.Name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(buffer.Name["Local\\photone.".Length..], "N", out _));
        Assert.NotEqual(0UL, buffer.InstanceId);
        Assert.True(buffer.IsWriter);
        Assert.True(buffer.IsMappedAtCreatorAddress);
        Assert.Equal(buffer.BaseAddress, buffer.CreatorBaseAddress);
        Assert.Equal(sizeof(int), buffer.ElementSize);
        Assert.Equal(buffer.Capacity * sizeof(int), buffer.DataBytes);
    }

    [Fact]
    public void Name_Normalization()
    {
        string plain = TestNames.Unique();
        using (RingBuffer<int> b = RingBuffer<int>.Create(100, plain))
        {
            Assert.Equal("Local\\photone." + plain, b.Name);
            using RingBuffer<int> o = RingBuffer<int>.Open(plain);
            Assert.Equal(b.Name, o.Name);
            Assert.Equal(b.InstanceId, o.InstanceId);
        }

        string explicitName = "Local\\" + TestNames.Unique();
        using (RingBuffer<int> b = RingBuffer<int>.Create(100, explicitName))
        {
            Assert.Equal(explicitName, b.Name);
            using RingBuffer<int> o = RingBuffer<int>.Open(explicitName);
            Assert.Equal(explicitName, o.Name);
        }

        Assert.Throws<ArgumentException>(() => RingBuffer<int>.Create(100, string.Empty));
        Assert.Throws<ArgumentException>(() => RingBuffer<int>.Create(100, "a\\b"));
        Assert.Throws<ArgumentException>(() => RingBuffer<int>.Open(string.Empty));
    }

    [Fact]
    public void Create_ThenCreateSameName_ThrowsAlreadyExists()
    {
        string name = TestNames.Unique();
        using RingBuffer<int> buffer = RingBuffer<int>.Create(100, name);
        RingBufferAlreadyExistsException ex = Assert.Throws<RingBufferAlreadyExistsException>(() => RingBuffer<int>.Create(100, name));
        Assert.Equal(Kernel.ERROR_ALREADY_EXISTS, ex.NativeErrorCode);
    }

    [Fact]
    public void Open_MissingName_ThrowsNotFound()
    {
        RingBufferNotFoundException ex = Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<int>.Open(TestNames.Unique()));
        Assert.Equal(Kernel.ERROR_FILE_NOT_FOUND, ex.NativeErrorCode);
    }

    [Fact]
    public void Open_WrongElementType_ThrowsLayout()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> buffer = RingBuffer<long>.Create(100, name);
        Assert.Throws<RingBufferLayoutException>(() => RingBuffer<int>.Open(name));      // size mismatch
        Assert.Throws<RingBufferLayoutException>(() => RingBuffer<double>.Open(name));   // same size, different type hash
        using RingBuffer<long> ok = RingBuffer<long>.Open(name);
        Assert.Equal(buffer.Capacity, ok.Capacity);
    }

    [Fact]
    public void Open_ForeignSection_TimesOut()
    {
        string name = TestNames.UniqueSection();
        using SafeSectionHandle raw = MirroredSection.CreateSection(65536, name);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RingBufferInitializationException ex = Assert.Throws<RingBufferInitializationException>(
            () => RingBuffer<int>.Open(name, new RingBufferOptions { InitializationTimeout = TimeSpan.FromMilliseconds(200) }));
        Assert.Contains("initialized", ex.Message, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds >= 150 && sw.ElapsedMilliseconds < 3000, $"elapsed {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Open_UnknownBackendId_ThrowsLayout()
    {
        string name = TestNames.Unique();
        using RingBuffer<int> buffer = RingBuffer<int>.Create(100, name);
        uint saved = RingTestUtil.SwapBackendId(buffer, 42);
        try
        {
            Assert.Throws<RingBufferLayoutException>(() => RingBuffer<int>.Open(name));
        }
        finally
        {
            RingTestUtil.SwapBackendId(buffer, saved);
        }

        using RingBuffer<int> ok = RingBuffer<int>.Open(name);
    }

    [Fact]
    public void Open_SameProcess_ReaderRole_SharesDataAndEvents()
    {
        string name = TestNames.Unique();
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name);
        using RingBuffer<long> opened = RingBuffer<long>.Open(name);
        Assert.False(opened.IsWriter);
        Assert.False(opened.IsMappedAtCreatorAddress);                  // the creator's range is occupied in this process
        Assert.Equal(writer.CreatorBaseAddress, opened.CreatorBaseAddress);
        Assert.NotEqual(writer.BaseAddress, opened.BaseAddress);
        using RingReader<long> reader = opened.CreateReader();
        Assert.Equal(1, writer.ActiveReaderCount);
        Task producer = Task.Run(() =>
        {
            RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(opened, reader.Slot), "reader waiting");
            RingTestUtil.WriteSequence(writer, 1000, 100);
        });
        Assert.True(reader.WaitSync(1000));
        producer.Wait(RingTestUtil.Short);
        Assert.True(reader.TryRead(1000, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
        reader.Advance(1000);
        Assert.Equal(1, writer.Counters.Signals);
        Assert.Equal(writer.WriteCursor, opened.WriteCursor);
    }

    [Fact]
    public void Open_ByDuplicatedHandle_SameProcess()
    {
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12);
        nint dup = writer.DuplicateSectionHandleTo(Environment.ProcessId);
        Assert.NotEqual(0, dup);
        using RingBuffer<long> opened = RingBuffer<long>.Open(new SafeSectionHandle(dup, ownsHandle: true));
        Assert.Null(opened.Name);
        Assert.Equal(writer.InstanceId, opened.InstanceId);
        using RingReader<long> reader = opened.CreateReader();
        RingTestUtil.WriteSequence(writer, 100, 100);
        Assert.True(reader.WaitSync(100, TimeSpan.FromSeconds(5)));
        Assert.True(reader.TryRead(100, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
    }

    [Fact]
    public void Dispose_WithLiveLocalReader_DefersUnmap()
    {
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        ulong baseAddress = buffer.BaseAddress;
        string name = buffer.Name!;
        RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 10, 10);
        buffer.Dispose();
        Assert.NotEqual(TestKernel.MEM_FREE, TestKernel.QueryState(baseAddress, out _));    // still mapped: the reader holds it
        using (RingBuffer<long> late = RingBuffer<long>.Open(name))                          // the section handle lives with the mapping, so the name is still resolvable
        {
            Assert.True(late.IsWriterClosed);
        }

        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        RingTestUtil.VerifyChunk(chunk);
        reader.Advance(10);
        Assert.False(reader.WaitSync(1));
        reader.Dispose();
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(baseAddress, out _));
        Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(name));      // last local handle gone => the name is gone
        Assert.Throws<ObjectDisposedException>(() => buffer.CreateReader());
        buffer.Dispose();   // idempotent
    }

    [Fact]
    public void Dispose_Writer_WithOutstandingBucket_DropsIt()
    {
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        RingTestUtil.WriteSequence(buffer, 5, 5);
        Bucket<long> b = buffer.GetBucket(10);
        b.Span.Fill(-1);
        buffer.Dispose();
        Assert.Equal(5, reader.Available);
        Assert.True(buffer.IsWriterClosed);
        try
        {
            b.Commit(10);                                                   // the bucket was closed by Dispose
            Assert.Fail("Commit on a closed bucket must throw");
        }
        catch (InvalidOperationException)
        {
        }

        b.Dispose();
        Assert.Equal(5, reader.Available);
    }

    [Fact]
    public void ZeroAlloc_HotPath()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        Run(buffer, reader, 10_000);                                   // warm-up (tiering, first-touch)
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(buffer, reader, 1_000_000);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);

        static void Run(RingBuffer<long> buffer, RingReader<long> reader, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                using (Bucket<long> b = buffer.GetBucket(16))
                {
                    b.Span[0] = i;
                    b.Commit(16);
                }

                if (!reader.WaitSync(16))
                {
                    throw new InvalidOperationException("WaitSync");
                }

                ValueTask<bool> vt = reader.Wait(16);
                if (!vt.IsCompletedSuccessfully || !vt.Result)
                {
                    throw new InvalidOperationException("Wait");
                }

                if (!reader.TryRead(16, out Chunk<long> c) || c.Span[0] != i)
                {
                    throw new InvalidOperationException("TryRead");
                }

                reader.Advance(16);
            }
        }
    }

    [Fact]
    public async Task ZeroAlloc_AsyncSuspendResume_SteadyState()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { AllowSynchronousContinuations = false });   // every wait suspends and resumes on the pool
        using var stop = new CancellationTokenSource();
        int measuring = 0;
        Task<long> producer = Task.Run(() =>
        {
            // The writer's signalling path (Commit -> SignalReaders -> SetEvent) measured exactly on its own thread, over the same steady-state
            // window as the reader (the very first SetEvent of the process allocates 48 bytes once, in the runtime's lazy P/Invoke binding).
            long t0 = -1;
            while (!stop.IsCancellationRequested)
            {
                if (t0 < 0 && Volatile.Read(ref measuring) != 0)
                {
                    t0 = GC.GetAllocatedBytesForCurrentThread();
                }

                if (RingTestUtil.ReaderBitSet(buffer, reader.Slot))
                {
                    RingTestUtil.WriteSequence(buffer, 4, 4);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }

            return t0 < 0 ? -1 : GC.GetAllocatedBytesForCurrentThread() - t0;
        });

        long suspended = 0;
        for (int i = 0; i < 200; i++)                                   // warm-up: waiter thread, this method's state machine box, tiering
        {
            ValueTask<bool> vt = reader.Wait(4);
            if (!vt.IsCompleted)
            {
                suspended++;
            }

            Assert.True(await vt);
            reader.Advance(4);
        }

        // exact per-thread measures: the caller thread's arm path, the waiter thread (internal counter) and the writer thread.
        // (A process-wide measure is not meaningful here: the continuation hops through the thread pool, whose dispatch and the
        // test host allocate on their own; the library's threads allocate nothing.)
        long armAllocated = 0;
        long waiterBefore = reader.Counters.WaiterAllocatedBytes;
        Volatile.Write(ref measuring, 1);
        for (int i = 0; i < 2000; i++)
        {
            long t0 = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<bool> vt = reader.Wait(4);
            armAllocated += GC.GetAllocatedBytesForCurrentThread() - t0;
            if (!vt.IsCompleted)
            {
                suspended++;
            }

            Assert.True(await vt);
            reader.Advance(4);
        }

        long waiterAllocated = reader.Counters.WaiterAllocatedBytes - waiterBefore;
        stop.Cancel();
        long writerAllocated = await producer;
        Assert.True(suspended > 1000, $"only {suspended} waits actually suspended");
        Assert.Equal(0, armAllocated);
        Assert.True(waiterAllocated == 0, $"waiter thread allocated {waiterAllocated} bytes over 2000 waits ({suspended} suspended)");
        Assert.True(writerAllocated == 0, $"writer thread allocated {writerAllocated} bytes");
    }

    [Fact]
    public void ZeroAlloc_SyncKernelWait_SteadyState()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.Zero });
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

        for (int i = 0; i < 200; i++)
        {
            Assert.True(reader.WaitSync(4));
            reader.Advance(4);
        }

        long kernelBefore = reader.Counters.KernelWaits;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            Assert.True(reader.WaitSync(4));
            reader.Advance(4);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        long kernelWaits = reader.Counters.KernelWaits - kernelBefore;
        stop.Cancel();
        producer.Wait(RingTestUtil.Short);
        Assert.True(kernelWaits > 1000, $"only {kernelWaits} kernel waits");
        Assert.Equal(0, delta);
    }
}
