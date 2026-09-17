using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

/// <summary>
/// <see cref="RingBufferPool"/> in one process (DESIGN §15): reuse, the "nobody else holds it" rule, aliases, parked opener mappings, expiry and bounds.
/// All pool tests live in this class (run sequentially) because some of them set process-wide test hooks.
/// </summary>
[Collection("ipc")]   // asserts that released address ranges are free: a test that maps memory in parallel (tag views, other buffers) can take them
public sealed unsafe class PoolTests
{
    private static readonly RingBufferPoolOptions s_keepForever = new() { IdleTimeout = Timeout.InfiniteTimeSpan };

    private static RingBufferOptions With(RingBufferPool pool) => new() { Pool = pool };

    // ------------------------------------------------------------------ creator reuse

    [Fact]
    public void Create_ReusesReturnedSection_SameAddressNewInstance()
    {
        using var pool = new RingBufferPool(s_keepForever);
        ulong baseAddress;
        ulong firstInstance;
        ulong sectionId;
        using (RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, options: With(pool)))
        {
            baseAddress = first.BaseAddress;
            firstInstance = first.InstanceId;
            sectionId = first.Pooled!.SectionId;
            using RingReader<long> reader = first.CreateReader();
            RingTestUtil.WriteSequence(first, 3000, 700);
            RingTestUtil.ReadSequence(reader, 3000, 500);
        }

        Assert.Equal(1, pool.IdleCount);
        Assert.Equal(1L << 16, pool.IdleBytes);

        using RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: With(pool));
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(1, pool.ReusedCount);
        Assert.Equal(1, pool.MissCount);
        Assert.Equal(baseAddress, second.BaseAddress);
        Assert.Equal(sectionId, second.Pooled!.SectionId);
        Assert.NotEqual(firstInstance, second.InstanceId);
        Assert.Equal(0L, second.WriteCursor);
        Assert.False(second.IsWriterClosed);
        Assert.Equal(0, second.ActiveReaderCount);

        // a fresh stream through the reused section, in-process and through an opener by name
        using RingBuffer<long> opened = RingBuffer<long>.Open(second.Name!);
        Assert.Equal(second.InstanceId, opened.InstanceId);
        using RingReader<long> r2 = opened.CreateReader();
        RingTestUtil.WriteSequence(second, 5000, 900);
        RingTestUtil.ReadSequence(r2, 5000, 600);
    }

    [Fact]
    public void Create_ReuseAcrossElementTypes_WithTheSameDataBytes()
    {
        using var pool = new RingBufferPool(s_keepForever);
        ulong baseAddress;
        using (RingBuffer<float> floats = RingBuffer<float>.Create(1 << 14, options: With(pool)))
        {
            baseAddress = floats.BaseAddress;
            Assert.Equal(1L << 16, floats.DataBytes);
        }

        string name = TestNames.Unique();
        using RingBuffer<int> ints = RingBuffer<int>.Create(1 << 14, name, With(pool));
        Assert.Equal(baseAddress, ints.BaseAddress);
        Assert.Equal(1L << 14, ints.Capacity);
        Assert.Equal(sizeof(int), ints.ElementSize);
        Assert.Throws<RingBufferLayoutException>(() => RingBuffer<float>.Open(name));    // the header describes the new buffer, not the old one
        using RingBuffer<int> opened = RingBuffer<int>.Open(name);
        Assert.Equal(ints.InstanceId, opened.InstanceId);
    }

    [Fact]
    public void Create_DifferentSize_MapsNewSection()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long>.Create(1 << 12, options: With(pool)).Dispose();
        using RingBuffer<long> larger = RingBuffer<long>.Create(1 << 14, options: With(pool));
        Assert.Equal(2, pool.MissCount);
        Assert.Equal(0, pool.ReusedCount);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Create_NotReusedWhileAnotherHandleIsOpen()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, TestNames.Unique(), With(pool));
        RingBuffer<long> holder = RingBuffer<long>.Open(first.Name!);                     // an opener without a pool holds the section open
        ulong firstBase = first.BaseAddress;
        first.Dispose();
        Assert.Equal(1, pool.IdleCount);

        using (RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: With(pool)))
        {
            Assert.NotEqual(firstBase, second.BaseAddress);
            Assert.Equal(1, pool.BusyCount);
            Assert.True(holder.IsWriterClosed);                                             // the old buffer is untouched: still closed, still readable
            Assert.Equal(first.InstanceId, holder.InstanceId);
        }

        holder.Dispose();
        using RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool));
        using RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: With(pool));
        Assert.Contains(firstBase, new[] { a.BaseAddress, b.BaseAddress });
        Assert.Equal(2, pool.ReusedCount);
    }

    [Fact]
    public void Create_ReturnedOnlyAfterTheLastReaderOfTheWriterIsDisposed()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingReader<long> reader = writer.CreateReader();
        RingTestUtil.WriteSequence(writer, 100, 100);
        writer.Dispose();
        Assert.Equal(0, pool.IdleCount);                                                   // the reader keeps the mapping
        RingTestUtil.ReadSequence(reader, 100, 100);
        Assert.False(reader.WaitSync(1));
        reader.Dispose();
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Create_DisposeWhileBlockedInGetBucket_NotReturnedUntilTheBlockedCallHasLeft()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name, With(pool));
        using RingBuffer<long> other = RingBuffer<long>.Open(name);                         // the stalling reader belongs to another object: no reference on `writer`
        using RingReader<long> reader = other.CreateReader();                               // never advances
        RingTestUtil.WriteSequence(writer, writer.Capacity, 1000);
        using var leaving = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        Exception? failure = null;
        var blocked = new Thread(() =>
        {
            try
            {
                using Bucket<long> b = writer.GetBucket(1);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        int blockedThread = blocked.ManagedThreadId;
        TestHooks.SlowGetBucketLeaving = () =>
        {
            if (Environment.CurrentManagedThreadId == blockedThread)
            {
                leaving.Set();
                proceed.Wait(RingTestUtil.Long);
            }
        };
        try
        {
            blocked.Start();
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(writer), "writer blocked");
            writer.Dispose();
            Assert.True(leaving.Wait(RingTestUtil.Short), "the blocked call did not leave");
            Assert.Equal(0, pool.IdleCount);                                                 // disposed, but the leaving call still holds the mapping
            proceed.Set();
            Assert.True(blocked.Join(RingTestUtil.Short));
            Assert.IsType<ObjectDisposedException>(failure);
            Assert.Equal(1, pool.IdleCount);                                                 // returned now (still busy: `other` holds the section)
        }
        finally
        {
            TestHooks.SlowGetBucketLeaving = null;
            proceed.Set();
        }
    }

    [Fact]
    public void Create_DisposeBetweenTheSpaceWaitAndTheReservation_GetBucketThrows_TheNextBufferReusesTheSection()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong firstBase = writer.BaseAddress;
        DisposeDuringGetBucket(writer, hook => TestHooks.AfterSpaceWait = hook, (bucketAddress, failure) =>
        {
            Assert.Equal(1, pool.IdleCount);                                                  // Dispose found no reservation: the section went back to the pool
            using RingBuffer<long> next = RingBuffer<long>.Create(1 << 12, options: With(pool));
            Assert.Equal(firstBase, next.BaseAddress);                                        // ... and the next buffer took it
            nint views = (nint)next.Data;
            Assert.False(
                bucketAddress >= views && bucketAddress < views + (nint)(2 * next.DataBytes),
                $"GetBucket returned a bucket at 0x{bucketAddress:X} inside the section the pool gave to the next buffer (data view at 0x{views:X}).");
            Assert.IsType<ObjectDisposedException>(failure);
        });
    }

    [Fact]
    public void Create_DisposeAfterTheReservationWasPublished_ReleasesTheSectionInsteadOfPoolingIt_GetBucketThrows()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong firstBase = writer.BaseAddress;
        DisposeDuringGetBucket(writer, hook => TestHooks.AfterReservationPublished = hook, (bucketAddress, failure) =>
        {
            Assert.IsType<ObjectDisposedException>(failure);                                  // the call still saw the disposal: no bucket
            Assert.Equal(nint.Zero, bucketAddress);
            Assert.Equal(0, pool.IdleCount);                                                  // Dispose found the reservation: released, never pooled
            Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(firstBase, out _));
            RingBuffer<long>.Create(1 << 12, options: With(pool)).Dispose();
            Assert.Equal(0, pool.ReusedCount);
        });
    }

    /// <summary>
    /// Fills the ring behind a reader, blocks <c>GetBucket(1)</c> on another thread and ends its wait by disposing the reader. The hook that
    /// <paramref name="setHook"/> installs disposes <paramref name="writer"/> on a third thread and waits for that to complete. <paramref name="whileHeld"/>
    /// gets the address of the bucket <c>GetBucket</c> returned (0 if it threw) and the exception, while the bucket is still held. The bucket is never
    /// stored through, committed or disposed: its memory may belong to another buffer by then.
    /// </summary>
    private static void DisposeDuringGetBucket(RingBuffer<long> writer, Action<Action<object>?> setHook, Action<nint, Exception?> whileHeld)
    {
        RingReader<long> reader = writer.CreateReader();                                    // never advances: the ring fills up
        RingTestUtil.WriteSequence(writer, writer.Capacity, 1000);
        using var returned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        nint bucketAddress = 0;
        Exception? failure = null;
        bool disposed = false;
        var blocked = new Thread(() =>
        {
            try
            {
                Bucket<long> bucket = writer.GetBucket(1);
                fixed (long* p = bucket.Span)
                {
                    bucketAddress = (nint)p;
                }

                returned.Set();
                release.Wait(RingTestUtil.Long);
            }
            catch (Exception ex)
            {
                failure = ex;
                returned.Set();
            }
        })
        { IsBackground = true };
        setHook(buffer =>
        {
            if (buffer == writer)                                                             // buffers of tests running in parallel pass through
            {
                var disposer = new Thread(writer.Dispose) { IsBackground = true };
                disposer.Start();
                disposed = disposer.Join(RingTestUtil.Short);
            }
        });
        try
        {
            blocked.Start();
            RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(writer), "writer blocked");
            reader.Dispose();                                                                 // frees the ring and wakes the writer: its wait succeeds
            Assert.True(returned.Wait(RingTestUtil.Short), "GetBucket did not return");
            Assert.True(disposed, "Dispose on another thread did not complete");
            whileHeld(bucketAddress, failure);
        }
        finally
        {
            setHook(null);
            release.Set();
            blocked.Join(RingTestUtil.Short);
        }
    }

    [Fact]
    public void CreateReader_RacingDispose_ReturnsTheMappingOnce()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        int claimingThread = Environment.CurrentManagedThreadId;
        TestHooks.AfterClaim = () =>
        {
            if (Environment.CurrentManagedThreadId == claimingThread)
            {
                TestHooks.AfterClaim = null;
                writer.Dispose();                                                            // stands in for another thread disposing in the middle of the claim
            }
        };
        RingReader<long> reader;
        try
        {
            reader = writer.CreateReader();
        }
        finally
        {
            TestHooks.AfterClaim = null;
        }

        Assert.Equal(0, pool.IdleCount);                                                     // the new reader keeps the mapping
        Assert.False(reader.WaitSync(1, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        reader.Dispose();
        Assert.Equal(1, pool.IdleCount);
        Assert.Equal(1L << 16, pool.IdleBytes);                                             // returned once, not twice

        using RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool));
        using RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: With(pool));
        Assert.NotEqual(a.BaseAddress, b.BaseAddress);
        Assert.Equal(1, pool.ReusedCount);
    }

    [Fact]
    public void Dispose_WithABucketOutstanding_ReleasesTheMappingInsteadOfPoolingIt()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong baseAddress = writer.BaseAddress;
        Bucket<long> bucket = writer.GetBucket(16);                                          // e.g. still being filled on another thread
        writer.Dispose();
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(baseAddress, out _));       // a late store into the span faults instead of landing in the next buffer
        bucket.Dispose();                                                                    // Commit(0) on a closed writer touches nothing

        RingBuffer<long>.Create(1 << 12, options: With(pool)).Dispose();                    // an ordinary release still pools
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void DuplicatedHandle_KeepsTheSectionFromBeingReused()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong firstBase = first.BaseAddress;
        var dup = new SafeSectionHandle(first.DuplicateSectionHandleTo(Environment.ProcessId), ownsHandle: true);
        first.Dispose();
        using (RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: With(pool)))
        {
            Assert.NotEqual(firstBase, second.BaseAddress);
        }

        using (RingBuffer<long> byHandle = RingBuffer<long>.Open(dup))                      // the handle still reaches the closed first buffer
        {
            Assert.Equal(first.InstanceId, byHandle.InstanceId);
            Assert.True(byHandle.IsWriterClosed);
            Assert.Null(byHandle.Name);
        }

        using RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool));
        using RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: With(pool));
        Assert.Contains(firstBase, new[] { a.BaseAddress, b.BaseAddress });
    }

    // ------------------------------------------------------------------ names

    [Fact]
    public void Name_BelongsToTheBuffer_NotToTheSection()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        ulong firstInstance;
        using (RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, With(pool)))
        {
            Assert.Equal("Local\\photone." + name, first.Name);
            Assert.Throws<RingBufferAlreadyExistsException>(() => RingBuffer<long>.Create(1 << 12, name, With(pool)));
            using RingBuffer<long> opened = RingBuffer<long>.Open(name);
            Assert.Equal(first.InstanceId, opened.InstanceId);
            Assert.Equal(first.Name, opened.Name);
            firstInstance = first.InstanceId;
        }

        Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(name));        // gone with the buffer, although the section lives on in the pool
        Assert.Equal(1, pool.IdleCount);
        Assert.Equal(1, pool.MissCount);                                                    // the AlreadyExists attempt never reached the pool

        using RingBuffer<long> again = RingBuffer<long>.Create(1 << 12, name, With(pool));
        using RingBuffer<long> reopened = RingBuffer<long>.Open(name);
        Assert.Equal(again.InstanceId, reopened.InstanceId);
        Assert.NotEqual(firstInstance, again.InstanceId);
        Assert.Equal(1, pool.ReusedCount);
    }

    [Fact]
    public void Open_AliasResolvedBeforeTheSectionWasReused_ThrowsNotFound()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, With(pool));
        ulong firstBase = first.BaseAddress;
        RingBuffer<long>? successor = null;
        TestHooks.AfterAliasResolved = () =>
        {
            TestHooks.AfterAliasResolved = null;
            first.Dispose();                                                                 // the opener holds only the alias so far
            successor = RingBuffer<long>.Create(1 << 12, options: With(pool));
        };
        try
        {
            RingBufferNotFoundException ex = Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(name));
            Assert.Contains("no longer exists", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(successor);
            Assert.Equal(firstBase, successor.BaseAddress);                                 // the section really was reused under the opener
            Assert.False(successor.IsWriterClosed);
        }
        finally
        {
            TestHooks.AfterAliasResolved = null;
            successor?.Dispose();
        }
    }

    [Fact]
    public void Open_AliasResolvedWhileTheSectionIsStillIdle_SeesTheClosedBuffer()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, With(pool));
        RingTestUtil.WriteSequence(first, 10, 10);
        TestHooks.AfterAliasResolved = () =>
        {
            TestHooks.AfterAliasResolved = null;
            first.Dispose();
        };
        try
        {
            using RingBuffer<long> late = RingBuffer<long>.Open(name);
            Assert.Equal(first.InstanceId, late.InstanceId);
            Assert.True(late.IsWriterClosed);
            using (RingBuffer<long> other = RingBuffer<long>.Create(1 << 12, options: With(pool)))
            {
                Assert.NotEqual(first.BaseAddress, other.BaseAddress);                     // the late opener holds the section: not reused under it
            }
        }
        finally
        {
            TestHooks.AfterAliasResolved = null;
        }
    }

    [Fact]
    public void Open_StaleAlias_GivesUpAsSoonAsTheSectionShowsAnotherInstance()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, With(pool));
        ulong firstBase = first.BaseAddress;
        using var resolved = new ManualResetEventSlim();
        using var reinitialising = new ManualResetEventSlim();
        using var openerDone = new ManualResetEventSlim();
        Exception? openerFailure = null;
        bool openerFinishedDuringReinit = false;
        var opener = new Thread(() =>
        {
            try
            {
                RingBuffer<long>.Open(name, new RingBufferOptions { InitializationTimeout = TimeSpan.FromSeconds(30) }).Dispose();
            }
            catch (Exception ex)
            {
                openerFailure = ex;
            }

            openerDone.Set();
        });
        int openerThread = opener.ManagedThreadId;
        int creatorThread = Environment.CurrentManagedThreadId;
        TestHooks.AfterAliasResolved = () =>
        {
            if (Environment.CurrentManagedThreadId == openerThread)
            {
                resolved.Set();
                reinitialising.Wait(RingTestUtil.Long);                                       // holds only the alias while the creator reuses the section
            }
        };
        TestHooks.BeforeInitState = () =>
        {
            if (Environment.CurrentManagedThreadId == creatorThread)
            {
                reinitialising.Set();                                                        // InitState stays 0 while this runs: a long re-initialisation
                openerFinishedDuringReinit = openerDone.Wait(RingTestUtil.Short);
            }
        };
        try
        {
            opener.Start();
            Assert.True(resolved.Wait(RingTestUtil.Short));
            first.Dispose();
            using RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: With(pool));
            Assert.Equal(firstBase, second.BaseAddress);
            Assert.True(opener.Join(RingTestUtil.Short));
            Assert.True(openerFinishedDuringReinit, "the stale opener waited for the re-initialisation instead of giving up");
            Assert.IsType<RingBufferNotFoundException>(openerFailure);
        }
        finally
        {
            TestHooks.AfterAliasResolved = null;
            TestHooks.BeforeInitState = null;
            reinitialising.Set();
        }
    }

    [Fact]
    public void Open_PoolSectionByItsKernelName_ThrowsNotFound()
    {
        using var pool = new RingBufferPool(s_keepForever);
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        string sectionName = buffer.Pooled!.SectionName!;
        Assert.StartsWith("Local\\photone.pool.", sectionName, StringComparison.Ordinal);
        RingBufferNotFoundException ex = Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(sectionName));
        Assert.Contains("RingBufferPool", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ opener parking

    [Fact]
    public void Opener_ParksItsMapping_AndAdoptsItForTheNextBufferInTheSameSection()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name1 = TestNames.Unique();
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name1, With(pool));
        ulong openerBase;
        using (RingBuffer<long> opened = RingBuffer<long>.Open(name1, With(pool)))
        {
            openerBase = opened.BaseAddress;
            Assert.NotEqual(first.BaseAddress, openerBase);
            using RingReader<long> reader = opened.CreateReader();
            RingTestUtil.WriteSequence(first, 2000, 300);
            RingTestUtil.ReadSequence(reader, 2000, 250);
        }

        Assert.Equal(1, pool.IdleCount);                                                   // parked, without the section handle
        first.Dispose();
        Assert.Equal(2, pool.IdleCount);

        string name2 = TestNames.Unique();
        using RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, name2, With(pool));
        Assert.Equal(1, pool.ReusedCount);                                                 // the parked opener did not count as a holder
        using RingBuffer<long> reopened = RingBuffer<long>.Open(name2, With(pool));
        Assert.Equal(1, pool.RevivedCount);
        Assert.Equal(openerBase, reopened.BaseAddress);
        Assert.Equal(second.InstanceId, reopened.InstanceId);
        Assert.Equal("Local\\photone." + name2, reopened.Name);
        Assert.Equal(0, pool.IdleCount);

        using RingReader<long> r2 = reopened.CreateReader();
        Task writer = Task.Run(() => RingTestUtil.WriteSequence(second, 50_000, 1000));      // wraps the ring several times: signaling works through the kept events
        RingTestUtil.ReadSequence(r2, 50_000, 700, new Random(3));
        Assert.True(writer.Wait(RingTestUtil.Long));
    }

    [Fact]
    public void Opener_EachParkedMappingIsAdoptedOnce_ThenNewMappings()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name, With(pool));
        using (RingBuffer<long> first = RingBuffer<long>.Open(name, With(pool)))
        using (RingBuffer<long> second = RingBuffer<long>.Open(name, With(pool)))
        {
            Assert.NotEqual(first.BaseAddress, second.BaseAddress);
        }

        Assert.Equal(2, pool.IdleCount);
        using RingBuffer<long> a = RingBuffer<long>.Open(name, With(pool));
        using RingBuffer<long> b = RingBuffer<long>.Open(name, With(pool));
        using RingBuffer<long> c = RingBuffer<long>.Open(name, With(pool));                // nothing parked any more: a new mapping
        Assert.Equal(2, pool.RevivedCount);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public void Opener_StaleAliasWithAParkedMapping_KeepsTheMappingAndThrowsNotFound()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, With(pool));
        RingBuffer<long>.Open(name, With(pool)).Dispose();                                // parks a mapping of the section
        RingBuffer<long>? successor = null;
        TestHooks.AfterAliasResolved = () =>
        {
            TestHooks.AfterAliasResolved = null;
            first.Dispose();
            successor = RingBuffer<long>.Create(1 << 12, options: With(pool));            // same section, next instance
        };
        try
        {
            Assert.Throws<RingBufferNotFoundException>(() => RingBuffer<long>.Open(name, With(pool)));
            Assert.Equal(0, pool.RevivedCount);
            Assert.Equal(1, pool.IdleCount);                                               // the parked mapping went back: it is still a good view of the section

            using RingBuffer<long> current = RingBuffer<long>.Open(successor!.Name!, With(pool));
            Assert.Equal(1, pool.RevivedCount);
            Assert.Equal(successor.InstanceId, current.InstanceId);
        }
        finally
        {
            TestHooks.AfterAliasResolved = null;
            successor?.Dispose();
        }
    }

    [Fact]
    public void Opener_WrongElementType_KeepsTheParkedMapping()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name, With(pool));
        RingBuffer<long>.Open(name, With(pool)).Dispose();
        Assert.Equal(1, pool.IdleCount);
        Assert.Throws<RingBufferLayoutException>(() => RingBuffer<double>.Open(name, With(pool)));
        Assert.Equal(1, pool.IdleCount);
        using RingBuffer<long> ok = RingBuffer<long>.Open(name, With(pool));
        Assert.Equal(1, pool.RevivedCount);
    }

    [Fact]
    public void Opener_ByDuplicatedHandle_ParksAndRevivesToo()
    {
        using var pool = new RingBufferPool(s_keepForever);
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong openerBase;
        using (RingBuffer<long> first = RingBuffer<long>.Open(new SafeSectionHandle(writer.DuplicateSectionHandleTo(Environment.ProcessId), true), With(pool)))
        {
            openerBase = first.BaseAddress;
        }

        using RingBuffer<long> second = RingBuffer<long>.Open(new SafeSectionHandle(writer.DuplicateSectionHandleTo(Environment.ProcessId), true), With(pool));
        Assert.Equal(openerBase, second.BaseAddress);
        Assert.Equal(1, pool.RevivedCount);
        using RingReader<long> reader = second.CreateReader();
        RingTestUtil.WriteSequence(writer, 1000, 100);
        RingTestUtil.ReadSequence(reader, 1000, 100);
    }

    [Fact]
    public void Opener_OfUnpooledBuffer_DoesNotPark()
    {
        using var pool = new RingBufferPool(s_keepForever);
        string name = TestNames.Unique();
        using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name);
        ulong openerBase;
        using (RingBuffer<long> opened = RingBuffer<long>.Open(name, With(pool)))
        {
            Assert.Null(opened.Pooled);
            openerBase = opened.BaseAddress;
        }

        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(openerBase, out _));
    }

    // ------------------------------------------------------------------ expiry and bounds

    [Fact]
    public void IdleTimeout_ReleasesIdleMappings()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) });
        string name = TestNames.Unique();
        RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name, With(pool));
        RingBuffer<long> opened = RingBuffer<long>.Open(name, With(pool));
        ulong writerBase = writer.BaseAddress;
        ulong openerBase = opened.BaseAddress;
        opened.Dispose();
        writer.Dispose();
        Assert.Equal(2, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_COMMIT, TestKernel.QueryState(writerBase, out _));

        RingTestUtil.WaitUntil(() => pool.IdleCount == 0, "idle mappings expired");
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(2, pool.ReleasedCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(writerBase, out _));
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(openerBase, out _));

        // the timer re-arms for later returns
        RingBuffer<long>.Create(1 << 12, options: With(pool)).Dispose();
        Assert.Equal(1, pool.IdleCount);
        RingTestUtil.WaitUntil(() => pool.IdleCount == 0, "second expiry");
    }

    [Fact]
    public void IdleTimeout_KeepsMappingsThatAreReusedInTime()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        ulong baseAddress = 0;
        for (int i = 0; i < 20; i++)
        {
            using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: With(pool));
            if (i == 0)
            {
                baseAddress = buffer.BaseAddress;
            }

            Assert.Equal(baseAddress, buffer.BaseAddress);
            Thread.Sleep(20);                                                                // well within the timeout each round
        }

        Assert.Equal(19, pool.ReusedCount);
        Assert.Equal(0, pool.ReleasedCount);
    }

    [Fact]
    public void IdleTimeout_Zero_KeepsNothing()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = TimeSpan.Zero });
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong baseAddress = buffer.BaseAddress;
        buffer.Dispose();
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(1, pool.ReleasedCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(baseAddress, out _));
    }

    [Fact]
    public void Trim_ReleasesEverythingIdle_AndPoolingContinues()
    {
        using var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingBuffer<int> b = RingBuffer<int>.Create(1 << 16, options: With(pool));
        ulong aBase = a.BaseAddress;
        a.Dispose();
        b.Dispose();
        Assert.Equal(2, pool.IdleCount);
        Assert.Equal((1L << 16) + (1L << 18), pool.IdleBytes);
        pool.Trim();
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(aBase, out _));
        RingBuffer<long>.Create(1 << 12, options: With(pool)).Dispose();
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void Dispose_ReleasesIdleMappings_AndLaterReturnsAreReleasedToo()
    {
        var pool = new RingBufferPool(s_keepForever);
        RingBuffer<long> idle = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingBuffer<long> live = RingBuffer<long>.Create(1 << 12, options: With(pool));
        ulong idleBase = idle.BaseAddress;
        ulong liveBase = live.BaseAddress;
        idle.Dispose();
        pool.Dispose();
        pool.Dispose();
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(idleBase, out _));

        using (RingReader<long> reader = live.CreateReader())                              // a buffer from a disposed pool keeps working
        {
            RingTestUtil.WriteSequence(live, 100, 10);
            RingTestUtil.ReadSequence(reader, 100, 10);
        }

        live.Dispose();
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(liveBase, out _));

        using RingBuffer<long> after = RingBuffer<long>.Create(1 << 12, options: With(pool));   // creating with a disposed pool just does not pool
        Assert.Equal(TestKernel.MEM_COMMIT, TestKernel.QueryState(after.BaseAddress, out _));
    }

    [Fact]
    public void MaxIdleBytes_ReleasesTheLongestIdleFirst()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan, MaxIdleBytes = 2L << 16 });
        RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingBuffer<long> c = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingBuffer<long> big = RingBuffer<long>.Create(1 << 15, options: With(pool));      // 256 KiB: larger than the bound on its own
        ulong aBase = a.BaseAddress;
        ulong bigBase = big.BaseAddress;
        a.Dispose();
        b.Dispose();
        c.Dispose();
        Assert.Equal(2, pool.IdleCount);
        Assert.Equal(2L << 16, pool.IdleBytes);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(aBase, out _));
        big.Dispose();
        Assert.Equal(2, pool.IdleCount);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(bigBase, out _));
        Assert.Equal(2, pool.ReleasedCount);
    }

    [Fact]
    public void Options_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = TimeSpan.FromSeconds(-2) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferPool(new RingBufferPoolOptions { MaxIdleBytes = -1 }));
        using var defaults = new RingBufferPool();
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.IdleTimeout);
        Assert.Equal(long.MaxValue, defaults.MaxIdleBytes);
        Assert.False(defaults.ClearOnReuse);
    }

    [Fact]
    public void Shared_IsOneInstance_AndDisposeOnlyTrims()
    {
        RingBufferPool shared = RingBufferPool.Shared;
        Assert.Same(shared, RingBufferPool.Shared);
        RingBuffer<long>.Create(1 << 12, options: With(shared)).Dispose();
        Assert.True(shared.IdleCount >= 1);
        shared.Dispose();
        Assert.Equal(0, shared.IdleCount);
        RingBuffer<long>.Create(1 << 12, options: With(shared)).Dispose();
        Assert.Equal(1, shared.IdleCount);
        shared.Trim();
    }

    // ------------------------------------------------------------------ data, address, finalization

    [Fact]
    public void ClearOnReuse_ZeroesTheData()
    {
        using var clearing = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan, ClearOnReuse = true });
        using var keeping = new RingBufferPool(s_keepForever);
        foreach (RingBufferPool pool in new[] { clearing, keeping })
        {
            using (RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, options: With(pool)))
            {
                using Bucket<long> bucket = first.GetBucket((int)first.Capacity);
                bucket.Span.Fill(-1);
                bucket.Commit((int)first.Capacity);
            }

            using RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: With(pool));
            Assert.Equal(1, pool.ReusedCount);
            using Bucket<long> view = second.GetBucket((int)second.Capacity);
            bool allZero = !view.Span.ContainsAnyExcept(0L);
            Assert.Equal(pool.ClearOnReuse, allZero);
        }
    }

    [Fact]
    public void PreferredBaseAddress_ReusesOnlyASectionAtThatAddress()
    {
        using var pool = new RingBufferPool(s_keepForever);
        ulong first;
        using (RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: With(pool)))
        {
            first = a.BaseAddress;
        }

        const ulong elsewhere = 0x4A00_5678_0000;                                            // in the quiet window, unused by other tests
        using (RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { Pool = pool, PreferredBaseAddress = elsewhere }))
        {
            Assert.Equal(elsewhere, b.BaseAddress);
            Assert.Equal(0, pool.ReusedCount);
        }

        using RingBuffer<long> c = RingBuffer<long>.Create(1 << 12, options: new RingBufferOptions { Pool = pool, PreferredBaseAddress = first });
        Assert.Equal(first, c.BaseAddress);
        Assert.Equal(1, pool.ReusedCount);
    }

    [Fact]
    public void Finalizer_ReleasesInsteadOfReturning()
    {
        using var pool = new RingBufferPool(s_keepForever);
        ulong baseAddress = CreateAndAbandon(pool);
        for (int i = 0; i < 3 && TestKernel.QueryState(baseAddress, out _) != TestKernel.MEM_FREE; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(baseAddress, out _));
        Assert.Equal(0, pool.IdleCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong CreateAndAbandon(RingBufferPool pool)
    {
        RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: With(pool));
        RingReader<long> reader = buffer.CreateReader();
        GC.KeepAlive(reader);
        return buffer.BaseAddress;
    }

    // ------------------------------------------------------------------ stress

    [Fact]
    public void Stress_ConcurrentCreateOpenReadDispose_NoCrossTalk()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) });
        const int Threads = 4;
        const int Iterations = 150;
        Exception? failure = null;
        var threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int seed = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    var rng = new Random(seed);
                    for (int i = 0; i < Iterations; i++)
                    {
                        string name = TestNames.Unique();
                        using RingBuffer<long> writer = RingBuffer<long>.Create(rng.Next(2) == 0 ? 1 << 13 : 1 << 14, name, With(pool));
                        using RingBuffer<long> opened = RingBuffer<long>.Open(name, With(pool));
                        using RingReader<long> reader = opened.CreateReader();
                        long tag = (long)(writer.InstanceId & 0x7FFF_FFFF) << 32;
                        long count = rng.Next(1, 20_000);
                        Task producer = Task.Run(() =>
                        {
                            long written = 0;
                            while (written < count)
                            {
                                int n = (int)Math.Min(count - written, 1 + (written % 777));
                                using Bucket<long> b = writer.GetBucket(n);
                                for (int j = 0; j < n; j++)
                                {
                                    b.Span[j] = tag | (b.Cursor + j);
                                }

                                b.Commit(n);
                                written += n;
                            }
                        });
                        long read = 0;
                        while (read < count)
                        {
                            int n = (int)Math.Min(count - read, 500);
                            Assert.True(reader.WaitSync(n, RingTestUtil.Long));
                            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
                            ReadOnlySpan<long> span = chunk.Data.Span;
                            for (int j = 0; j < n; j++)
                            {
                                if (span[j] != (tag | (chunk.Cursor + j)))
                                {
                                    throw new InvalidOperationException($"cross-talk: got 0x{span[j]:X} at cursor {chunk.Cursor + j}, tag 0x{tag:X}");
                                }
                            }

                            reader.Advance(n);
                            read += n;
                        }

                        Assert.True(producer.Wait(RingTestUtil.Long));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            });
            threads[t].Start();
        }

        foreach (Thread th in threads)
        {
            Assert.True(th.Join(TimeSpan.FromMinutes(2)));
        }

        Assert.Null(failure);
        Assert.True(pool.ReusedCount > 0, "expected reuse");
        Assert.True(pool.RevivedCount > 0, "expected revived opener mappings");
        pool.Trim();
    }
}
