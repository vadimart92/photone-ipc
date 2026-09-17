using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// Cost of stream tags (DESIGN §16) on the in-process path, one thread, buckets of 256 floats, payload untouched:
/// <list type="bullet">
/// <item><see cref="Protocol_NoTags"/>: GetBucket / Commit / TryRead / Advance on a buffer without a tag area (as <c>HotPathBenchmarks.WriteRead_Protocol</c>).</item>
/// <item><see cref="Protocol_TagAreaUnused"/>: the same on a buffer with a 1 MiB tag log that never carries a tag.</item>
/// <item><see cref="OneTagPerBucket"/>: plus one small non-persistent tag per bucket: serialize, publish, load, deserialize, deliver in the chunk, consume.</item>
/// <item><see cref="OnePersistentTagPerBucket"/>: plus one persistent tag per bucket: also the persistent-tag table and the joiner snapshot, and the reader's last value.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[GcServer(false)]
[GcConcurrent(false)]
public class TagBenchmarks
{
    private const int N = 256;

    private RingBuffer<float> _plain = null!;
    private RingReader<float> _plainReader = null!;
    private RingBuffer<float> _unused = null!;
    private RingReader<float> _unusedReader = null!;
    private RingBuffer<float> _tagged = null!;
    private RingReader<float> _taggedReader = null!;

    [GlobalSetup]
    public void Setup()
    {
        _plain = RingBuffer<float>.Create(1 << 20);
        _plainReader = _plain.CreateReader();
        _unused = RingBuffer<float>.Create(1 << 20, options: new RingBufferOptions { TagCapacity = 1 << 20 });
        _unusedReader = _unused.CreateReader();
        var serializer = new JsonTagSerializer().Register<BenchLabel>().Register<BenchRate>();
        _tagged = RingBuffer<float>.Create(1 << 20, options: new RingBufferOptions { TagCapacity = 1 << 20, TagSerializer = serializer });
        _taggedReader = _tagged.CreateReader();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plainReader.Dispose();
        _plain.Dispose();
        _unusedReader.Dispose();
        _unused.Dispose();
        _taggedReader.Dispose();
        _tagged.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long Protocol_NoTags() => Round(_plain, _plainReader);

    [Benchmark]
    public long Protocol_TagAreaUnused() => Round(_unused, _unusedReader);

    [Benchmark]
    public int OneTagPerBucket()
    {
        using (Bucket<float> b = _tagged.GetBucket(N))
        {
            b.AddTag(new BenchLabel { Offset = b.StartOffset + 17, Text = "burst" });
            b.Commit(N);
        }

        _taggedReader.TryRead(N, out Chunk<float> chunk);
        int tags = chunk.Tags.Length;
        _taggedReader.Advance(N);
        return tags;
    }

    [Benchmark]
    public int OnePersistentTagPerBucket()
    {
        using (Bucket<float> b = _tagged.GetBucket(N))
        {
            b.AddTag(new BenchRate { Offset = b.StartOffset, Hz = 48_000 });
            b.Commit(N);
        }

        _taggedReader.TryRead(N, out Chunk<float> chunk);
        int tags = chunk.Tags.Length;
        _taggedReader.Advance(N);
        return tags;
    }

    /// <summary>Reference: the JSON serializer alone, writing the tag of <see cref="OneTagPerBucket"/> and reading it back (what the library adds is the rest).</summary>
    [Benchmark]
    public ulong JsonRoundTripOnly()
    {
        _json.Clear();
        _serializer.Serialize(new BenchLabel { Offset = 17, Text = "burst" }, _json);
        return _serializer.Deserialize(_serializer.GetTypeName<BenchLabel>(), _json.WrittenSpan)!.Offset;
    }

    private readonly System.Buffers.ArrayBufferWriter<byte> _json = new(256);
    private readonly JsonTagSerializer _serializer = new JsonTagSerializer().Register<BenchLabel>();

    private static long Round(RingBuffer<float> buffer, RingReader<float> reader)
    {
        using (Bucket<float> b = buffer.GetBucket(N))
        {
            b.Commit(N);
        }

        reader.TryRead(N, out Chunk<float> chunk);
        long cursor = chunk.Cursor;
        reader.Advance(N);
        return cursor;
    }
}

/// <summary>A small non-persistent tag.</summary>
public sealed class BenchLabel : ITag
{
    public ulong Offset { get; set; }

    public string Key => "burst";

    public string Text { get; set; } = "";
}

/// <summary>A small persistent tag.</summary>
public sealed class BenchRate : ITag
{
    public static bool IsPersistent => true;

    public ulong Offset { get; set; }

    public string Key => "sample_rate";

    public double Hz { get; set; }
}
