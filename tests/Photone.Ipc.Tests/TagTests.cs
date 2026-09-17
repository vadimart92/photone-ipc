using System.Text;
using System.Text.Json;
using Photone.Ipc.Internal;
using Photone.Ipc.TestChild;

namespace Photone.Ipc.Tests;

/// <summary>Stream tags (DESIGN §16): delivery with the elements, ordering, persistence, joining readers, the tag log's capacity and waits.</summary>
public sealed unsafe class TagTests
{
    /// <summary>AddTag on a bucket, returning what it threw (a ref struct cannot be captured by an Assert.Throws lambda).</summary>
    private static Exception? AddTagError<TTag>(Bucket<long> bucket, TTag tag) where TTag : ITag
    {
        try
        {
            bucket.AddTag(tag);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static RingBufferOptions TagOptions(long tagCapacity = 1 << 16, int persistentCapacity = 16 << 10, ITagSerializer? serializer = null)
        => new() { TagCapacity = tagCapacity, PersistentTagCapacity = persistentCapacity, TagSerializer = serializer ?? TagPlan.CreateSerializer() };

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

    private static LabelTag Label(long offset, string text) => new() { Offset = (ulong)offset, Text = text };

    private static string[] Texts(Chunk<long> chunk) => chunk.Tags.ToArray().Select(t => ((LabelTag)t).Text).ToArray();

    // ------------------------------------------------------------------ configuration

    [Fact]
    public void WithoutTagCapacity_TheBufferCarriesNoTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(0, buffer.TagCapacity);
        Assert.Equal(0, buffer.PersistentTagCapacity);

        using (Bucket<long> bucket = buffer.GetBucket(4))
        {
            InvalidOperationException ex = Assert.IsType<InvalidOperationException>(AddTagError(bucket, Label(bucket.Cursor, "x")));
            Assert.Contains("TagCapacity", ex.Message, StringComparison.Ordinal);
            bucket.Commit(4);
        }

        Assert.True(reader.TryRead(4, out Chunk<long> chunk));
        Assert.True(chunk.Tags.IsEmpty);
        Assert.Empty(reader.ReadLastTagValues());
        Assert.Null(reader.Tags);
    }

    [Fact]
    public void WithoutSerializer_TheWriterCannotAddTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { TagCapacity = 1 << 16 });
        using Bucket<long> bucket = buffer.GetBucket(4);
        InvalidOperationException ex = Assert.IsType<InvalidOperationException>(AddTagError(bucket, Label(bucket.Cursor, "x")));
        Assert.Contains("TagSerializer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 16 << 10, 4096, 60 << 10)]
    [InlineData(4096, 0, 4096, 61440)]
    [InlineData(5000, 16 << 10, 8192, 57344)]
    [InlineData(1 << 16, 0, 1 << 16, 0)]
    [InlineData(1 << 16, 1, 1 << 16, 1 << 16)]
    [InlineData(1 << 20, 16 << 10, 1 << 20, 1 << 16)]
    public void TagArea_IsAPowerOfTwoLogPlusATableRoundedTo64KiB(long tagCapacity, int persistent, long expectedLog, int expectedState)
    {
        (long log, int state) = TagFormat.ChooseArea(tagCapacity, persistent);
        Assert.Equal(expectedLog, log);
        Assert.Equal(expectedState, state);
        Assert.True(TagFormat.IsValidArea(log, state));

        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity, persistent));
        Assert.Equal(expectedLog, buffer.TagCapacity);
        Assert.Equal(expectedState, buffer.PersistentTagCapacity);
        Assert.Equal(Layout.HeaderViewBytes + expectedState + expectedLog, buffer.Header->DataOffset);
    }

    [Fact]
    public void TagCapacity_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: (1L << 30) + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RingBuffer<long>.Create(1 << 16, options: TagOptions(persistentCapacity: -1)));
    }

    // ------------------------------------------------------------------ delivery

    [Fact]
    public void Tags_ArriveWithTheirElements()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 100, b =>
        {
            b.AddTag(Label(b.Cursor, "first"));
            b.AddTag(Label(b.Cursor + 10, "ten"));
            b.AddTag(Label(b.Cursor + 99, "last"));
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
    }

    [Fact]
    public void PartiallyAdvancedChunk_KeepsTheTagsItStillCovers()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 10, b =>
        {
            b.AddTag(Label(b.Cursor + 1, "one"));
            b.AddTag(Label(b.Cursor + 5, "five"));
        });

        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        ReadOnlyMemory<ITag> tags = chunk.Tags;
        reader.Advance(3);                                              // past "one", not past "five"
        for (int i = 0; i < 20; i++)
        {
            WriteBucket(buffer, 10, b => b.AddTag(Label(b.Cursor, "later")));   // more tags join the reader's queue (and grow it)
        }

        Assert.True(reader.TryRead(207, out Chunk<long> next));
        Assert.Equal(21, next.Tags.Length);
        Assert.Equal("five", ((LabelTag)tags.Span[1]).Text);           // the first chunk's view of what it still covers is intact
        Assert.NotNull(tags.Span[0]);                                   // and a tag it was advanced past is not pulled out from under it
        Assert.Equal("five", ((LabelTag)next.Tags.Span[0]).Text);
    }

    [Fact]
    public void Tags_AddedInAnyOrder_ArriveInOffsetOrder_EqualOffsetsInAddOrder()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 10, b =>
        {
            b.AddTag(Label(b.Cursor + 5, "b"));
            b.AddTag(Label(b.Cursor + 2, "a"));
            b.AddTag(Label(b.Cursor + 5, "c"));
            b.AddTag(Label(b.Cursor + 9, "e"));
            b.AddTag(Label(b.Cursor + 5, "d"));
        });

        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["a", "b", "c", "d", "e"], Texts(chunk));
    }

    [Fact]
    public void CommitPrefix_DropsTheTagsOfTheDroppedElements()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 10, b =>
        {
            b.AddTag(Label(b.Cursor + 3, "kept"));
            b.AddTag(Label(b.Cursor + 5, "dropped at 5"));
            b.AddTag(Label(b.Cursor + 8, "dropped at 8"));
        }, commit: 5);
        WriteBucket(buffer, 5, b => b.AddTag(Label(b.Cursor, "second bucket at 5")));
        WriteBucket(buffer, 3, b => b.AddTag(Label(b.Cursor, "dropped entirely")), commit: 0);

        Assert.Equal(10, buffer.WriteCursor);
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["kept", "second bucket at 5"], Texts(chunk));
        Assert.Equal([3ul, 5ul], chunk.Tags.ToArray().Select(t => t.Offset));
    }

    [Fact]
    public void DisposedBucket_PublishesNoTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        using (Bucket<long> bucket = buffer.GetBucket(4))
        {
            bucket.AddTag(Label(bucket.Cursor, "never"));
        }

        WriteBucket(buffer, 4, b => b.AddTag(Label(b.Cursor + 1, "published")));
        Assert.True(reader.TryRead(4, out Chunk<long> chunk));
        Assert.Equal(["published"], Texts(chunk));
        Assert.Equal(0, buffer.TagWriter!.PendingCount);
    }

    [Fact]
    public void AddTag_Validation()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        WriteBucket(buffer, 7);
        using (Bucket<long> bucket = buffer.GetBucket(10))
        {
            Assert.IsType<ArgumentOutOfRangeException>(AddTagError(bucket, Label(bucket.Cursor - 1, "before")));
            Assert.IsType<ArgumentOutOfRangeException>(AddTagError(bucket, Label(bucket.Cursor + 10, "after")));
            Assert.IsType<ArgumentException>(AddTagError<ITag>(bucket, Label(bucket.Cursor, "interface")));
            Assert.IsType<ArgumentException>(AddTagError(bucket, new LabelTag { Offset = bucket.StartOffset, Key = null!, Text = "null key" }));
            Assert.IsType<ArgumentException>(AddTagError(bucket, Label(bucket.Cursor, new string('x', 5000))));   // alone larger than the 4 KiB log

            int accepted = 0;
            Exception? full = null;
            for (int i = 0; i < 100 && full is null; i++)
            {
                full = AddTagError(bucket, Label(bucket.Cursor + (i % 10), new string('y', 60)));   // ~160 bytes each
                accepted += full is null ? 1 : 0;
            }

            Assert.IsType<InvalidOperationException>(full);                // the bucket's tags exceed the log
            Assert.InRange(accepted, 20, 30);
            Assert.Equal(accepted, buffer.TagWriter!.PendingCount);        // the failed one left nothing behind
            bucket.Commit(10);
            Assert.IsType<InvalidOperationException>(AddTagError(bucket, Label(bucket.Cursor, "after commit")));
        }

        using RingReader<long> reader = buffer.CreateReader();
        using (Bucket<long> bucket = buffer.GetBucket(1))
        {
            bucket.AddTag(Label(bucket.Cursor, "fine"));
            bucket.Commit(1);
        }

        Assert.True(reader.TryRead(1, out Chunk<long> chunk));
        Assert.Equal(["fine"], Texts(chunk));
    }

    [Fact]
    public void StaleBucketCopy_CannotTagTheNextBucket()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        Bucket<long> first = buffer.GetBucket(4);
        Bucket<long> copy = first;                                      // a copy keeps _committed = -1 after the original commits
        first.Commit(4);
        using Bucket<long> second = buffer.GetBucket(4);
        Assert.IsType<InvalidOperationException>(AddTagError(copy, Label(copy.Cursor, "stale")));
        second.Commit(0);
    }

    // ------------------------------------------------------------------ persistent tags

    [Fact]
    public void LastTagValues_FollowTheReaderPosition()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        WriteBucket(buffer, 100, b =>
        {
            b.AddTag(new SampleRateTag { Offset = b.StartOffset, Rate = 1000 });
            b.AddTag(Label(b.Cursor + 20, "not persistent"));
            b.AddTag(new SampleRateTag { Offset = b.StartOffset + 50, Rate = 2000 });
        });

        Assert.Empty(reader.ReadLastTagValues());
        Assert.True(reader.TryRead(100, out Chunk<long> chunk));
        Assert.Equal(3, chunk.Tags.Length);
        Assert.Empty(reader.ReadLastTagValues());                      // reading is not passing

        reader.Advance(1);
        Assert.Equal(1000, Assert.IsType<SampleRateTag>(Assert.Single(reader.ReadLastTagValues()).Value).Rate);
        reader.Advance(49);                                             // at 50: the second rate applies to element 50, not before it
        Assert.Equal(1000, ((SampleRateTag)reader.ReadLastTagValues()[SampleRateTag.TagKey]).Rate);
        reader.Advance(1);
        IReadOnlyDictionary<string, ITag> state = reader.ReadLastTagValues();
        Assert.Equal(2000, ((SampleRateTag)state[SampleRateTag.TagKey]).Rate);
        Assert.False(state.ContainsKey("label"));
    }

    [Fact]
    public void JoiningReader_StartsWithTheLastPersistentTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        WriteBucket(buffer, 10, b =>
        {
            b.AddTag(new SampleRateTag { Offset = b.StartOffset + 1, Rate = 1 });
            b.AddTag(new StateTag { Offset = b.StartOffset + 2, Key = "mode", Value = 7 });
            b.AddTag(Label(b.Cursor + 3, "before join"));
        });
        WriteBucket(buffer, 10, b => b.AddTag(new SampleRateTag { Offset = b.StartOffset + 5, Rate = 2 }));

        using RingReader<long> late = buffer.CreateReader();
        Assert.Equal(20, late.ReadCursor);
        IReadOnlyDictionary<string, ITag> state = late.ReadLastTagValues();
        Assert.Equal(2, state.Count);
        Assert.Equal(2, ((SampleRateTag)state[SampleRateTag.TagKey]).Rate);
        Assert.Equal(7, ((StateTag)state["mode"]).Value);

        WriteBucket(buffer, 10, b =>
        {
            b.AddTag(Label(b.Cursor, "after join"));
            b.AddTag(new StateTag { Offset = b.StartOffset + 4, Key = "mode", Value = 8 });
        });

        Assert.True(late.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(2, chunk.Tags.Length);
        Assert.Equal("after join", ((LabelTag)chunk.Tags.Span[0]).Text);
        late.Advance(10);
        Assert.Equal(8, ((StateTag)late.ReadLastTagValues()["mode"]).Value);
        Assert.Equal(2, ((SampleRateTag)late.ReadLastTagValues()[SampleRateTag.TagKey]).Rate);
    }

    [Fact]
    public void JoiningReader_StartsAtTheSnapshotWhenItIsNewerThanTheCursorItJoinedAt()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        WriteBucket(buffer, 30, b => b.AddTag(new SampleRateTag { Offset = b.StartOffset + 20, Rate = 5 }));
        WriteBucket(buffer, 30, b => b.AddTag(Label(b.Cursor + 1, "at 31")));

        // a reader that loaded W = 10 before the writer published up to 60: the table already holds the rate at 20, so it must start at 60
        TagReader tags = TagReader.Join(buffer.Header, buffer.TagState, buffer.PersistentTagCapacity, buffer.TagLog, buffer.TagCapacity, TagPlan.CreateSerializer(), joinW: 10, out long cursor);
        Assert.Equal(60, cursor);
        Assert.Equal(5, ((SampleRateTag)tags.LastValues()[SampleRateTag.TagKey]).Rate);
        Assert.Equal(long.MaxValue, tags.NextOffset);

        // a reader that joined at the snapshot's cursor starts there, with nothing queued
        TagReader current = TagReader.Join(buffer.Header, buffer.TagState, buffer.PersistentTagCapacity, buffer.TagLog, buffer.TagCapacity, null, joinW: 60, out long at);
        Assert.Equal(60, at);
        Assert.IsType<UnknownTag>(current.LastValues()[SampleRateTag.TagKey]);   // no serializer here
    }

    [Fact]
    public void PersistentTable_Overflow_IsReportedByAddTag()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 1 << 16, persistentCapacity: 0));
        Assert.Equal(0, buffer.PersistentTagCapacity);
        using Bucket<long> bucket = buffer.GetBucket(4);
        bucket.AddTag(Label(bucket.Cursor, "non-persistent tags need no table"));
        InvalidOperationException ex = Assert.IsType<InvalidOperationException>(AddTagError(bucket, new SampleRateTag { Offset = bucket.StartOffset, Rate = 1 }));
        Assert.Contains("PersistentTagCapacity", ex.Message, StringComparison.Ordinal);
        bucket.Commit(4);
    }

    [Fact]
    public void PersistentTable_ReusesTheSpaceOfAKey()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096, persistentCapacity: 0));   // table = 60 KiB of slack
        int stateBytes = buffer.PersistentTagCapacity;
        for (int i = 0; i < 2000; i++)
        {
            WriteBucket(buffer, 1, b => b.AddTag(new StateTag { Offset = b.StartOffset, Key = "only", Value = i }));
        }

        Assert.True(buffer.Header->TagStateUsed < stateBytes);
        Assert.Equal(1, buffer.Header->TagStateCount);
        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(1999, ((StateTag)reader.ReadLastTagValues()["only"]).Value);
    }

    // ------------------------------------------------------------------ serializers

    [Fact]
    public void UnknownTypes_ArriveAsUnknownTag()
    {
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, TagOptions());
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = new JsonTagSerializer().Register<StateTag>() });
        using RingReader<long> reader = opened.CreateReader();
        using RingReader<long> raw = RingBuffer<long>.Open(name).CreateReader();   // no serializer at all
        WriteBucket(writer, 4, b =>
        {
            b.AddTag(Label(b.Cursor + 1, "hello"));
            b.AddTag(new SampleRateTag { Offset = b.StartOffset + 2, Rate = 44100 });
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
        Assert.IsType<UnknownTag>(reader.ReadLastTagValues()[SampleRateTag.TagKey]);   // persistence comes from the writer's type
        Assert.True(raw.TryRead(4, out Chunk<long> rawChunk));
        Assert.All(rawChunk.Tags.ToArray(), t => Assert.IsType<UnknownTag>(t));
    }

    [Fact]
    public void PayloadsThatFailToDeserialize_ArriveAsUnknownTagWithTheError()
    {
        string name = TestNames.UniqueSection();
        using var writer = RingBuffer<long>.Create(1 << 16, name, TagOptions());
        var mismatched = new JsonTagSerializer().Register<IntLabelTag>(typeof(LabelTag).FullName);
        using var opened = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = mismatched });
        using RingReader<long> reader = opened.CreateReader();
        WriteBucket(writer, 2, b => b.AddTag(Label(b.Cursor, "not a number")));

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

    // ------------------------------------------------------------------ the tag log: wrap-around, waits, deadlock, dispose

    [Fact]
    public void TagLog_WrapsAround_WithReadersKeepingUp()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        using RingReader<long> reader = buffer.CreateReader();
        using RingReader<long> second = buffer.CreateReader();
        var rng = new Random(5);
        long written = 0;
        for (int round = 0; round < 3000; round++)
        {
            written += TagPlan.Write(buffer, rng.Next(1, 40), 16, rng);
            foreach (RingReader<long> r in new[] { reader, second })
            {
                long available = r.Available;
                if (available == 0)
                {
                    continue;
                }

                Assert.True(r.TryRead((int)available, out Chunk<long> chunk));
                Assert.Null(TagPlan.CheckChunk(chunk));
                r.Advance((int)available);
                Assert.Null(TagPlan.CheckState(r.ReadLastTagValues(), r.ReadCursor));
            }
        }

        Assert.True(buffer.TagWriter!.End > 20 * buffer.TagCapacity, $"the log wrapped only {buffer.TagWriter.End / buffer.TagCapacity} times");
        Assert.Equal(0, buffer.Counters.TagWaits);
    }

    [Fact]
    public void TagLog_Full_CommitWaitsUntilTheSlowestReaderPassesOlderTags()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        using RingReader<long> reader = buffer.CreateReader();
        int committed = 0;
        var writer = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                WriteBucket(buffer, 1, b => b.AddTag(Label(b.Cursor, new string('w', 100))));   // ~150 bytes: 4 KiB holds ~27
                Interlocked.Increment(ref committed);
            }
        }, TaskCreationOptions.LongRunning);

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "the writer waits for tag space");
        int stalledAt = Volatile.Read(ref committed);
        Assert.InRange(stalledAt, 10, 30);
        Assert.False(writer.IsCompleted);

        long read = 0;
        while (read < 100)
        {
            Assert.True(reader.WaitSync(1, RingTestUtil.Long));
            int n = (int)reader.Available;
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Equal(n, chunk.Tags.Length);
            reader.Advance(n);
            read += n;
        }

        Assert.True(writer.Wait(RingTestUtil.Long));
        Assert.True(buffer.Counters.TagWaits > 0);
    }

    [Fact]
    public void TagLog_Full_WithEveryHolderWaitingForMoreData_ThrowsTagLogFull()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        using RingReader<long> reader = buffer.CreateReader();
        var greedy = Task.Factory.StartNew(() => reader.WaitSync(1000, RingTestUtil.Long), TaskCreationOptions.LongRunning);   // more than will be published
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "the reader blocks");

        Exception? failure = null;
        int committed = 0;
        try
        {
            for (int i = 0; i < 100; i++)
            {
                WriteBucket(buffer, 1, b => b.AddTag(Label(b.Cursor, new string('w', 100))));
                committed++;
            }
        }
        catch (TagLogFullException ex)
        {
            failure = ex;
        }

        Assert.NotNull(failure);
        Assert.InRange(committed, 10, 30);
        Assert.Equal(committed, buffer.WriteCursor);                   // the failed bucket was dropped
        WriteBucket(buffer, 1000 - committed);                          // the writer is usable: publish enough to satisfy the reader
        Assert.True(greedy.Wait(RingTestUtil.Long));
        Assert.True(greedy.Result);
    }

    [Fact]
    public void TagLog_Deadlock_IsDetectedAlsoWhenTheWriterSpinsForever()
    {
        var options = new RingBufferOptions { TagCapacity = 4096, TagSerializer = TagPlan.CreateSerializer(), SpinTime = Timeout.InfiniteTimeSpan };
        using var buffer = RingBuffer<long>.Create(1 << 16, options: options);
        using RingReader<long> reader = buffer.CreateReader();
        var greedy = Task.Factory.StartNew(() => reader.WaitSync(1000, RingTestUtil.Long), TaskCreationOptions.LongRunning);
        RingTestUtil.WaitUntil(() => RingTestUtil.ReaderBitSet(buffer, reader.Slot), "the reader blocks");

        var writer = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                WriteBucket(buffer, 1, b => b.AddTag(Label(b.Cursor, new string('w', 100))));
            }
        }, TaskCreationOptions.LongRunning);

        AggregateException ex = Assert.Throws<AggregateException>(() => writer.Wait(RingTestUtil.Long));
        Assert.IsType<TagLogFullException>(ex.InnerException);
        WriteBucket(buffer, 1000);
        Assert.True(greedy.Wait(RingTestUtil.Long));
    }

    [Fact]
    public void Dispose_JustAsACommitWithTagsStopsWaiting_LetsTheCommitComplete()
    {
        var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        RingReader<long> reader = buffer.CreateReader();
        Task? disposer = null;
        TestHooks.BeforeTagAppend = committing =>
        {
            if (!ReferenceEquals(committing, buffer))
            {
                return;                                                     // another test's buffer
            }

            disposer = Task.Run(buffer.Dispose);                            // lands after the tags fit, before they are appended
            SpinWait.SpinUntil(() => buffer.IsWriterClosed, RingTestUtil.Long);
            Thread.Sleep(20);                                               // every chance to drop the bucket under the commit: it must wait instead
        };

        try
        {
            WriteBucket(buffer, 10, b => b.AddTag(Label(b.Cursor + 2, "committed while disposing")));
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
    public void CorruptTagEnd_OrRecordLength_IsReportedInsteadOfFollowed()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        using RingReader<long> early = buffer.CreateReader();

        // a header claiming a 16 MiB record at the start of the 4 KiB log
        var header = new TagRecordHeader { RecordBytes = 1 << 24, KeyBytes = 1, TypeNameBytes = 1, PayloadBytes = 2, Offset = 0 };
        System.Runtime.InteropServices.MemoryMarshal.Write(new Span<byte>(buffer.TagLog, TagRecordHeader.Bytes), in header);
        WriteBucket(buffer, 10);

        buffer.Header->TagEnd = 1 << 24;                                    // a TagEnd that would let the reader follow it, far beyond the log
        Assert.Throws<RingBufferLayoutException>(() => early.TryRead(10, out _));

        buffer.Header->TagEnd = 4000;                                       // inside the log: the record is longer than anything that can belong to it
        Assert.Throws<RingBufferLayoutException>(() => early.Advance(10));
        Assert.Throws<RingBufferLayoutException>(() => buffer.CreateReader());   // a joiner copies the same record from the snapshot range
        Assert.Equal(1, buffer.ActiveReaderCount);                          // and gives its slot back

        buffer.Header->TagEnd = 0;
    }

    [Fact]
    public void JoiningReader_WhenTheWriterDiedMidSnapshot_StartsAtTheFinalCursorWithoutState()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        WriteBucket(buffer, 20, b =>
        {
            b.AddTag(new SampleRateTag { Offset = b.StartOffset + 3, Rate = 9 });
            b.AddTag(Label(b.Cursor + 15, "published"));
        });

        buffer.Header->TagVersion |= 1;                                     // a snapshot that never completes ...
        int writerState = buffer.Header->WriterState;
        buffer.Header->WriterState = 2;                                     // ... because the writer is gone
        try
        {
            TagReader tags = TagReader.Join(buffer.Header, buffer.TagState, buffer.PersistentTagCapacity, buffer.TagLog, buffer.TagCapacity, TagPlan.CreateSerializer(), joinW: 10, out long cursor);
            Assert.Equal(20, cursor);                                       // not 10: the tags of [10, 20) were published before TagEnd, and are skipped with their elements
            Assert.Equal(buffer.Header->TagEnd, tags.Position);
            Assert.Empty(tags.LastValues());
            Assert.Equal(long.MaxValue, tags.NextOffset);
        }
        finally
        {
            buffer.Header->TagVersion &= ~1UL;
            buffer.Header->WriterState = writerState;
        }
    }

    [Fact]
    public void Pool_CountsTheTagAreaAgainstMaxIdleBytes()
    {
        using var pool = new RingBufferPool();
        var options = new RingBufferOptions { TagCapacity = 1 << 20, TagSerializer = TagPlan.CreateSerializer(), Pool = pool };
        long expected;
        using (var buffer = RingBuffer<long>.Create(1 << 16, options: options))
        {
            expected = buffer.DataBytes + buffer.TagCapacity + buffer.PersistentTagCapacity;
        }

        Assert.Equal(expected, pool.IdleBytes);
        using var bounded = new RingBufferPool(new RingBufferPoolOptions { MaxIdleBytes = expected - 1 });
        using (RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { TagCapacity = 1 << 20, TagSerializer = options.TagSerializer, Pool = bounded }))
        {
        }

        Assert.Equal(0, bounded.IdleCount);                                 // larger than the bound with its tag area: released at once
    }

    [Fact]
    public void TagLog_Full_DisposeEndsTheWaitingCommit()
    {
        var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions(tagCapacity: 4096));
        RingReader<long> reader = buffer.CreateReader();
        var writer = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                WriteBucket(buffer, 1, b => b.AddTag(Label(b.Cursor, new string('w', 100))));
            }
        }, TaskCreationOptions.LongRunning);

        RingTestUtil.WaitUntil(() => RingTestUtil.WriterIsWaiting(buffer), "the writer waits for tag space");
        buffer.Dispose();
        AggregateException ex = Assert.Throws<AggregateException>(() => writer.Wait(RingTestUtil.Long));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
        Assert.Equal(ReaderStatus.WriterClosed, reader.Status);
        reader.Dispose();
    }

    // ------------------------------------------------------------------ pool, allocations

    [Fact]
    public void PooledSection_Reuse_ForgetsThePreviousBuffersTags()
    {
        using var pool = new RingBufferPool();
        var options = new RingBufferOptions { TagCapacity = 1 << 16, TagSerializer = TagPlan.CreateSerializer(), Pool = pool };
        using (var first = RingBuffer<long>.Create(1 << 16, options: options))
        {
            WriteBucket(first, 10, b =>
            {
                b.AddTag(new SampleRateTag { Offset = b.StartOffset, Rate = 1 });
                b.AddTag(Label(b.Cursor + 5, "old"));
            });
        }

        Assert.Equal(1, pool.IdleCount);
        using var second = RingBuffer<long>.Create(1 << 16, options: options);
        Assert.Equal(1, pool.ReusedCount);
        using RingReader<long> reader = second.CreateReader();
        Assert.Empty(reader.ReadLastTagValues());
        WriteBucket(second, 10, b => b.AddTag(Label(b.Cursor + 5, "new")));
        Assert.True(reader.TryRead(10, out Chunk<long> chunk));
        Assert.Equal(["new"], Texts(chunk));

        // a pooled section is reused only for the same tag area
        using var other = RingBuffer<long>.Create(1 << 16, options: new RingBufferOptions { TagCapacity = 1 << 20, TagSerializer = options.TagSerializer, Pool = pool });
        Assert.Equal(1, pool.ReusedCount);
    }

    [Fact]
    public void TagsEnabledButUnused_TheHotPathDoesNotAllocate()
    {
        using var buffer = RingBuffer<long>.Create(1 << 16, options: TagOptions());
        using RingReader<long> reader = buffer.CreateReader();
        for (int i = 0; i < 1000; i++)
        {
            RunRound(buffer, reader);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            RunRound(buffer, reader);
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

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public void Fuzz_ConcurrentWriterReadersAndJoiners_MatchThePlan()
    {
        const long Total = 300_000;
        using var buffer = RingBuffer<long>.Create(1 << 12, options: TagOptions(tagCapacity: 1 << 16));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim(false);

        Task Reader(int seed, long joinAt) => Task.Factory.StartNew(() =>
        {
            if (joinAt > 0)
            {
                started.Wait();
                SpinWait.SpinUntil(() => buffer.WriteCursor >= joinAt);   // join mid-stream, after persistent tags were written and released
            }

            using RingReader<long> reader = buffer.CreateReader();
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
            }
        }, TaskCreationOptions.LongRunning);

        Task[] readers = [Reader(1, 0), Reader(2, 0), Reader(3, Total / 3), Reader(4, 2 * Total / 3)];
        started.Wait();
        long written = TagPlan.Write(buffer, Total, 200, new Random(11));
        buffer.Dispose();
        Assert.True(Task.WaitAll(readers, RingTestUtil.Long));
        Assert.Equal(Total, written);
        Assert.Empty(errors);
    }
}

/// <summary>A tag whose Text is a number: a LabelTag payload does not deserialize into it.</summary>
public sealed class IntLabelTag : ITag
{
    public ulong Offset { get; set; }

    public string Key { get; set; } = "";

    public int Text { get; set; }
}
