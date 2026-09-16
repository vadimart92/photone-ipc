using System.Runtime.InteropServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

/// <summary>12-byte element.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Vec3 : IEquatable<Vec3>
{
    public float X;
    public float Y;
    public float Z;

    public static Vec3 FromCursor(long c) => new() { X = c, Y = c * 2 + 1, Z = -c };

    public readonly bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override readonly bool Equals(object? obj) => obj is Vec3 v && Equals(v);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
}

/// <summary>24-byte element.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct Sample24 : IEquatable<Sample24>
{
    public long Sequence;
    public double Value;
    public int Tag;
    public int Flags;

    public static Sample24 FromCursor(long c) => new() { Sequence = c, Value = c * 0.5, Tag = (int)(c & 0xFFFF), Flags = (int)(c >> 16) };

    public readonly bool Equals(Sample24 other) => Sequence == other.Sequence && Value == other.Value && Tag == other.Tag && Flags == other.Flags;

    public override readonly bool Equals(object? obj) => obj is Sample24 s && Equals(s);

    public override readonly int GetHashCode() => HashCode.Combine(Sequence, Value, Tag, Flags);
}

/// <summary>Shared helpers for the ring-buffer tests.</summary>
internal static unsafe class RingTestUtil
{
    public static readonly TimeSpan Short = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    /// <summary>Writes <paramref name="count"/> elements whose value is their absolute cursor, in buckets of at most <paramref name="maxBucket"/>.</summary>
    public static long WriteSequence(RingBuffer<long> buffer, long count, int maxBucket, Random? rng = null)
    {
        long written = 0;
        while (written < count)
        {
            int n = (int)Math.Min(count - written, rng is null ? maxBucket : rng.Next(1, maxBucket + 1));
            using Bucket<long> bucket = buffer.GetBucket(n);
            Span<long> span = bucket.Span;
            for (int j = 0; j < span.Length; j++)
            {
                span[j] = bucket.Cursor + j;
            }

            bucket.Commit(n);
            written += n;
        }

        return written;
    }

    /// <summary>Reads exactly <paramref name="count"/> elements (blocking), verifying every value equals its cursor.</summary>
    public static void ReadSequence(RingReader<long> reader, long count, int maxChunk, Random? rng = null)
    {
        long remaining = count;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, rng is null ? maxChunk : rng.Next(1, maxChunk + 1));
            Assert.True(reader.WaitSync(n, Long), $"WaitSync({n}) failed: status={reader.Status} available={reader.Available}");
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            VerifyChunk(chunk);
            reader.Advance(n);
            remaining -= n;
        }
    }

    public static void VerifyChunk(Chunk<long> chunk)
    {
        ReadOnlySpan<long> span = chunk.Span;
        for (int j = 0; j < span.Length; j++)
        {
            if (span[j] != chunk.Cursor + j)
            {
                Assert.Fail($"data mismatch at cursor {chunk.Cursor + j}: got {span[j]}");
            }
        }
    }

    public static bool WriterIsWaiting<T>(RingBuffer<T> buffer) where T : unmanaged => Volatile.Read(ref buffer.Header->WriterWaiting) != 0;

    public static bool ReaderBitSet<T>(RingBuffer<T> buffer, int slot) where T : unmanaged => (Volatile.Read(ref buffer.Header->WaitersMask) & (1UL << slot)) != 0;

    public static ref ReaderSlot Slot<T>(RingBuffer<T> buffer, int i) where T : unmanaged => ref ControlBlock.SlotRef(buffer.Header, i);

    public static int LastEvictedPid<T>(RingBuffer<T> buffer) where T : unmanaged => Volatile.Read(ref buffer.Header->LastEvictedPid);

    /// <summary>Patches the header's backend id (test hook for the unknown-backend path); returns the previous value.</summary>
    public static uint SwapBackendId<T>(RingBuffer<T> buffer, uint id) where T : unmanaged
    {
        uint old = buffer.Header->SignalBackendId;
        buffer.Header->SignalBackendId = id;
        return old;
    }

    public static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        if (!SpinWait.SpinUntil(condition, timeout ?? Short))
        {
            Assert.Fail("Timed out waiting for: " + what);
        }
    }
}
