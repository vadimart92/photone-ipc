using System.Diagnostics;

namespace Photone.Ipc.Tests;

/// <summary>Everything a <see cref="RingBufferPool"/> keeps is given back: handles, address space.</summary>
[Collection("ipc")]   // process-wide measurements (handle count, virtual size): must not share the process with parallel tests
public sealed class ProcessCounterTests
{
    [Fact]
    public void Pool_CreateOpenParkReuseTrim_LeavesNoHandlesOrAddressSpaceBehind()
    {
        using Process self = Process.GetCurrentProcess();
        Cycle(10);                                                                          // warm up (JIT, first-time allocations, thread-pool timer thread)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        self.Refresh();
        int handlesBefore = self.HandleCount;
        long vmBefore = self.VirtualMemorySize64;

        (long reused, long revived) = Cycle(300);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        self.Refresh();
        int handleGrowth = self.HandleCount - handlesBefore;
        long vmGrowth = self.VirtualMemorySize64 - vmBefore;
        Assert.True(reused >= 290 && revived >= 290, $"reused {reused}, revived {revived}");
        Assert.True(handleGrowth < 50, $"handle count grew by {handleGrowth}");
        Assert.True(vmGrowth < 16L << 20, $"virtual size grew by {vmGrowth} bytes");
    }

    /// <summary>Creates, opens (by name, with the pool), streams through, and releases a buffer <paramref name="rounds"/> times; then disposes the pool.</summary>
    private static (long Reused, long Revived) Cycle(int rounds)
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan });
        var options = new RingBufferOptions { Pool = pool };
        for (int i = 0; i < rounds; i++)
        {
            string name = TestNames.Unique();
            using RingBuffer<long> writer = RingBuffer<long>.Create(1 << 12, name, options);
            using RingBuffer<long> opened = RingBuffer<long>.Open(name, options);
            using RingReader<long> reader = opened.CreateReader();
            RingTestUtil.WriteSequence(writer, 100, 50);
            RingTestUtil.ReadSequence(reader, 100, 50);
        }

        return (pool.ReusedCount, pool.RevivedCount);
    }
}
