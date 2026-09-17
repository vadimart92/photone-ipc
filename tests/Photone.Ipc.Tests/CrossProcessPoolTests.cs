using System.Globalization;
using Xunit.Sdk;

namespace Photone.Ipc.Tests;

/// <summary>
/// <see cref="RingBufferPool"/> with real child processes (DESIGN §15): a process that holds a section keeps it from being reused, a process that exited or
/// crashed does not, and an opener in another process parks its mapping and adopts it for the next buffer in the same section.
/// </summary>
[Collection("ipc")]
public sealed class CrossProcessPoolTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static RingBufferOptions KeepForever(out RingBufferPool pool)
    {
        pool = new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = Timeout.InfiniteTimeSpan });
        return new RingBufferOptions { Pool = pool };
    }

    private static string Expect(ChildProcess child, string prefix)
    {
        string line = child.ReadLine(s_timeout);
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new XunitException($"child printed '{line}', expected a line starting with '{prefix}'");
        }

        return line;
    }

    private static string Field(string line, string key)
    {
        foreach (string token in line.Split(' '))
        {
            if (token.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return token[(key.Length + 1)..];
            }
        }

        throw new XunitException($"no '{key}=' in '{line}'");
    }

    [Fact]
    public void CrossProcess_Pool_ChildReadsByName_ThenTheSectionIsReused()
    {
        RingBufferOptions options = KeepForever(out RingBufferPool pool);
        using (pool)
        {
            string name = TestNames.Unique();
            ulong firstBase;
            using (RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 16, name, options))
            {
                firstBase = buffer.BaseAddress;
                using ChildProcess child = ChildProcess.Start("reader", name, "200000");
                string ready = Expect(child, "ready ");
                Assert.Contains("name=Local\\photone." + name, ready, StringComparison.Ordinal);
                RingTestUtil.WriteSequence(buffer, 200_000, 4096, new Random(11));
                Assert.StartsWith("done ok", Expect(child, "done "), StringComparison.Ordinal);
                Assert.Equal(0, child.WaitForExit(s_timeout));
            }

            using RingBuffer<long> next = RingBuffer<long>.Create(1 << 16, TestNames.Unique(), options);
            Assert.Equal(firstBase, next.BaseAddress);
            Assert.Equal(1, pool.ReusedCount);
        }
    }

    [Fact]
    public void CrossProcess_Pool_ChildHoldingTheBuffer_BlocksReuse_UntilItIsKilled()
    {
        RingBufferOptions options = KeepForever(out RingBufferPool pool);
        using (pool)
        {
            string name = TestNames.Unique();
            RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, options);
            ulong firstBase = first.BaseAddress;
            using ChildProcess child = ChildProcess.Start("hold-name", name);
            Expect(child, "holding");
            first.Dispose();

            ulong secondBase;
            using (RingBuffer<long> second = RingBuffer<long>.Create(1 << 12, options: options))
            {
                secondBase = second.BaseAddress;
                Assert.NotEqual(firstBase, secondBase);
                Assert.Equal(1, pool.BusyCount);
            }

            child.Kill();
            child.WaitForExit(s_timeout);                                                    // a terminated process's handles are closed by the time it is signaled
            using RingBuffer<long> a = RingBuffer<long>.Create(1 << 12, options: options);
            using RingBuffer<long> b = RingBuffer<long>.Create(1 << 12, options: options);
            Assert.Equal(new[] { firstBase, secondBase }.Order(), new[] { a.BaseAddress, b.BaseAddress }.Order());
            Assert.Equal(2, pool.ReusedCount);
        }
    }

    [Fact]
    public void CrossProcess_Pool_ChildDrainsTheClosedBuffer_WhileTheCreatorMakesNewOnes()
    {
        RingBufferOptions options = KeepForever(out RingBufferPool pool);
        using (pool)
        {
            string name = TestNames.Unique();
            RingBuffer<long> first = RingBuffer<long>.Create(1 << 12, name, options);
            using ChildProcess child = ChildProcess.Start("step-reader", name);
            Expect(child, "ready ");
            RingTestUtil.WriteSequence(first, 8000, 1000);
            first.Dispose();                                                                 // closed with 8000 elements the child has not read yet

            for (int i = 0; i < 20; i++)
            {
                using RingBuffer<long> other = RingBuffer<long>.Create(1 << 12, options: options);
                Assert.NotEqual(first.BaseAddress, other.BaseAddress);
                RingTestUtil.WriteSequence(other, 8000, 1000);                               // would overwrite the child's data if its section were reused
            }

            child.WriteLine("advance 8000");
            Assert.Equal("advanced cursor=8000", Expect(child, "advanced "));
            child.WriteLine("exit");
            Expect(child, "exiting");
            Assert.Equal(0, child.WaitForExit(s_timeout));
            Assert.Equal(19, pool.ReusedCount);
        }
    }

    [Fact]
    public void CrossProcess_Pool_ChildParksItsMapping_AndAdoptsItForTheNextBuffer()
    {
        RingBufferOptions options = KeepForever(out RingBufferPool pool);
        using (pool)
        {
            using ChildProcess child = ChildProcess.Start("pool-reader-loop");
            Expect(child, "looping");

            string name1 = TestNames.Unique();
            ulong writerBase;
            string childBase;
            using (RingBuffer<long> first = RingBuffer<long>.Create(1 << 16, name1, options))
            {
                writerBase = first.BaseAddress;
                child.WriteLine("open " + name1 + " 100000");
                string ready = Expect(child, "ready ");
                Assert.Contains("revived=0", ready, StringComparison.Ordinal);
                childBase = Field(ready, "base");
                RingTestUtil.WriteSequence(first, 100_000, 4096);
                Assert.Equal("done ok idle=1", Expect(child, "done "));                     // parked, with its section handle closed
            }

            string name2 = TestNames.Unique();
            using RingBuffer<long> second = RingBuffer<long>.Create(1 << 16, name2, options);
            Assert.Equal(writerBase, second.BaseAddress);                                   // reused although the child still maps the section
            Assert.Equal(1, pool.ReusedCount);

            child.WriteLine("open " + name2 + " 300000");
            string ready2 = Expect(child, "ready ");
            output.WriteLine(ready2);
            Assert.Contains("revived=1", ready2, StringComparison.Ordinal);
            Assert.Equal(childBase, Field(ready2, "base"));
            RingTestUtil.WriteSequence(second, 300_000, 4096, new Random(12));               // wraps the ring several times: the kept events still wake the child
            Assert.Equal("done ok idle=1", Expect(child, "done "));

            child.WriteLine("exit");
            Expect(child, "exiting");
            Assert.Equal(0, child.WaitForExit(s_timeout));
        }
    }

    [Fact]
    public void CrossProcess_Pool_DuplicatedHandle_ChildReads_ThenTheSectionIsReused()
    {
        RingBufferOptions options = KeepForever(out RingBufferPool pool);
        using (pool)
        {
            ulong firstBase;
            using (RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12, options: options))
            {
                firstBase = buffer.BaseAddress;
                using ChildProcess child = ChildProcess.Start("reader", "--stdin-handle", "5000");
                nint dup = buffer.DuplicateSectionHandleTo(child.Id);
                child.WriteLine("handle " + dup.ToString("X", CultureInfo.InvariantCulture));
                Assert.Contains("name=null", Expect(child, "ready "), StringComparison.Ordinal);
                RingTestUtil.WriteSequence(buffer, 5000, 500);
                Assert.StartsWith("done ok", Expect(child, "done "), StringComparison.Ordinal);
                Assert.Equal(0, child.WaitForExit(s_timeout));
            }

            using RingBuffer<long> next = RingBuffer<long>.Create(1 << 12, options: options);
            Assert.Equal(firstBase, next.BaseAddress);
        }
    }
}
