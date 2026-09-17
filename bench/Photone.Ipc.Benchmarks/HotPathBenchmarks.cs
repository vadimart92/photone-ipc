using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// In-process write + read throughput (BenchmarkDotNet). One iteration = one bucket of <see cref="BucketElements"/> floats through the ring.
/// <list type="bullet">
/// <item><see cref="WriteRead_Protocol"/>: GetBucket / Commit / TryRead / Advance on one thread, payload untouched (pure protocol cost).</item>
/// <item><see cref="WriteRead_FillAndSum"/>: the writer fills the bucket, the reader sums it (payload moved through cache/memory).</item>
/// <item><see cref="WriteRead_FillAndSum_OddBucket"/>: same with <c>BucketElements + 1</c> elements, so buckets regularly wrap through the mirror.</item>
/// <item><see cref="Write_SpinningReaderThread"/>: the writer thread commits while a second thread drains (cross-core cursor traffic, writer blocks when the ring is full).
/// The drain thread exists only for this benchmark (per-benchmark setup), so it does not disturb the single-threaded rows.</item>
/// </list>
/// Bytes/op should be 0 for all four. Throughput = BucketElements * 4 bytes / (ns per op).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[GcServer(false)]
[GcConcurrent(false)]
public class HotPathBenchmarks
{
    private RingBuffer<float> _single = null!;
    private RingReader<float> _singleReader = null!;

    private RingBuffer<float> _threaded = null!;
    private Thread? _drain;
    private volatile bool _stop;

    /// <summary>Bucket size in elements of <see cref="float"/> (1 KiB, 16 KiB, 256 KiB).</summary>
    [Params(256, 4096, 65536)]
    public int BucketElements { get; set; }

    [GlobalSetup(Targets = new[] { nameof(WriteRead_Protocol), nameof(WriteRead_FillAndSum), nameof(WriteRead_FillAndSum_OddBucket) })]
    public void SetupSingle()
    {
        _single = RingBuffer<float>.Create(1 << 20);
        _singleReader = _single.CreateReader();
    }

    [GlobalCleanup(Targets = new[] { nameof(WriteRead_Protocol), nameof(WriteRead_FillAndSum), nameof(WriteRead_FillAndSum_OddBucket) })]
    public void CleanupSingle()
    {
        _singleReader.Dispose();
        _single.Dispose();
    }

    [GlobalSetup(Target = nameof(Write_SpinningReaderThread))]
    public void SetupThreaded()
    {
        _threaded = RingBuffer<float>.Create(1 << 22);
        _stop = false;
        var ready = new ManualResetEventSlim(false);
        int n = BucketElements;
        _drain = new Thread(() =>
        {
            using RingReader<float> reader = _threaded.CreateReader(new ReaderOptions { SpinTime = Timeout.InfiniteTimeSpan });
            ready.Set();
            while (!_stop)
            {
                if (reader.TryRead(n, out Chunk<float> _))
                {
                    reader.Advance(n);
                }
                else
                {
                    Thread.SpinWait(8);
                }
            }
        })
        { IsBackground = true, Name = "bench-drain" };
        _drain.Start();
        ready.Wait();
    }

    [GlobalCleanup(Target = nameof(Write_SpinningReaderThread))]
    public void CleanupThreaded()
    {
        _stop = true;
        _drain?.Join();
        _threaded.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long WriteRead_Protocol()
    {
        int n = BucketElements;
        using (Bucket<float> b = _single.GetBucket(n))
        {
            b.Commit(n);
        }

        _singleReader.TryRead(n, out Chunk<float> chunk);
        long cursor = chunk.Cursor;
        _singleReader.Advance(n);
        return cursor;
    }

    [Benchmark]
    public float WriteRead_FillAndSum()
    {
        int n = BucketElements;
        using (Bucket<float> b = _single.GetBucket(n))
        {
            b.Span.Fill(1f);
            b.Commit(n);
        }

        _singleReader.TryRead(n, out Chunk<float> chunk);
        float sum = Peer.Sum(chunk.Data.Span);
        _singleReader.Advance(n);
        return sum;
    }

    [Benchmark]
    public float WriteRead_FillAndSum_OddBucket()
    {
        int n = BucketElements + 1;                                  // does not divide the ring: every (Capacity / n)-th bucket crosses the data/mirror boundary
        using (Bucket<float> b = _single.GetBucket(n))
        {
            b.Span.Fill(1f);
            b.Commit(n);
        }

        _singleReader.TryRead(n, out Chunk<float> chunk);
        float sum = Peer.Sum(chunk.Data.Span);
        _singleReader.Advance(n);
        return sum;
    }

    [Benchmark]
    public long Write_SpinningReaderThread()
    {
        int n = BucketElements;
        using Bucket<float> b = _threaded.GetBucket(n);
        long cursor = b.Cursor;
        b.Commit(n);
        return cursor;
    }
}
