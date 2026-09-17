namespace Photone.Ipc.Tests;

public sealed class WriterTests
{
    private static void RoundTripAcrossWrap<T>(Func<long, T> make) where T : unmanaged, IEquatable<T>
    {
        using RingBuffer<T> buffer = RingBuffer<T>.Create(1024);
        using RingReader<T> reader = buffer.CreateReader();
        long c = buffer.Capacity;
        Assert.True(c >= 1024);

        // stream up to 5 elements before the end of the ring, verifying each bucket as it is read
        long cursor = 0;
        while (cursor < c - 5)
        {
            int n = (int)Math.Min(1000, c - 5 - cursor);
            using (Bucket<T> bucket = buffer.GetBucket(n))
            {
                Assert.Equal(n, bucket.Length);
                Assert.Equal(cursor, bucket.Cursor);
                for (int j = 0; j < n; j++)
                {
                    bucket.Span[j] = make(cursor + j);
                }

                bucket.Commit(n);
            }

            Assert.True(reader.TryRead(n, out Chunk<T> chunk));
            Assert.Equal(cursor, chunk.Cursor);
            ReadOnlySpan<T> span = chunk.Data.Span;
            for (int j = 0; j < n; j++)
            {
                Assert.True(span[j].Equals(make(cursor + j)), $"mismatch at {cursor + j}");
            }

            reader.Advance(n);
            cursor += n;
        }

        // a 300-element bucket now crosses the data/mirror boundary; the span is still contiguous
        using (Bucket<T> bucket = buffer.GetBucket(300))
        {
            Assert.Equal(c - 5, bucket.Cursor);
            Assert.Equal(300, bucket.Span.Length);
            for (int j = 0; j < 300; j++)
            {
                bucket.Span[j] = make(bucket.Cursor + j);
            }

            bucket.Commit(300);
        }

        Assert.Equal(c + 295, buffer.WriteCursor);
        Assert.True(reader.TryRead(300, out Chunk<T> wrapped));
        Assert.Equal(c - 5, wrapped.Cursor);
        ReadOnlySpan<T> wrappedSpan = wrapped.Data.Span;
        for (int j = 0; j < 300; j++)
        {
            Assert.True(wrappedSpan[j].Equals(make(c - 5 + j)), $"wrap mismatch at {c - 5 + j}");
        }

        reader.Advance(300);
        Assert.Equal(0, reader.Available);
    }

    [Fact]
    public void GetBucket_ExactCount_Contiguous_AcrossWrap_Byte() => RoundTripAcrossWrap(static c => (byte)(c * 7 + 3));

    [Fact]
    public void GetBucket_ExactCount_Contiguous_AcrossWrap_Float() => RoundTripAcrossWrap(static c => c * 0.25f);

    [Fact]
    public void GetBucket_ExactCount_Contiguous_AcrossWrap_Vec3() => RoundTripAcrossWrap(Vec3.FromCursor);

    [Fact]
    public void GetBucket_ExactCount_Contiguous_AcrossWrap_Sample24() => RoundTripAcrossWrap(Sample24.FromCursor);

    [Fact]
    public void GetBucket_TooLarge_Throws()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => { buffer.GetBucket((int)buffer.Capacity + 1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { buffer.TryGetBucket((int)buffer.Capacity + 1, out _); });
    }

    [Fact]
    public void GetBucket_Zero_Throws()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => { buffer.GetBucket(0); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { buffer.GetBucket(-1); });
    }

    [Fact]
    public void GetBucket_WhileOutstanding_Throws()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        Bucket<int> first = buffer.GetBucket(10);
        Assert.Throws<InvalidOperationException>(() => { buffer.GetBucket(1); });
        Assert.Throws<InvalidOperationException>(() => { buffer.TryGetBucket(1, out _); });
        first.Commit(10);
        using Bucket<int> second = buffer.GetBucket(1);
        Assert.Equal(10, second.Cursor);
    }

    [Fact]
    public void GetBucket_NotWriter_Throws()
    {
        string name = TestNames.Unique();
        using RingBuffer<int> writer = RingBuffer<int>.Create(1000, name);
        using RingBuffer<int> opened = RingBuffer<int>.Open(name);
        Assert.False(opened.IsWriter);
        Assert.True(writer.IsWriter);
        Assert.Throws<InvalidOperationException>(() => { opened.GetBucket(1); });
        Assert.Throws<InvalidOperationException>(() => opened.FreeSpace);
    }

    [Fact]
    public void GetBucket_AfterDispose_Throws()
    {
        RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { buffer.GetBucket(1); });
        Assert.Throws<ObjectDisposedException>(() => buffer.CreateReader());
    }

    [Fact]
    public void Commit_Less_ShrinksReservation()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        using (Bucket<int> b = buffer.GetBucket(100))
        {
            b.Commit(40);
            Assert.Equal(0, b.Length);
            Assert.True(b.Span.IsEmpty);
        }

        Assert.Equal(40, buffer.WriteCursor);
        using Bucket<int> next = buffer.GetBucket(10);
        Assert.Equal(40, next.Cursor);
    }

    [Fact]
    public void Commit_Twice_Throws()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        Bucket<int> b = buffer.GetBucket(10);
        b.Commit(5);
        AssertCommitThrows<InvalidOperationException>(ref b, 1);
        AssertCommitThrows<InvalidOperationException>(ref b, 0);
        b.Dispose();
        Assert.Equal(5, buffer.WriteCursor);
    }

    [Fact]
    public void Commit_TooMany_Throws()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        Bucket<int> b = buffer.GetBucket(10);
        AssertCommitThrows<ArgumentOutOfRangeException>(ref b, 11);
        AssertCommitThrows<ArgumentOutOfRangeException>(ref b, -1);
        b.Commit(10);
    }

    /// <summary>A ref struct cannot be captured by a lambda, so <c>Assert.Throws</c> cannot be used on <see cref="Bucket{T}.Commit"/> directly.</summary>
    private static void AssertCommitThrows<TException>(ref Bucket<int> bucket, int count) where TException : Exception
    {
        try
        {
            bucket.Commit(count);
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Commit({count}) did not throw {typeof(TException).Name}");
    }

    [Fact]
    public void Dispose_WithoutCommit_DropsReservation()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        using (Bucket<int> b = buffer.GetBucket(100))
        {
            b.Span.Fill(7);
        }

        Assert.Equal(0, buffer.WriteCursor);
        using Bucket<int> next = buffer.GetBucket(10);
        Assert.Equal(0, next.Cursor);
    }

    [Fact]
    public void Commit_Zero()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        using RingReader<int> reader = buffer.CreateReader();
        using (Bucket<int> b = buffer.GetBucket(100))
        {
            b.Commit(0);
        }

        Assert.Equal(0, buffer.WriteCursor);
        Assert.Equal(0, reader.Available);
        Assert.Equal(1, buffer.Counters.Commits);
    }

    [Fact]
    public void NoReaders_WriterNeverBlocks()
    {
        using RingBuffer<byte> buffer = RingBuffer<byte>.Create(1);   // 65536 bytes
        long c = buffer.Capacity;
        int n = (int)(c / 4);
        for (int i = 0; i < 40; i++)                                    // 10 x C
        {
            using Bucket<byte> b = buffer.GetBucket(n);
            b.Span.Fill((byte)i);
            b.Commit(n);
        }

        Assert.Equal(10 * c, buffer.WriteCursor);
        Assert.Equal(0, buffer.Counters.KernelWaits);
        Assert.Equal(0, buffer.Counters.Signals);
        Assert.Equal(c, buffer.FreeSpace);
    }

    [Fact]
    public void FullCapacity_Usable()
    {
        using RingBuffer<int> buffer = RingBuffer<int>.Create(1000);
        using RingReader<int> reader = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        using (Bucket<int> b = buffer.GetBucket(c))
        {
            Assert.Equal(c, b.Length);
            for (int i = 0; i < c; i++)
            {
                b.Span[i] = i;
            }

            b.Commit(c);
        }

        Assert.False(buffer.TryGetBucket(1, out _));
        Assert.Equal(0, buffer.FreeSpace);
        Assert.True(reader.TryRead(c, out Chunk<int> chunk));
        ReadOnlySpan<int> span = chunk.Data.Span;
        for (int i = 0; i < c; i++)
        {
            Assert.Equal(i, span[i]);
        }

        reader.Advance(c);
        Assert.True(buffer.TryGetBucket(1, out Bucket<int> one));
        Assert.Equal(c, one.Cursor);
        one.Commit(1);
        Assert.Equal(c + 1, buffer.WriteCursor);
    }

    [Fact]
    public void TryGetBucket_ReturnsFalseWhenFull_TrueAfterAdvance()
    {
        using RingBuffer<long> buffer = RingBuffer<long>.Create(1 << 12);
        using RingReader<long> reader = buffer.CreateReader();
        int c = (int)buffer.Capacity;
        RingTestUtil.WriteSequence(buffer, c, 1000);
        Assert.False(buffer.TryGetBucket(1, out _));
        reader.WaitSync(1);
        Assert.True(reader.TryRead(10, out _));
        reader.Advance(10);
        Assert.True(buffer.TryGetBucket(10, out Bucket<long> b));
        Assert.Throws<InvalidOperationException>(() => { buffer.TryGetBucket(1, out _); });   // one outstanding bucket
        b.Dispose();
    }
}
