using System.Diagnostics;

namespace Photone.Ipc.Tests;

public sealed class FuzzTests
{
    private const long StressElements = 10_000_000;

    /// <summary>1 writer, 3 readers (sync / async / spin), random bucket and chunk sizes, 10 M sequence-numbered elements, every element verified.</summary>
    [Fact]
    public async Task Stress_1Writer_3Readers_10M()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 16);
        int c = (int)buffer.Capacity;
        using RingReader<long> sync = buffer.CreateReader();
        using RingReader<long> async = buffer.CreateReader();
        using RingReader<long> spin = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.FromMilliseconds(5) });

        Task<long> syncTask = Task.Run(() => ConsumeSync(sync, new Random(11), c / 3));
        Task<long> asyncTask = ConsumeAsync(async, new Random(22), c / 5);
        Task<long> spinTask = Task.Run(() => ConsumeSync(spin, new Random(33), c / 2));

        var rng = new Random(44);
        long written = 0;
        while (written < StressElements)
        {
            int n = (int)Math.Min(StressElements - written, rng.Next(1, c / 2 + 1));
            using Bucket<long> bucket = buffer.GetBucket(n);
            Span<long> span = bucket.Span;
            for (int j = 0; j < span.Length; j++)
            {
                span[j] = bucket.Cursor + j;
            }

            int k = rng.Next(4) == 0 ? rng.Next(0, n + 1) : n;     // sometimes commit less
            bucket.Commit(k);
            written += k;
        }

        Assert.Equal(StressElements, buffer.WriteCursor);
        Assert.Equal(0, buffer.EvictedReaders);
        buffer.Dispose();                                          // close: readers drain, then Wait returns false (the buffer's properties now throw)
        long[] results = await Task.WhenAll(syncTask, asyncTask, spinTask).WaitAsync(TimeSpan.FromMinutes(5));
        Assert.All(results, r => Assert.Equal(StressElements, r));
        Assert.Throws<ObjectDisposedException>(() => buffer.WriteCursor);
        Assert.All(new[] { sync, async, spin }, r => Assert.Equal(ReaderStatus.WriterClosed, r.Status));
        Assert.All(new[] { sync, async, spin }, r => Assert.True(r.IsCompleted));

        static long ConsumeSync(RingReader<long> reader, Random rng, int maxChunk)
        {
            long total = 0;
            while (true)
            {
                int n = rng.Next(1, maxChunk + 1);
                if (!reader.WaitSync(n))
                {
                    n = (int)reader.Available;                     // writer closed: drain the tail
                    if (n == 0)
                    {
                        return total;
                    }
                }

                Assert.True(reader.TryRead(n, out Chunk<long> chunk));
                Assert.Equal(reader.ReadCursor, chunk.Cursor);
                RingTestUtil.VerifyChunk(chunk);
                int adv = rng.Next(3) == 0 ? rng.Next(1, n + 1) : n;   // sometimes advance less than read
                reader.Advance(adv);
                total += adv;
                Assert.Equal(total, reader.ReadCursor);
            }
        }

        static async Task<long> ConsumeAsync(RingReader<long> reader, Random rng, int maxChunk)
        {
            long total = 0;
            while (true)
            {
                int n = rng.Next(1, maxChunk + 1);
                if (!await reader.Wait(n))
                {
                    n = (int)reader.Available;
                    if (n == 0)
                    {
                        return total;
                    }
                }

                {
                    Assert.True(reader.TryRead(n, out Chunk<long> chunk));
                    RingTestUtil.VerifyChunk(chunk);
                    int adv = rng.Next(3) == 0 ? rng.Next(1, n + 1) : n;
                    reader.Advance(adv);
                    total += adv;
                }
            }
        }
    }

    /// <summary>Readers join and leave at a high rate on 4 threads while the writer streams; every element read is verified (gate for DESIGN §5.6).</summary>
    [Fact]
    public void JoinStorm()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 14, options: new RingBufferOptions { LivenessCheckInterval = TimeSpan.FromMilliseconds(2) });
        int done = 0;
        long joins = 0;
        long verified = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var started = new CountdownEvent(4);

        Task[] stormers = new Task[4];
        for (int t = 0; t < stormers.Length; t++)
        {
            int seed = t;
            stormers[t] = Task.Run(() =>
            {
                var rng = new Random(seed);
                started.Signal();
                try
                {
                    while (Volatile.Read(ref done) == 0)
                    {
                        using RingReader<long> r = buffer.CreateReader();
                        Interlocked.Increment(ref joins);
                        int n = rng.Next(1, 256);
                        if (r.WaitSync(n, TimeSpan.FromMilliseconds(rng.Next(0, 3))))
                        {
                            Assert.True(r.TryRead(n, out Chunk<long> chunk));
                            RingTestUtil.VerifyChunk(chunk);
                            r.Advance(rng.Next(0, n + 1));
                            Interlocked.Add(ref verified, n);
                        }
                        else if (r.Status != ReaderStatus.Active)
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });
        }

        Assert.True(started.Wait(RingTestUtil.Short), "storm tasks did not start");
        var sw = Stopwatch.StartNew();
        var rng = new Random(99);
        long total = 0;
        while (sw.ElapsedMilliseconds < 1500 || total < 3_000_000)          // stream for at least 1.5 s and at least 3 M elements
        {
            total += RingTestUtil.WriteSequence(buffer, 100_000, 2000, rng);
        }

        Volatile.Write(ref done, 1);
        Task.WaitAll(stormers, TimeSpan.FromMinutes(2));
        Assert.Empty(errors);
        Assert.Equal(total, buffer.WriteCursor);
        Assert.True(Volatile.Read(ref joins) > 100, $"only {joins} joins in {sw.ElapsedMilliseconds} ms");
        Assert.True(Volatile.Read(ref verified) > 0);
        Assert.Equal(0, buffer.ActiveReaderCount);
        Assert.Equal(0, buffer.EvictedReaders);
    }
}
