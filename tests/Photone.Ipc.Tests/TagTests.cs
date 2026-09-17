using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Photone.Ipc.Internal;
using Photone.Ipc.TestChild;

namespace Photone.Ipc.Tests;

/// <summary>
/// Stream tags (DESIGN §16): delivery with the elements, ordering, persistence and joining readers, for the three kinds of reader (the writer's own readers
/// of an in-process and of a cross-process buffer, and readers of the buffer opened from shared memory); tag memory that grows, moves and shrinks; the pool.
/// </summary>
public sealed unsafe class TagTests
{
    /// <summary>Which readers a test reads with.</summary>
    public enum ReaderKind
    {
        /// <summary><see cref="TagMode.InProcess"/>, readers of the writer's buffer: the tag objects.</summary>
        InProcess,

        /// <summary><see cref="TagMode.CrossProcess"/>, readers of the writer's buffer: the tag objects too.</summary>
        CrossProcessWriter,

        /// <summary><see cref="TagMode.CrossProcess"/>, readers of the buffer opened by name: records in shared memory, deserialized.</summary>
        CrossProcessOpener,
    }

    /// <summary>A writer with tags and, for <see cref="ReaderKind.CrossProcessOpener"/>, the same buffer opened by name.</summary>
    private sealed class Setup : IDisposable
    {
        public Setup(ReaderKind kind, long capacity = 1 << 16, ITagSerializer? serializer = null, RingBufferPool? pool = null, long maxUnreadTags = 0, long maxUnreadTagBytes = 0)
        {
            Kind = kind;
            Serializer = serializer ?? TagPlan.CreateSerializer();
            Name = TestNames.UniqueSection();
            var options = new RingBufferOptions
            {
                Tags = kind == ReaderKind.InProcess ? TagMode.InProcess : TagMode.CrossProcess,
                TagSerializer = kind == ReaderKind.InProcess ? null : Serializer,
                Pool = pool,
                MaxUnreadTags = maxUnreadTags,
                MaxUnreadTagBytes = maxUnreadTagBytes,
            };
            Writer = RingBuffer<long>.Create(capacity, Name, options);
            Opened = kind == ReaderKind.CrossProcessOpener ? RingBuffer<long>.Open(Name, new RingBufferOptions { TagSerializer = Serializer }) : null;
        }

        public ReaderKind Kind { get; }

        public ITagSerializer Serializer { get; }

        public string Name { get; }

        public RingBuffer<long> Writer { get; }

        public RingBuffer<long>? Opened { get; }

        public SharedTagLog? Shared => Writer.TagWriter!.Shared;

        public RingReader<long> CreateReader() => (Opened ?? Writer).CreateReader();

        public void Dispose()
        {
            Opened?.Dispose();
            Writer.Dispose();
        }
    }

    public static TheoryData<ReaderKind> AllKinds => [ReaderKind.InProcess, ReaderKind.CrossProcessWriter, ReaderKind.CrossProcessOpener];

    /// <summary>AddTag on a bucket, returning what it threw (a ref struct cannot be captured by an Assert.Throws lambda).</summary>
    private static Exception? AddTagError<TTag>(Bucket<long> bucket, TTag tag, int index = 0) where TTag : ITag
    {
        try
        {
            bucket.AddTag(tag, index);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static RingBufferOptions CrossProcess(RingBufferPool? pool = null) => new() { Tags = TagMode.CrossProcess, TagSerializer = TagPlan.CreateSerializer(), Pool = pool };

    private static void WriteBucket(RingBuffer<long> buffer, int n, Action<Bucket<long>>? tag = null, int? commit = null)
    {
        using Bucket<long> bucket = buffer.GetBucket(n);
        for (int j = 0; j < n; j++)
        {
            bucket.Span[j] = bucket.Cursor + j;
        }

        tag?.Invoke(bucket);
        bucket.Commit(commit ?? n);
    }

    private static LabelTag Label(string text) => new() { Text = text };

    private static string[] Texts(Chunk<long> chunk) => chunk.Tags.ToArray().Select(t => ((LabelTag)t).Text).ToArray();

    private static ITag? Last(RingReader<long> reader, string key) => TagPlan.Find(reader.ReadLastTagValues(), key);

    // ------------------------------------------------------------------ configuration

    [Fact]
    public void WithoutTags_TheBufferCarriesNoTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(TagMode.None, buffer.Tags);
        Assert.Equal(0, buffer.Header->TagReserveBytes);

        using (Bucket<long> bucket = buffer.GetBucket(4))
        {
            InvalidOperationException ex = Assert.IsType<InvalidOperationException>(AddTagError(bucket, Label("x")));
            Assert.Contains("RingBufferOptions.Tags", ex.Message, StringComparison.Ordinal);
            bucket.Commit(4);
        }

        Assert.Throws<InvalidOperationException>(() => buffer.AddTag(Label("x")));
        Assert.True(reader.TryRead(4, out Chunk<long> chunk));
        Assert.True(chunk.Tags.IsEmpty);
        Assert.Equal(0, reader.ReadLastTagValues().Length);
        Assert.Null(reader.Tags);
        Assert.Null(buffer.TagWriter);
    }

    [Fact]
    public void CrossProcessTags_NeedASerializer_AndTheModeMustBeValid()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.CrossProcess }));
        Assert.Contains("TagSerializer", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = (TagMode)7 }));
    }

    [Fact]
    public void TagMode_IsVisibleToOpeners_AndInProcessTagsStayInTheWritersProcess()
    {
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, new RingBufferOptions { Tags = TagMode.InProcess });
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        Assert.Equal(TagMode.InProcess, writer.Tags);
        Assert.Equal(TagMode.InProcess, opened.Tags);
        Assert.Equal(0, writer.Header->TagReserveBytes);                   // no shared tag memory at all
        using RingReader<long> own = writer.CreateReader();
        using RingReader<long> other = opened.CreateReader();
        WriteBucket(writer, 4, b => b.AddTag(Label("local"), 1));

        Assert.True(own.TryRead(4, out Chunk<long> ownChunk));
        Assert.Equal(["local"], Texts(ownChunk));
        Assert.True(other.TryRead(4, out Chunk<long> otherChunk));
        Assert.True(otherChunk.Tags.IsEmpty);
        Assert.Null(other.Tags);

        string crossName = TestNames.UniqueSection();
        using var cross = RingBuffer<long>.Create(1 << 16, crossName, CrossProcess());
        using var crossOpened = RingBuffer<long>.Open(crossName);
        Assert.Equal(TagMode.CrossProcess, crossOpened.Tags);
        Assert.Equal(TagFormat.ReserveBytes, crossOpened.Header->TagReserveBytes);
        Assert.Equal(0, cross.TagViews!.MappedRings);                       // nothing mapped or committed before the first tag
    }

    // ------------------------------------------------------------------ delivery

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Tags_ArriveWithTheirElements(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 100, b =>
        {
            b.AddTag(Label("first"));
            b.AddTag(Label("ten"), 10);
            b.AddTag(Label("last"), 99);
        });

        Assert.True(reader.TryRead(10, out Chunk<long> head));
        Assert.Equal(["first"], Texts(head));
        Assert.Equal(0ul, head.StartOffset);

        Assert.True(reader.TryRead(100, out Chunk<long> all));
        Assert.Equal(["first", "ten", "last"], Texts(all));
        Assert.Equal([0ul, 10ul, 99ul], all.Tags.ToArray().Select(t => t.Offset));

        reader.Advance(11);                                             // past "first" and "ten"
        Assert.True(reader.TryRead(89, out Chunk<long> rest));
        Assert.Equal(11ul, rest.StartOffset);
        Assert.Equal(["last"], Texts(rest));
        Assert.True(reader.TryRead(88, out Chunk<long> beforeLast));
        Assert.True(beforeLast.Tags.IsEmpty);
        reader.Advance(89);
        Assert.Equal(0, reader.Tags!.QueuedCount);
        Assert.IsType(kind == ReaderKind.CrossProcessOpener ? typeof(SharedTagReader) : typeof(LocalTagReader), reader.Tags);
    }

    [Fact]
    public void WritersOwnReaders_ReceiveTheAddedInstances_WithoutSerializing()
    {
        var inProcessSerializer = new CountingSerializer(TagPlan.CreateSerializer());
        using (var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess, TagSerializer = inProcessSerializer }))
        {
            using RingReader<long> reader = buffer.CreateReader();
            LabelTag tag = Label("same");
            var rate = new SampleRateTag { Rate = 48_000 };
            WriteBucket(buffer, 8, b =>
            {
                b.AddTag(tag, 3);
                b.AddTag(rate, 5);
            });

            Assert.True(reader.TryRead(8, out Chunk<long> chunk));
            Assert.Same(tag, chunk.Tags.Span[0]);
            Assert.Same(rate, chunk.Tags.Span[1]);
            reader.Advance(8);
            Assert.Same(rate, Last(reader, SampleRateTag.TagKey));
            Assert.Equal(0, inProcessSerializer.Serialized);                // an in-process buffer never serializes, even with a serializer configured
            Assert.Equal(0, inProcessSerializer.Deserialized);
        }

        var serializer = new CountingSerializer(TagPlan.CreateSerializer());
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, new RingBufferOptions { Tags = TagMode.CrossProcess, TagSerializer = serializer });
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = serializer });
        using RingReader<long> own = writer.CreateReader();
        using RingReader<long> shared = opened.CreateReader();
        LabelTag label = Label("both");
        WriteBucket(writer, 4, b => b.AddTag(label, 2));
        Assert.Equal(1, serializer.Serialized);                             // once, for shared memory

        Assert.True(own.TryRead(4, out Chunk<long> ownChunk));
        Assert.Same(label, Assert.Single(ownChunk.Tags.ToArray()));
        Assert.Equal(0, serializer.Deserialized);
        Assert.True(shared.TryRead(4, out Chunk<long> sharedChunk));
        LabelTag copy = Assert.IsType<LabelTag>(Assert.Single(sharedChunk.Tags.ToArray()));
        Assert.NotSame(label, copy);
        Assert.Equal("both", copy.Text);
        Assert.Equal(1, serializer.Deserialized);
    }

    [Fact]
    public void AddTag_SetsTheOffset_AndOpenersTakeItFromTheRecord()
    {
        var serializer = new JsonTagSerializer().Register<QuietOffsetTag>();
        using var setup = new Setup(ReaderKind.CrossProcessOpener, serializer: serializer);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 3);
        var tag = new QuietOffsetTag { Offset = 12345, N = 7 };        // whatever the caller put there is replaced
        WriteBucket(setup.Writer, 10, b => b.AddTag(tag, 4));
        Assert.Equal(7ul, tag.Offset);

        Assert.True(reader.TryRead(13, out Chunk<long> chunk));
        QuietOffsetTag received = Assert.IsType<QuietOffsetTag>(Assert.Single(chunk.Tags.ToArray()));
        Assert.NotSame(tag, received);
        Assert.Equal(7ul, received.Offset);                            // not in the JSON: set from the record header
        Assert.Equal(7, received.N);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void PartiallyAdvancedChunk_KeepsTheTagsItStillCovers(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 10, b =>
        {
            b.AddTag(Label("one"), 1);
            b.AddTag(Label("five"), 5);
        });

        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        ReadOnlyMemory<ITag> tags = chunk.Tags;
        reader.Advance(3);                                              // past "one", not past "five"
        for (int i = 0; i < 20; i++)
        {
            WriteBucket(setup.Writer, 10, b => b.AddTag(Label("later")));   // more tags join the reader's queue (and grow it)
        }

        Assert.True(reader.TryRead(207, out Chunk<long> next));
        Assert.Equal(21, next.Tags.Length);
        Assert.Equal("five", ((LabelTag)tags.Span[1]).Text);           // the first chunk's view of what it still covers is intact
        Assert.NotNull(tags.Span[0]);                                   // and a tag it was advanced past is not pulled out from under it
        Assert.Equal("five", ((LabelTag)next.Tags.Span[0]).Text);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Tags_AddedInAnyOrder_ArriveInOffsetOrder_EqualOffsetsInAddOrder(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 10, b =>
        {
            b.AddTag(Label("b"), 5);
            b.AddTag(Label("a"), 2);
            b.AddTag(Label("c"), 5);
            b.AddTag(Label("e"), 9);
            b.AddTag(Label("d"), 5);
        });

        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["a", "b", "c", "d", "e"], Texts(chunk));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void CommitPrefix_DropsTheTagsOfTheDroppedElements(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 10, b =>
        {
            b.AddTag(Label("kept"), 3);
            b.AddTag(Label("dropped at 5"), 5);
            b.AddTag(Label("dropped at 8"), 8);
        }, commit: 5);
        WriteBucket(setup.Writer, 5, b => b.AddTag(Label("second bucket at 5")));
        WriteBucket(setup.Writer, 3, b => b.AddTag(Label("dropped entirely")), commit: 0);

        Assert.Equal(10, setup.Writer.WriteCursor);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["kept", "second bucket at 5"], Texts(chunk));
        Assert.Equal([3ul, 5ul], chunk.Tags.ToArray().Select(t => t.Offset));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void DisposedBucket_PublishesNoTags(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        using (Bucket<long> bucket = setup.Writer.GetBucket(4))
        {
            bucket.AddTag(Label("never"));
        }

        WriteBucket(setup.Writer, 4, b => b.AddTag(Label("published"), 1));
        Assert.True(reader.TryRead(4, out Chunk<long> chunk));
        Assert.Equal(["published"], Texts(chunk));
        Assert.Equal(0, setup.Writer.TagWriter!.PendingCount);
    }

    [Fact]
    public void AddTag_Validation()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> buffer = setup.Writer;
        WriteBucket(buffer, 7);
        using (Bucket<long> bucket = buffer.GetBucket(10))
        {
            Assert.IsType<ArgumentOutOfRangeException>(AddTagError(bucket, Label("before"), -1));
            Assert.IsType<ArgumentOutOfRangeException>(AddTagError(bucket, Label("after"), 10));
            Assert.IsType<ArgumentException>(AddTagError<ITag>(bucket, Label("interface")));
            Assert.IsType<ArgumentException>(AddTagError(bucket, new LabelTag { Key = null!, Text = "null key" }));
            Assert.IsType<ArgumentException>(AddTagError(bucket, new LabelTag { Key = new string('k', TagFormat.MaxNameBytes + 1) }));   // a record's key length is 16 bits
            Assert.Equal(0, buffer.TagWriter!.PendingCount);                // the failed ones left nothing behind

            for (int i = 0; i < 1000; i++)
            {
                bucket.AddTag(Label(new string('y', 1000)), i % 10);        // a megabyte of tags in one bucket: no capacity to exceed
            }

            bucket.Commit(10);
            Assert.IsType<InvalidOperationException>(AddTagError(bucket, Label("after commit")));
        }

        using RingReader<long> reader = setup.CreateReader();
        using (Bucket<long> bucket = buffer.GetBucket(1))
        {
            bucket.AddTag(Label("fine"));
            bucket.Commit(1);
        }

        Assert.True(reader.TryRead(1, out Chunk<long> chunk));
        Assert.Equal(["fine"], Texts(chunk));

        // in-process tags are never records: a long key is fine
        using var inProcess = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess });
        using RingReader<long> local = inProcess.CreateReader();
        WriteBucket(inProcess, 1, b => b.AddTag(new LabelTag { Key = new string('k', TagFormat.MaxNameBytes + 1) }));
        Assert.True(local.TryRead(1, out Chunk<long> localChunk));
        Assert.Equal(TagFormat.MaxNameBytes + 1, Assert.Single(localChunk.Tags.ToArray()).Key.Length);
    }

    [Fact]
    public void StaleBucketCopy_CannotTagTheNextBucket()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: CrossProcess());
        Bucket<long> first = buffer.GetBucket(4);
        Bucket<long> copy = first;                                      // a copy keeps _committed = -1 after the original commits
        first.Commit(4);
        using Bucket<long> second = buffer.GetBucket(4);
        Assert.IsType<InvalidOperationException>(AddTagError(copy, Label("stale")));
        second.Commit(0);
    }

    // ------------------------------------------------------------------ tags added to the buffer

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void BufferAddTag_AttachesToTheNextElementWritten(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        RingBuffer<long> buffer = setup.Writer;
        using RingReader<long> reader = setup.CreateReader();
        buffer.AddTag(Label("before the first bucket"));
        WriteBucket(buffer, 10, b => b.AddTag(Label("bucket"), 3));
        buffer.AddTag(Label("buffer at 10"));
        WriteBucket(buffer, 5, b => b.AddTag(Label("bucket at 10")));

        Assert.True(reader.TryRead(15, out Chunk<long> chunk));
        Assert.Equal(["before the first bucket", "bucket", "buffer at 10", "bucket at 10"], Texts(chunk));
        Assert.Equal([0ul, 3ul, 10ul, 10ul], chunk.Tags.ToArray().Select(t => t.Offset));
        Assert.Equal(0, buffer.TagWriter!.PendingCount);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void BufferAddTag_StaysPendingThroughCommitsThatPublishNothing(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        RingBuffer<long> buffer = setup.Writer;
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(buffer, 2);

        buffer.AddTag(new SampleRateTag { Rate = 44_100 });
        WriteBucket(buffer, 4, b => b.AddTag(Label("dropped with its element")), commit: 0);
        using (Bucket<long> bucket = buffer.GetBucket(3))
        {
            var mid = new SampleRateTag { Rate = 48_000 };
            buffer.AddTag(mid);                                         // during a bucket: the bucket's first element
            Assert.Equal(bucket.StartOffset, mid.Offset);
        }                                                               // disposed without a commit: still pending

        Assert.Equal(2, buffer.TagWriter!.PendingCount);
        Assert.Equal(2, buffer.WriteCursor);
        WriteBucket(buffer, 1);

        Assert.True(reader.TryRead(3, out Chunk<long> chunk));
        Assert.Equal([2ul, 2ul], chunk.Tags.ToArray().Select(t => t.Offset));
        reader.Advance(3);
        Assert.Equal(48_000, Assert.IsType<SampleRateTag>(Last(reader, SampleRateTag.TagKey)).Rate);
        Assert.Equal(0, buffer.TagWriter.PendingCount);
    }

    [Fact]
    public void BufferAddTag_Validation_AndDroppedWhenTheWriterCloses()
    {
        string name = TestNames.UniqueSection();
        var buffer = RingBuffer<long>.Create(1 << 16, name, CrossProcess());
        using (var opened = RingBuffer<long>.Open(name))
        {
            Assert.Throws<InvalidOperationException>(() => opened.AddTag(Label("reader role")));
        }

        Assert.Throws<ArgumentException>(() => buffer.AddTag<ITag>(Label("interface")));
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 5);
        buffer.AddTag(Label("never published"));
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.AddTag(Label("after dispose")));

        Assert.True(reader.TryRead(5, out Chunk<long> chunk));
        Assert.True(chunk.Tags.IsEmpty);
        reader.Advance(5);
        Assert.True(reader.IsCompleted);
    }

    // ------------------------------------------------------------------ persistent tags

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void LastTagValues_FollowTheReaderPosition(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 100, b =>
        {
            b.AddTag(new SampleRateTag { Rate = 1000 });
            b.AddTag(Label("not persistent"), 20);
            b.AddTag(new SampleRateTag { Rate = 2000 }, 50);
        });

        Assert.Equal(0, reader.ReadLastTagValues().Length);
        Assert.True(reader.TryRead(100, out Chunk<long> chunk));
        Assert.Equal(3, chunk.Tags.Length);
        Assert.Equal(0, reader.ReadLastTagValues().Length);            // reading is not passing

        reader.Advance(1);
        Assert.Equal(1000, Assert.IsType<SampleRateTag>(Assert.Single(reader.ReadLastTagValues().ToArray())).Rate);
        reader.Advance(49);                                             // at 50: the second rate applies to element 50, not before it
        Assert.Equal(1000, ((SampleRateTag)Last(reader, SampleRateTag.TagKey)!).Rate);
        reader.Advance(1);
        Assert.Equal(2000, ((SampleRateTag)Last(reader, SampleRateTag.TagKey)!).Rate);
        Assert.Equal(1, reader.ReadLastTagValues().Length);            // one per key; the label is not state
        Assert.Null(Last(reader, "label"));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void LastTagValues_AreOnePerKeyInFirstSeenOrder_WithoutAllocating(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        for (int i = 0; i < 50; i++)
        {
            WriteBucket(setup.Writer, 2, b =>
            {
                b.AddTag(new StateTag { Key = "b", Value = i });
                b.AddTag(new StateTag { Key = "a", Value = i }, 1);
            });
        }

        reader.Advance((int)reader.Available);
        Assert.Equal(["b", "a"], reader.ReadLastTagValues().ToArray().Select(t => t.Key));
        Assert.Equal(49, ((StateTag)Last(reader, "a")!).Value);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 10_000; i++)
        {
            foreach (ITag tag in reader.ReadLastTagValues())
            {
                total += tag.Key.Length;
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(20_000, total);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void JoiningReader_StartsWithTheLastPersistentTags(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        WriteBucket(setup.Writer, 10, b =>
        {
            b.AddTag(new SampleRateTag { Rate = 1 }, 1);
            b.AddTag(new StateTag { Key = "mode", Value = 7 }, 2);
            b.AddTag(Label("before join"), 3);
        });
        WriteBucket(setup.Writer, 10, b => b.AddTag(new SampleRateTag { Rate = 2 }, 5));

        using RingReader<long> late = setup.CreateReader();
        Assert.Equal(20, late.ReadCursor);
        Assert.Equal(2, late.ReadLastTagValues().Length);
        Assert.Equal(2, ((SampleRateTag)Last(late, SampleRateTag.TagKey)!).Rate);
        Assert.Equal(15ul, Last(late, SampleRateTag.TagKey)!.Offset);
        Assert.Equal(7, ((StateTag)Last(late, "mode")!).Value);

        WriteBucket(setup.Writer, 10, b =>
        {
            b.AddTag(Label("after join"));
            b.AddTag(new StateTag { Key = "mode", Value = 8 }, 4);
        });

        Assert.True(late.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(2, chunk.Tags.Length);
        Assert.Equal("after join", ((LabelTag)chunk.Tags.Span[0]).Text);
        late.Advance(10);
        Assert.Equal(8, ((StateTag)Last(late, "mode")!).Value);
        Assert.Equal(2, ((SampleRateTag)Last(late, SampleRateTag.TagKey)!).Rate);
    }

    [Fact]
    public void JoiningReader_StartsAtTheSnapshotWhenItIsNewerThanTheCursorItJoinedAt()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        WriteBucket(setup.Writer, 30, b => b.AddTag(new SampleRateTag { Rate = 5 }, 20));
        WriteBucket(setup.Writer, 30, b => b.AddTag(Label("at 31"), 1));

        // a reader that loaded W = 10 before the writer published up to 60: the table already holds the rate at 20, so it must start at 60
        SharedTagReader tags = SharedTagReader.Join(setup.Opened!.Header, setup.Opened.TagViews!, TagPlan.CreateSerializer(), joinW: 10, out long cursor);
        Assert.Equal(60, cursor);
        Assert.Equal(5, ((SampleRateTag)TagPlan.Find(tags.LastValues(), SampleRateTag.TagKey)!).Rate);
        Assert.Equal(long.MaxValue, tags.NextOffset);

        // a reader that joined at the snapshot's cursor starts there, with nothing queued
        SharedTagReader current = SharedTagReader.Join(setup.Opened.Header, setup.Opened.TagViews!, null, joinW: 60, out long at);
        Assert.Equal(60, at);
        Assert.IsType<UnknownTag>(TagPlan.Find(current.LastValues(), SampleRateTag.TagKey));   // no serializer here

        // the object log renews its snapshot once a chunk of entries has accumulated: at entry 256, the 254th of this loop (two came before it)
        LocalTagLog log = setup.Writer.TagWriter!.Local;
        Assert.Equal(0, log.Current.W);
        for (int i = 0; i < LocalTagLog.ChunkSize; i++)
        {
            WriteBucket(setup.Writer, 1, b => b.AddTag(new StateTag { Key = "n", Value = i }));
        }

        long snapshotW = 60 + LocalTagLog.ChunkSize - 2;
        Assert.Equal(LocalTagLog.ChunkSize, log.Current.End);
        Assert.Equal(snapshotW, log.Current.W);
        LocalTagReader local = LocalTagReader.Join(log, joinW: 10, out long localCursor);
        Assert.Equal(snapshotW, localCursor);
        Assert.Equal(LocalTagLog.ChunkSize - 3, ((StateTag)TagPlan.Find(local.LastValues(), "n")!).Value);   // the entry right before the snapshot's cursor
        Assert.Equal(5, ((SampleRateTag)TagPlan.Find(local.LastValues(), SampleRateTag.TagKey)!).Rate);
        Assert.Equal(2, local.QueuedCount);                             // the entries after the snapshot lie at or after its cursor
    }

    [Fact]
    public void PersistentTable_GrowsAsKeysArrive_AndReusesTheSpaceOfAKey()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> buffer = setup.Writer;
        for (int i = 0; i < 2000; i++)
        {
            WriteBucket(buffer, 1, b => b.AddTag(new StateTag { Key = "only", Value = i }));
        }

        Assert.Equal(1, buffer.Header->TagStateCount);
        Assert.Equal(Layout.HeaderViewBytes, buffer.Header->TagTableCommitted);   // one key: the first 64 KiB are plenty

        const int Keys = 400;                                           // ~1 KiB each: more than the table's first commit
        for (int k = 0; k < Keys; k++)
        {
            WriteBucket(buffer, 1, b => b.AddTag(new StateTag { Key = "key " + k + " " + new string('p', 1000), Value = k }));
        }

        Assert.Equal(Keys + 1, buffer.Header->TagStateCount);
        Assert.True(buffer.Header->TagTableCommitted >= buffer.Header->TagStateUsed && buffer.Header->TagStateUsed > Layout.HeaderViewBytes * 4);

        using RingReader<long> joiner = setup.CreateReader();
        Assert.Equal(Keys + 1, joiner.ReadLastTagValues().Length);
        Assert.Equal(1999, ((StateTag)Last(joiner, "only")!).Value);
        Assert.Equal(Keys - 1, ((StateTag)joiner.ReadLastTagValues()[Keys]).Value);

        // a record in the middle of the table changes its size (every later record moves), another keeps it (rewritten in place), a key is added
        WriteBucket(buffer, 2, b =>
        {
            b.AddTag(new StateTag { Key = "key 7 " + new string('p', 1000), Value = -7 }, 0);
            b.AddTag(new StateTag { Key = "key 3 " + new string('p', 1000), Value = 33 }, 0);   // two records of one key in one commit: the last counts
            b.AddTag(new StateTag { Key = "key 3 " + new string('p', 1000), Value = 3_000_000_000_000 }, 1);
            b.AddTag(new StateTag { Key = "added", Value = 1 }, 1);
        });
        using RingReader<long> late = setup.CreateReader();
        ReadOnlySpan<ITag> state = late.ReadLastTagValues();
        Assert.Equal(Keys + 2, state.Length);
        Assert.Equal(3_000_000_000_000, ((StateTag)state[4]).Value);           // "only", then key 0.. in the order they appeared
        Assert.Equal(-7, ((StateTag)state[8]).Value);
        Assert.Equal(Keys - 1, ((StateTag)state[Keys]).Value);                  // behind the moved records, still intact
        Assert.Equal(1, ((StateTag)state[Keys + 1]).Value);
        for (int k = 0; k < Keys; k++)
        {
            long expected = k == 3 ? 3_000_000_000_000 : k == 7 ? -7 : k;
            Assert.Equal(expected, ((StateTag)state[k + 1]).Value);
        }
    }

    // ------------------------------------------------------------------ serializers

    [Fact]
    public void UnknownTypes_ArriveAsUnknownTag()
    {
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, CrossProcess());
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = new JsonTagSerializer().Register<StateTag>() });
        using RingReader<long> reader = opened.CreateReader();
        using RingReader<long> raw = RingBuffer<long>.Open(name).CreateReader();   // no serializer at all
        WriteBucket(writer, 4, b =>
        {
            b.AddTag(Label("hello"), 1);
            b.AddTag(new SampleRateTag { Rate = 44100 }, 2);
        });

        Assert.True(reader.TryRead(4, out Chunk<long> chunk));
        UnknownTag label = Assert.IsType<UnknownTag>(chunk.Tags.Span[0]);
        Assert.Equal(1ul, label.Offset);
        Assert.Equal("label", label.Key);
        Assert.Equal(typeof(LabelTag).FullName, label.TypeName);
        Assert.False(label.Persistent);
        Assert.Null(label.Error);
        Assert.Equal("hello", JsonDocument.Parse(label.Payload).RootElement.GetProperty("Text").GetString());
        UnknownTag rate = Assert.IsType<UnknownTag>(chunk.Tags.Span[1]);
        Assert.True(rate.Persistent);

        reader.Advance(4);
        Assert.IsType<UnknownTag>(Last(reader, SampleRateTag.TagKey));  // persistence comes from the writer's type
        Assert.True(raw.TryRead(4, out Chunk<long> rawChunk));
        Assert.All(rawChunk.Tags.ToArray(), t => Assert.IsType<UnknownTag>(t));
    }

    [Fact]
    public void PayloadsThatFailToDeserialize_ArriveAsUnknownTagWithTheError()
    {
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, CrossProcess());
        var mismatched = new JsonTagSerializer().Register<IntLabelTag>(typeof(LabelTag).FullName);
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = mismatched });
        using RingReader<long> reader = opened.CreateReader();
        WriteBucket(writer, 2, b => b.AddTag(Label("not a number")));

        Assert.True(reader.TryRead(2, out Chunk<long> chunk));
        UnknownTag tag = Assert.IsType<UnknownTag>(Assert.Single(chunk.Tags.ToArray()));
        Assert.IsType<JsonException>(tag.Error);
        Assert.Contains("not a number", Encoding.UTF8.GetString(tag.Payload.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTagSerializer_Registration()
    {
        var serializer = new JsonTagSerializer();
        Assert.Equal(typeof(LabelTag).FullName, serializer.GetTypeName<LabelTag>());       // registered on first use
        serializer.Register<LabelTag>();                                                    // same name again: fine
        Assert.Throws<InvalidOperationException>(() => serializer.Register<LabelTag>("other"));
        serializer.Register<StateTag>("state");
        Assert.Throws<InvalidOperationException>(() => serializer.Register<SampleRateTag>("state"));
        Assert.Throws<ArgumentException>(() => serializer.Register<ITag>());
        Assert.Null(serializer.Deserialize("nobody", "{}"u8));
        Assert.Equal("state", serializer.GetTypeName<StateTag>());
    }

    // ------------------------------------------------------------------ shared tag memory: rings that wrap, grow, move and shrink

    [Fact]
    public void SharedTags_StayInTheSmallestRing_WhileReadersKeepUp()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        using RingReader<long> reader = setup.CreateReader();
        using RingReader<long> own = setup.Writer.CreateReader();
        var rng = new Random(5);
        for (int round = 0; round < 3000; round++)
        {
            TagPlan.Write(setup.Writer, rng.Next(1, 40), 16, rng);
            foreach (RingReader<long> r in new[] { reader, own })
            {
                long available = r.Available;
                Assert.True(r.TryRead((int)available, out Chunk<long> chunk));
                Assert.Null(TagPlan.CheckChunk(chunk));
                r.Advance((int)available);
                Assert.Null(TagPlan.CheckState(r.ReadLastTagValues(), r.ReadCursor));
            }
        }

        SharedTagLog shared = setup.Shared!;
        Assert.True(shared.End > 20 * TagFormat.MinRingBytes, $"the ring wrapped only {shared.End / TagFormat.MinRingBytes} times");
        Assert.Equal(0, shared.CurrentRing);
        Assert.Equal(0, shared.RingSwitches);
        Assert.Equal(TagFormat.MinRingBytes + Layout.HeaderViewBytes, shared.CommittedBytes);   // ring 0 and the table's first 64 KiB
    }

    [Fact]
    public void SharedTags_GrowWhileAReaderLags_TheWriterNeverWaits_ThenShrinkAfterTheBurst()
    {
        const int Capacity = 1 << 10;
        using var setup = new Setup(ReaderKind.CrossProcessOpener, capacity: Capacity);
        using RingReader<long> reader = setup.CreateReader();
        SharedTagLog shared = setup.Shared!;
        string text = new('g', 2000);

        // the reader reads nothing: a full ring of elements carries 2 MiB of tags, which the writer publishes without waiting
        long written = 0;
        while (written < Capacity)
        {
            WriteBucket(setup.Writer, 16, b =>
            {
                for (int j = 0; j < 16; j++)
                {
                    b.AddTag(Label(text + (b.Cursor + j)), j);
                }
            });
            written += 16;
        }

        int peak = shared.CurrentRing;
        Assert.True(shared.RingSwitches >= 3, $"only {shared.RingSwitches} ring switches");
        Assert.True(TagFormat.RingBytes(peak) >= 512 << 10, $"peak ring {peak}");
        Assert.True(shared.CommittedBytes > 2 << 20);
        Assert.Equal(0, setup.Writer.Counters.KernelWaits);

        // every tag is still there, through every generation
        long read = 0;
        while (read < Capacity)
        {
            int n = (int)Math.Min(100, reader.Available);
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Equal(n, chunk.Tags.Length);
            for (int j = 0; j < n; j++)
            {
                Assert.Equal((ulong)(read + j), chunk.Tags.Span[j].Offset);
                Assert.Equal(text + (read + j), ((LabelTag)chunk.Tags.Span[j]).Text);
            }

            reader.Advance(n);
            read += n;
        }

        // with the reader keeping up, a few ring sizes later the log moves back to a small ring
        for (int round = 0; round < 20_000 && shared.CurrentRing >= peak - 1; round++)
        {
            WriteBucket(setup.Writer, 4, b =>
            {
                for (int j = 0; j < 4; j++)
                {
                    b.AddTag(Label(text), j);
                }
            });
            Assert.True(reader.TryRead(4, out Chunk<long> chunk));
            Assert.Equal(4, chunk.Tags.Length);
            reader.Advance(4);
        }

        Assert.True(shared.CurrentRing < peak - 1, $"still in ring {shared.CurrentRing} (peak {peak})");
        WriteBucket(setup.Writer, 2, b => b.AddTag(Label("after the shrink"), 1));
        Assert.True(reader.TryRead(2, out Chunk<long> last));
        Assert.Equal(["after the shrink"], Texts(last));
        Assert.Equal(shared.CurrentRing, ((SharedTagReader)reader.Tags!).Ring);
    }

    [Fact]
    public void FirstCommit_LargerThanTheSmallestRing_JumpsOutOfItAtPositionZero()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        using RingReader<long> early = setup.CreateReader();
        WriteBucket(setup.Writer, 100, b =>
        {
            for (int j = 0; j < 100; j++)
            {
                b.AddTag(Label(new string('f', 1000) + j), j);             // ~100 KiB: more than ring 0 holds
            }

            b.AddTag(new SampleRateTag { Rate = 3 }, 50);
        });

        SharedTagLog shared = setup.Shared!;
        Assert.Equal(1, shared.RingSwitches);
        Assert.True(shared.CurrentRing > 0);
        Assert.Equal(TagRecordHeader.Bytes, ReadHeader(setup.Writer, ring: 0, physical: 0).RecordBytes);
        Assert.True(ReadHeader(setup.Writer, ring: 0, physical: 0).IsJump);

        Assert.True(early.TryRead(100, out Chunk<long> chunk));
        Assert.Equal(101, chunk.Tags.Length);
        early.Advance(100);
        Assert.Equal(3, ((SampleRateTag)Last(early, SampleRateTag.TagKey)!).Rate);
        using RingReader<long> late = setup.CreateReader();
        Assert.Equal(3, ((SampleRateTag)Last(late, SampleRateTag.TagKey)!).Rate);
    }

    [Fact]
    public void JoiningReader_CopiesTheRecordsAfterTheSnapshot_AcrossAJumpToAnotherRing()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> writer = setup.Writer;
        WriteBucket(writer, 10, b => b.AddTag(new SampleRateTag { Rate = 1 }, 2));   // the snapshot: ring 0

        RingReader<long>? late = null;
        TestHooks.BeforeTagSnapshot = committing =>
        {
            if (ReferenceEquals(committing, writer) && late is null)
            {
                late = setup.CreateReader();                            // this commit's write cursor is published, its snapshot is not
            }
        };

        try
        {
            WriteBucket(writer, 100, b =>
            {
                for (int j = 0; j < 100; j++)
                {
                    b.AddTag(Label(new string('j', 1000)), j);             // more than ring 0 holds: a jump before this commit's records
                }

                b.AddTag(new StateTag { Key = "big", Value = 7 }, 99);
            });
        }
        finally
        {
            TestHooks.BeforeTagSnapshot = null;
        }

        Assert.NotNull(late);
        using (late)
        {
            Assert.Equal(1, setup.Shared!.RingSwitches);
            Assert.Equal(110, late.ReadCursor);
            Assert.Equal(7, ((StateTag)Last(late, "big")!).Value);       // found only by walking from the snapshot in ring 0 through the jump
            Assert.Equal(1, ((SampleRateTag)Last(late, SampleRateTag.TagKey)!).Rate);
            Assert.Equal(setup.Shared.CurrentRing, ((SharedTagReader)late.Tags!).Ring);

            WriteBucket(writer, 5, b => b.AddTag(Label("after"), 1));
            Assert.True(late.TryRead(5, out Chunk<long> chunk));
            Assert.Equal(["after"], Texts(chunk));
        }
    }

    [Fact]
    public void CorruptRecords_AreReportedInsteadOfFollowed()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> writer = setup.Writer;
        using RingReader<long> early = setup.CreateReader();
        WriteBucket(writer, 10, b => b.AddTag(Label("valid"), 1));
        long end = writer.Header->TagEnd;
        byte* ring0 = writer.TagViews!.Ring(0).Address;
        try
        {
            writer.Header->TagEnd = end + TagFormat.ReserveBytes;          // a TagEnd far beyond anything the reserve can hold
            Assert.Throws<RingBufferLayoutException>(() => early.TryRead(10, out _));

            var huge = new TagRecordHeader { RecordBytes = 1 << 20, KeyBytes = 1, TypeNameBytes = 1, PayloadBytes = 2 };
            System.Runtime.InteropServices.MemoryMarshal.Write(new Span<byte>(ring0 + end, TagRecordHeader.Bytes), in huge);
            writer.Header->TagEnd = end + 64;                               // the valid record, then one longer than what can belong to it
            Assert.Throws<RingBufferLayoutException>(() => early.TryRead(10, out _));

            TagRecordHeader jump = TagRecordHeader.Jump(7);                 // a jump into a ring nobody committed
            System.Runtime.InteropServices.MemoryMarshal.Write(new Span<byte>(ring0 + end, TagRecordHeader.Bytes), in jump);
            writer.Header->TagEnd = end + (2 * TagRecordHeader.Bytes);
            Assert.Throws<RingBufferLayoutException>(() => early.Advance(10));   // a fault would crash the process instead
            Assert.Equal(7, ((SharedTagReader)early.Tags!).Ring);

            Assert.Throws<RingBufferLayoutException>(() => setup.CreateReader());   // a joiner walks the same records from the snapshot
            Assert.Equal(1, setup.Opened!.ActiveReaderCount);                  // and gives its slot back,
            Assert.Equal(0, RingTestUtil.Slot(writer, 1).ProcessStartTime);    // without leaving its identity behind for a sweeper
            Assert.Equal(0ul, RingTestUtil.Slot(writer, 1).ClaimTick);
            Assert.Equal(SlotState.Free, SlotWord.State(RingTestUtil.Slot(writer, 1).Word));

            writer.Header->TagEnd = end;
            long snapshotW = writer.Header->TagSnapshotW;
            writer.Header->TagSnapshotW = writer.WriteCursor + 1;             // a snapshot that names a write cursor nobody published
            Assert.Throws<RingBufferLayoutException>(() => setup.CreateReader());
            writer.Header->TagSnapshotW = snapshotW;
        }
        finally
        {
            new Span<byte>(ring0 + end, TagRecordHeader.Bytes).Clear();
            writer.Header->TagEnd = end;
        }
    }

    [Fact]
    public void JoiningReader_WhenTheWriterDiedMidSnapshot_StartsAtTheFinalCursorWithoutState()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> buffer = setup.Writer;
        WriteBucket(buffer, 20, b =>
        {
            b.AddTag(new SampleRateTag { Rate = 9 }, 3);
            b.AddTag(Label("published"), 15);
        });

        buffer.Header->TagVersion |= 1;                                     // a snapshot that never completes ...
        int writerState = buffer.Header->WriterState;
        buffer.Header->WriterState = 2;                                     // ... because the writer is gone
        try
        {
            SharedTagReader tags = SharedTagReader.Join(setup.Opened!.Header, setup.Opened.TagViews!, TagPlan.CreateSerializer(), joinW: 10, out long cursor);
            Assert.Equal(20, cursor);                                       // not 10: the tags of [10, 20) were published before TagEnd, and are skipped with their elements
            Assert.Equal(buffer.Header->TagEnd, tags.Position);
            Assert.Equal(0, tags.LastValues().Length);
            Assert.Equal(long.MaxValue, tags.NextOffset);
        }
        finally
        {
            buffer.Header->TagVersion &= ~1UL;
            buffer.Header->WriterState = writerState;
        }
    }

    [Fact]
    public void JoiningReader_WhenTheWriterClosesWhileItRetries_StillCopiesTheState()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> writer = setup.Writer;
        WriteBucket(writer, 10, b => b.AddTag(new SampleRateTag { Rate = 7 }, 3));
        ulong version = writer.Header->TagVersion;
        int writerState = writer.Header->WriterState;
        writer.Header->TagVersion = version | 1;                            // a snapshot in progress: the joiner retries and checks on the writer
        try
        {
            Task<SharedTagReader> join = Task.Run(() => SharedTagReader.Join(setup.Opened!.Header, setup.Opened.TagViews!, setup.Serializer, joinW: 10, out _));
            Thread.Sleep(50);
            Assert.False(join.IsCompleted);                                  // a live writer's odd version: keep retrying
            writer.Header->TagVersion = version;                            // the snapshot completes ...
            writer.Header->WriterState = 2;                                 // ... and the writer closes
            Assert.True(join.Wait(RingTestUtil.Long));
            Assert.Equal(7, ((SampleRateTag)TagPlan.Find(join.Result.LastValues(), SampleRateTag.TagKey)!).Rate);   // not the no-state fallback of a dead writer
        }
        finally
        {
            writer.Header->TagVersion = version;
            writer.Header->WriterState = writerState;
        }
    }

    [Fact]
    public void SharedTags_ADeadReaderIsEvicted_BeforeTheLogGrowsForIt()
    {
        using var setup = new Setup(ReaderKind.CrossProcessOpener);
        RingBuffer<long> writer = setup.Writer;
        ref ReaderSlot dead = ref RingTestUtil.Slot(writer, 5);                 // our own PID with a wrong start time: a process that is gone
        dead.ProcessStartTime = 1;
        dead.ReadCursor = 0;
        Volatile.Write(ref dead.Word, SlotWord.Make(SlotState.Active, 7, Environment.ProcessId));
        Interlocked.Or(ref writer.Header->ActiveMask, 1UL << 5);

        string text = new('d', 1000);
        for (int i = 0; i < 1000; i++)                                          // ~1 MiB of tags: ring 0 many times over
        {
            WriteBucket(writer, 1, b => b.AddTag(Label(text)));
        }

        Assert.Equal(1, writer.EvictedReaders);
        Assert.Equal(SlotState.Free, SlotWord.State(Volatile.Read(ref dead.Word)));
        Assert.Equal(0, setup.Shared!.RingSwitches);                            // evicted when ring 0 first filled, instead of growing for it
        Assert.Equal(TagFormat.MinRingBytes, setup.Shared.CommittedBytes);
    }

    [Theory]
    [InlineData(TagMode.InProcess)]
    [InlineData(TagMode.CrossProcess)]
    public void Appends_AllocateNothing_OnceTheCommitIsPrepared(TagMode mode)
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = mode, TagSerializer = TagPlan.CreateSerializer() });
        TagWriter tags = buffer.TagWriter!;
        using Bucket<long> bucket = buffer.GetBucket(600);
        for (int j = 0; j < 600; j++)                                           // more than a chunk of the object log, new keys, a key twice, sizes that change
        {
            bucket.AddTag(new StateTag { Key = "k" + (j % 300), Value = j * 1_000_003L }, j);
            bucket.AddTag(Label(new string('a', j % 50)), j);
        }

        // what PublishTags does: select, prepare (may fail), then the appends that must not
        Assert.Equal(1200, tags.SelectForCommit(600));
        tags.PrepareLocal();
        tags.Shared?.Prepare(tags.SelectedBytes, tags.StateChanges, appendW: 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        tags.AppendSelected(appendW: 0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        tags.ClearPending(keepSticky: true);
        bucket.Commit(600);

        using RingReader<long> reader = buffer.CreateReader();                 // a joiner finds the state the appends built
        Assert.Equal(300, reader.ReadLastTagValues().Length);
        Assert.Equal(599 * 1_000_003L, ((StateTag)Last(reader, "k299")!).Value);
    }

    // ------------------------------------------------------------------ the optional limit

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void MaxUnreadTags_MakesTheCommitWaitUntilTheReaderReadsPastOlderTags(ReaderKind kind)
    {
        const int Limit = 16;
        using var setup = new Setup(kind, maxUnreadTags: Limit);
        using RingReader<long> reader = setup.CreateReader();
        Assert.Equal((Limit, 0L), setup.Writer.MaxUnreadTags);
        int committed = 0;
        var writer = Task.Factory.StartNew(
            () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    WriteBucket(setup.Writer, 1, b => b.AddTag(Label("tag " + i)));
                    Interlocked.Increment(ref committed);
                }
            },
            TaskCreationOptions.LongRunning);

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(setup.Writer), "the writer waits for tag room");
        int stalledAt = Volatile.Read(ref committed);
        Assert.InRange(stalledAt, Limit, Limit + 2);                    // the tags of the elements the reader has not read
        Assert.False(writer.IsCompleted);

        long read = 0;
        while (read < 100)
        {
            Assert.True(reader.WaitSync(1, RingTestUtil.Long), $"status {reader.Status}");
            int n = (int)reader.Available;
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Equal(n, chunk.Tags.Length);
            reader.Advance(n);
            read += n;
        }

        Assert.True(writer.Wait(RingTestUtil.Long));
        Assert.True(setup.Writer.Counters.TagWaits > 0);
        Assert.Equal(100, setup.Writer.WriteCursor);
    }

    [Fact]
    public void MaxUnreadTagBytes_MakesTheCommitWaitOnTheRecordBytes()
    {
        const int Limit = 8 * 1024;
        using var setup = new Setup(ReaderKind.CrossProcessOpener, maxUnreadTagBytes: Limit);
        using RingReader<long> reader = setup.CreateReader();
        string text = new('b', 500);                                    // records of about 600 bytes: ~13 fit
        int committed = 0;
        var writer = Task.Factory.StartNew(
            () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    WriteBucket(setup.Writer, 1, b => b.AddTag(Label(text)));
                    Interlocked.Increment(ref committed);
                }
            },
            TaskCreationOptions.LongRunning);

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(setup.Writer), "the writer waits for tag room");
        Assert.InRange(Volatile.Read(ref committed), 5, 20);
        Assert.True(setup.Shared!.UnreadBytes <= Limit);

        long read = 0;
        while (read < 100)
        {
            Assert.True(reader.WaitSync(1, RingTestUtil.Long), $"status {reader.Status}");
            int n = (int)reader.Available;
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Equal(n, chunk.Tags.Length);
            reader.Advance(n);
            read += n;
        }

        Assert.True(writer.Wait(RingTestUtil.Long));
        Assert.True(setup.Writer.Counters.TagWaits > 0);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void MaxUnreadTags_NeverWaits_WhileTheReadersKeepUp(ReaderKind kind)
    {
        using var setup = new Setup(kind, maxUnreadTags: 64);
        using RingReader<long> reader = setup.CreateReader();
        for (int i = 0; i < 500; i++)
        {
            WriteBucket(setup.Writer, 2, b => b.AddTag(Label("keeping up"), 1));
            Assert.True(reader.TryRead(2, out Chunk<long> chunk));
            Assert.Single(chunk.Tags.ToArray());
            reader.Advance(2);
        }

        Assert.Equal(0, setup.Writer.Counters.TagWaits);
        Assert.Equal(0, setup.Writer.Counters.KernelWaits);

        // and with no readers at all, nothing is ever unread
        using var alone = new Setup(kind, maxUnreadTags: 4);
        for (int i = 0; i < 100; i++)
        {
            WriteBucket(alone.Writer, 1, b => b.AddTag(Label("nobody reads")));
        }

        Assert.Equal(0, alone.Writer.Counters.TagWaits);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void MaxUnreadTags_ACommitThatExceedsTheLimitAlone_IsDroppedWithAnError(ReaderKind kind)
    {
        using var setup = new Setup(kind, maxUnreadTags: 4);
        using RingReader<long> reader = setup.CreateReader();
        WriteBucket(setup.Writer, 1, b => b.AddTag(Label("fits")));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WriteBucket(setup.Writer, 10, b =>
        {
            for (int j = 0; j < 10; j++)
            {
                b.AddTag(Label("too many " + j), j);
            }
        }));

        Assert.Contains("MaxUnreadTags", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, setup.Writer.WriteCursor);                      // the bucket was dropped
        WriteBucket(setup.Writer, 1, b => b.AddTag(Label("still usable")));
        Assert.True(reader.TryRead(2, out Chunk<long> chunk));
        Assert.Equal(["fits", "still usable"], Texts(chunk));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void MaxUnreadTags_ReleasesWholeChunksOfTheObjectLog_AlsoAtItsEnd(ReaderKind kind)
    {
        // A limit of exactly one chunk makes the release land on a full chunk whose successor is not linked yet.
        using var setup = new Setup(kind, maxUnreadTags: LocalTagLog.ChunkSize);
        using RingReader<long> reader = setup.CreateReader();
        for (int i = 0; i < 4 * LocalTagLog.ChunkSize; i++)
        {
            WriteBucket(setup.Writer, 1, b => b.AddTag(Label("tag " + i)));
            Assert.True(reader.TryRead(1, out Chunk<long> chunk));
            Assert.Equal("tag " + i, ((LabelTag)Assert.Single(chunk.Tags.ToArray())).Text);
            reader.Advance(1);                                      // the reader keeps up: every release frees whole chunks
        }

        Assert.Equal(0, setup.Writer.Counters.TagWaits);
        Assert.Equal(4 * LocalTagLog.ChunkSize, setup.Writer.WriteCursor);
    }

    [Fact]
    public void Dispose_EndsACommitThatWaitsForTagRoom()
    {
        var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions
        {
            Tags = TagMode.CrossProcess,
            TagSerializer = TagPlan.CreateSerializer(),
            MaxUnreadTags = 8,
        });
        RingReader<long> reader = buffer.CreateReader();
        var writer = Task.Factory.StartNew(
            () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    WriteBucket(buffer, 1, b => b.AddTag(Label("tag " + i)));
                }
            },
            TaskCreationOptions.LongRunning);

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "the writer waits for tag room");
        buffer.Dispose();
        AggregateException ex = Assert.Throws<AggregateException>(() => writer.Wait(RingTestUtil.Long));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        reader.Dispose();
    }

    [Fact]
    public void TagLimits_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess, MaxUnreadTags = -1 }));
        ArgumentException noTags = Assert.Throws<ArgumentException>(() => RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { MaxUnreadTags = 8 }));
        Assert.Contains("RingBufferOptions.Tags", noTags.Message, StringComparison.Ordinal);
        ArgumentException inProcessBytes = Assert.Throws<ArgumentException>(
            () => RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess, MaxUnreadTagBytes = 4096 }));
        Assert.Contains("MaxUnreadTagBytes needs TagMode.CrossProcess", inProcessBytes.Message, StringComparison.Ordinal);

        using var unlimited = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess });
        Assert.Equal((0L, 0L), unlimited.MaxUnreadTags);
    }

    // ------------------------------------------------------------------ dispose, pool, memory

    [Fact]
    public void Dispose_DuringACommitWithTags_LetsTheCommitComplete()
    {
        var buffer = RingBuffer<long>.Create(1 << 16, options: CrossProcess());
        RingReader<long> reader = buffer.CreateReader();
        Task? disposer = null;
        TestHooks.BeforeTagAppend = committing =>
        {
            if (!ReferenceEquals(committing, buffer))
            {
                return;                                                     // another test's buffer
            }

            disposer = Task.Run(buffer.Dispose);                            // lands after the tag memory is prepared, before the tags are published
            SpinWait.SpinUntil(() => buffer.IsWriterClosed, RingTestUtil.Long);
            Thread.Sleep(20);                                               // every chance to drop the bucket under the commit: it must wait instead
        };

        try
        {
            WriteBucket(buffer, 10, b => b.AddTag(Label("committed while disposing"), 2));
        }
        finally
        {
            TestHooks.BeforeTagAppend = null;
        }

        Assert.NotNull(disposer);
        Assert.True(disposer.Wait(RingTestUtil.Long));
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["committed while disposing"], Texts(chunk));
        reader.Advance(10);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        Assert.True(reader.IsCompleted);
        reader.Dispose();
    }

    [Fact]
    public void Pool_ReusesOnlySectionsWithTheSameTagReserve_AndCountsCommittedTagMemory()
    {
        using var pool = new RingBufferPool();
        long expected;
        using (var buffer = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool)))
        {
            WriteBucket(buffer, 10, b => b.AddTag(new SampleRateTag { Rate = 1 }));
            expected = buffer.DataBytes + buffer.TagWriter!.Shared!.CommittedBytes;
            Assert.Equal(TagFormat.MinRingBytes + Layout.HeaderViewBytes, buffer.TagWriter.Shared.CommittedBytes);
        }

        Assert.Equal(expected, pool.IdleBytes);
        using (RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Pool = pool }))
        {
            Assert.Equal(0, pool.ReusedCount);                              // no tags: a section without a tag reserve
        }

        using (RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess, Pool = pool }))
        {
            Assert.Equal(1, pool.ReusedCount);                              // in-process tags need no reserve either: the plain section returned above
        }

        using (RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool)))
        {
            Assert.Equal(2, pool.ReusedCount);                              // the section with the reserve
        }

        long union;
        using (var reuse = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool)))
        {
            Assert.Equal(3, pool.ReusedCount);
            WriteBucket(reuse, 100, b =>
            {
                for (int j = 0; j < 100; j++)
                {
                    b.AddTag(Label(new string('u', 1000)), j);              // no table, but a larger ring than the first buffer used
                }
            });
            SharedTagLog shared = reuse.TagWriter!.Shared!;
            Assert.True(shared.CurrentRing > 0);
            union = reuse.DataBytes + Layout.HeaderViewBytes;               // the first buffer's table ...
            foreach (long ring in shared.RingCommitted)
            {
                union += ring;                                              // ... and every ring this one committed (ring 0 is in both)
            }
        }

        Assert.Equal(union, pool.IdleBytes - ((1L << 16) * 8));             // the committed memory of both buffers; the plain section is idle too

        using var bounded = new RingBufferPool(new RingBufferPoolOptions { MaxIdleBytes = expected - 1 });
        using (var buffer = RingBuffer<long>.Create(1 << 16, options: CrossProcess(bounded)))
        {
            WriteBucket(buffer, 10, b => b.AddTag(new SampleRateTag { Rate = 1 }));
        }

        Assert.Equal(0, bounded.IdleCount);                                 // larger than the bound with its tag memory: released at once
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PooledSection_Reuse_ForgetsThePreviousBuffersTags(bool clearOnReuse)
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { ClearOnReuse = clearOnReuse });
        using (var first = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool)))
        {
            WriteBucket(first, 10, b =>
            {
                b.AddTag(new SampleRateTag { Rate = 1 });
                b.AddTag(Label("old"), 5);
            });
        }

        Assert.Equal(1, pool.IdleCount);
        using var second = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool));
        Assert.Equal(1, pool.ReusedCount);
        byte* ring0 = second.TagViews!.Ring(0).Address;                     // committed by the first buffer: it stays committed
        byte* table = second.TagViews.Table(Layout.HeaderViewBytes).Address;
        Assert.Equal(clearOnReuse, new ReadOnlySpan<byte>(ring0, (int)TagFormat.MinRingBytes).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(clearOnReuse, new ReadOnlySpan<byte>(table, (int)Layout.HeaderViewBytes).IndexOfAnyExcept((byte)0) < 0);

        using RingReader<long> reader = second.CreateReader();
        Assert.Equal(0, reader.ReadLastTagValues().Length);
        WriteBucket(second, 10, b => b.AddTag(Label("new"), 5));
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["new"], Texts(chunk));
    }

    [Fact]
    public void PooledSection_ClearOnReuse_TrustsOnlyItsOwnRecordOfTheCommittedTagMemory()
    {
        using var pool = new RingBufferPool(new RingBufferPoolOptions { ClearOnReuse = true });
        using (var first = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool)))
        {
            WriteBucket(first, 10, b => b.AddTag(Label("old")));
            for (int ring = 1; ring < TagFormat.RingClasses; ring++)
            {
                first.Header->TagRingCommitted[ring] = TagFormat.RingBytes(ring);   // what any process that maps the section could store
            }

            first.Header->TagTableCommitted = TagFormat.TableReserveBytes;
        }

        using var second = RingBuffer<long>.Create(1 << 16, options: CrossProcess(pool));   // zeroes only ring 0 (a fault would end the test process)
        Assert.Equal(1, pool.ReusedCount);
        Assert.True(new ReadOnlySpan<byte>(second.TagViews!.Ring(0).Address, (int)TagFormat.MinRingBytes).IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void TagsEnabledButUnused_TheHotPathDoesNotAllocate(ReaderKind kind)
    {
        using var setup = new Setup(kind);
        using RingReader<long> reader = setup.CreateReader();
        for (int i = 0; i < 1000; i++)
        {
            RunRound(setup.Writer, reader);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            RunRound(setup.Writer, reader);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void RunRound(RingBuffer<long> buffer, RingReader<long> reader)
        {
            using (Bucket<long> b = buffer.GetBucket(16))
            {
                b.Commit(16);
            }

            reader.TryRead(16, out Chunk<long> chunk);
            if (!chunk.Tags.IsEmpty)
            {
                throw new InvalidOperationException("unexpected tags");
            }

            reader.Advance(16);
        }
    }

    [Fact]
    public void InProcessTags_BecomeGarbageOnceReadersPassThem()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { Tags = TagMode.InProcess });
        using RingReader<long> reader = buffer.CreateReader();
        WeakReference first = WriteTracked(buffer);
        for (int i = 0; i < 4 * LocalTagLog.ChunkSize; i++)
        {
            WriteBucket(buffer, 1, b => b.AddTag(Label("filler")));
            Assert.True(reader.TryRead(1, out _));
            reader.Advance(1);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(first.IsAlive, "the object log still holds a tag every reader has passed");
        Assert.True(reader.ReadCursor > LocalTagLog.ChunkSize);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference WriteTracked(RingBuffer<long> buffer)
        {
            var tag = new LabelTag { Text = "tracked" };
            WriteBucket(buffer, 1, b => b.AddTag(tag));
            return new WeakReference(tag);
        }
    }

    // ------------------------------------------------------------------ concurrency

    [Theory]
    [InlineData(TagMode.InProcess)]
    [InlineData(TagMode.CrossProcess)]
    public void Fuzz_ConcurrentWriterReadersAndJoiners_MatchThePlan(TagMode mode)
    {
        const long Total = 300_000;
        string name = TestNames.UniqueSection();
        using var buffer = RingBuffer<long>.Create(1 << 12, name, new RingBufferOptions { Tags = mode, TagSerializer = TagPlan.CreateSerializer() });
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        RingBuffer<long> openerOrWriter = mode == TagMode.CrossProcess ? opened : buffer;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim(false);

        Task Reader(RingBuffer<long> from, int seed, long joinAt) => Task.Factory.StartNew(() =>
        {
            if (joinAt > 0)
            {
                started.Wait();
                SpinWait.SpinUntil(() => buffer.WriteCursor >= joinAt);   // join mid-stream, after persistent tags were written and released
            }

            using RingReader<long> reader = from.CreateReader();
            started.Set();
            var rng = new Random(seed);
            if (TagPlan.CheckState(reader.ReadLastTagValues(), reader.ReadCursor) is string joinError)
            {
                errors.Enqueue("join: " + joinError);
                return;
            }

            while (true)
            {
                if (!reader.WaitSync(1, RingTestUtil.Long))
                {
                    if (reader.IsCompleted)
                    {
                        return;
                    }

                    errors.Enqueue("wait failed: " + reader.Status);
                    return;
                }

                int n = (int)Math.Min(reader.Available, rng.Next(1, 300));
                if (!reader.TryRead(n, out Chunk<long> chunk))
                {
                    errors.Enqueue("TryRead false");
                    return;
                }

                if (TagPlan.CheckChunk(chunk) is string chunkError)
                {
                    errors.Enqueue(chunkError);
                    return;
                }

                reader.Advance(rng.Next(3) == 0 ? rng.Next(1, n + 1) : n);
                if (TagPlan.CheckState(reader.ReadLastTagValues(), reader.ReadCursor) is string stateError)
                {
                    errors.Enqueue(stateError);
                    return;
                }

                if (rng.Next(20_000) == 0)
                {
                    Thread.Sleep(5);                                    // lag now and then: the shared tags grow and shrink behind the writer
                }
            }
        }, TaskCreationOptions.LongRunning);

        Task[] readers =
        [
            Reader(buffer, 1, 0), Reader(openerOrWriter, 2, 0), Reader(openerOrWriter, 3, Total / 3), Reader(buffer, 4, Total / 2), Reader(openerOrWriter, 5, 2 * Total / 3),
        ];
        started.Wait();
        long written = TagPlan.Write(buffer, Total, 200, new Random(11));
        buffer.Dispose();
        Assert.True(Task.WaitAll(readers, RingTestUtil.Long));
        Assert.Equal(Total, written);
        Assert.Empty(errors);
    }

    private static TagRecordHeader ReadHeader(RingBuffer<long> buffer, int ring, long physical)
        => System.Runtime.InteropServices.MemoryMarshal.Read<TagRecordHeader>(new ReadOnlySpan<byte>(buffer.TagViews!.Ring(ring).Address + physical, TagRecordHeader.Bytes));

    /// <summary>A serializer that counts what goes through it.</summary>
    private sealed class CountingSerializer(ITagSerializer inner) : ITagSerializer
    {
        private int _serialized;
        private int _deserialized;

        public int Serialized => Volatile.Read(ref _serialized);

        public int Deserialized => Volatile.Read(ref _deserialized);

        public string GetTypeName<TTag>() where TTag : ITag => inner.GetTypeName<TTag>();

        public void Serialize<TTag>(TTag tag, IBufferWriter<byte> destination) where TTag : ITag
        {
            Interlocked.Increment(ref _serialized);
            inner.Serialize(tag, destination);
        }

        public ITag? Deserialize(string typeName, ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref _deserialized);
            return inner.Deserialize(typeName, payload);
        }
    }
}

/// <summary>A tag whose Text is a number: a LabelTag payload does not deserialize into it.</summary>
public sealed class IntLabelTag : ITag
{
    public ulong Offset { get; set; }

    public string Key { get; set; } = "";

    public int Text { get; set; }
}

/// <summary>A tag that leaves its offset out of the JSON: readers set it from the record.</summary>
public sealed class QuietOffsetTag : ITag
{
    [JsonIgnore]
    public ulong Offset { get; set; }

    public string Key => "quiet";

    public int N { get; set; }
}
