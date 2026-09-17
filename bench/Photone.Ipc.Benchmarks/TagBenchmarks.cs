using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// Cost of stream tags (DESIGN §16), one thread, buckets of 256 floats, payload untouched. Readers are either the writer's own (the tag objects) or of
/// the buffer opened by name in the same process (the records in shared memory, deserialized):
/// <list type="bullet">
/// <item><see cref="Protocol_NoTags"/>: GetBucket / Commit / TryRead / Advance on a buffer without tags (as <c>HotPathBenchmarks.WriteRead_Protocol</c>).</item>
/// <item><see cref="Protocol_TagsUnused_WritersReader"/> / <see cref="Protocol_TagsUnused_Opener"/>: the same on a buffer with tags that never carries one.</item>
/// <item><c>OneTag_*</c>: plus one small non-persistent tag per bucket: stage, publish, load, deliver in the chunk, consume.</item>
/// <item><c>OnePersistentTag_*</c>: plus one persistent tag per bucket: also the snapshot for joiners, and the reader's last value.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[GcServer(false)]
[GcConcurrent(false)]
public class TagBenchmarks
{
    private const int N = 256;

    private readonly List<IDisposable> _owned = [];
    private RingBuffer<float> _plain = null!;
    private RingReader<float> _plainReader = null!;
    private (RingBuffer<float> Writer, RingReader<float> Reader) _unusedLocal;
    private (RingBuffer<float> Writer, RingReader<float> Reader) _unusedOpener;
    private (RingBuffer<float> Writer, RingReader<float> Reader) _inProcess;
    private (RingBuffer<float> Writer, RingReader<float> Reader) _crossLocal;
    private (RingBuffer<float> Writer, RingReader<float> Reader) _crossOpener;

    [GlobalSetup]
    public void Setup()
    {
        _plain = RingBuffer<float>.Create(1 << 20);
        _plainReader = _plain.CreateReader();
        _unusedLocal = Pair(TagMode.InProcess, opener: false);
        _unusedOpener = Pair(TagMode.CrossProcess, opener: true);
        _inProcess = Pair(TagMode.InProcess, opener: false);
        _crossLocal = Pair(TagMode.CrossProcess, opener: false);
        _crossOpener = Pair(TagMode.CrossProcess, opener: true);
    }

    private (RingBuffer<float> Writer, RingReader<float> Reader) Pair(TagMode mode, bool opener)
    {
        var serializer = new JsonTagSerializer().Register<BenchLabel>().Register<BenchRate>();
        string name = "photone.bench.tags." + Guid.NewGuid().ToString("N");
        var writer = RingBuffer<float>.Create(1 << 20, name, new RingBufferOptions { Tags = mode, TagSerializer = serializer });
        _owned.Add(writer);
        RingBuffer<float> source = writer;
        if (opener)
        {
            source = RingBuffer<float>.Open(name, new RingBufferOptions { TagSerializer = serializer });
            _owned.Add(source);
        }

        RingReader<float> reader = source.CreateReader();
        _owned.Insert(0, reader);
        return (writer, reader);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plainReader.Dispose();
        _plain.Dispose();
        foreach (IDisposable d in _owned)
        {
            d.Dispose();
        }
    }

    [Benchmark(Baseline = true)]
    public long Protocol_NoTags() => Round(_plain, _plainReader);

    [Benchmark]
    public long Protocol_TagsUnused_WritersReader() => Round(_unusedLocal.Writer, _unusedLocal.Reader);

    [Benchmark]
    public long Protocol_TagsUnused_Opener() => Round(_unusedOpener.Writer, _unusedOpener.Reader);

    [Benchmark]
    public int OneTag_InProcess() => OneTag(_inProcess);

    [Benchmark]
    public int OneTag_CrossProcess_WritersReader() => OneTag(_crossLocal);

    [Benchmark]
    public int OneTag_CrossProcess_Opener() => OneTag(_crossOpener);

    [Benchmark]
    public int OnePersistentTag_InProcess() => OnePersistentTag(_inProcess);

    [Benchmark]
    public int OnePersistentTag_CrossProcess_Opener() => OnePersistentTag(_crossOpener);

    /// <summary>Reference: the JSON serializer alone, writing the tag of <see cref="OneTag_CrossProcess_Opener"/> and reading it back (what the library adds is the rest).</summary>
    [Benchmark]
    public ulong JsonRoundTripOnly()
    {
        _json.Clear();
        _serializer.Serialize(new BenchLabel { Offset = 17, Text = "burst" }, _json);
        return _serializer.Deserialize(_serializer.GetTypeName<BenchLabel>(), _json.WrittenSpan)!.Offset;
    }

    private readonly System.Buffers.ArrayBufferWriter<byte> _json = new(256);
    private readonly JsonTagSerializer _serializer = new JsonTagSerializer().Register<BenchLabel>();

    private static int OneTag((RingBuffer<float> Writer, RingReader<float> Reader) pair)
    {
        using (Bucket<float> b = pair.Writer.GetBucket(N))
        {
            b.AddTag(new BenchLabel { Text = "burst" }, 17);
            b.Commit(N);
        }

        pair.Reader.TryRead(N, out Chunk<float> chunk);
        int tags = chunk.Tags.Length;
        pair.Reader.Advance(N);
        return tags;
    }

    private static int OnePersistentTag((RingBuffer<float> Writer, RingReader<float> Reader) pair)
    {
        using (Bucket<float> b = pair.Writer.GetBucket(N))
        {
            b.AddTag(new BenchRate { Hz = 48_000 });
            b.Commit(N);
        }

        pair.Reader.TryRead(N, out Chunk<float> chunk);
        int tags = chunk.Tags.Length;
        pair.Reader.Advance(N);
        return tags;
    }

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
