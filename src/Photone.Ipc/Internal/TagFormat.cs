using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>Bits of <see cref="TagRecordHeader.Flags"/>.</summary>
internal static class TagFlags
{
    /// <summary>The tag type declared <see cref="ITag.IsPersistent"/>.</summary>
    public const ushort Persistent = 1;

    /// <summary>
    /// Not a tag: the last record of a ring generation. The log continues right after it, at physical offset 0 of ring <see cref="TagRecordHeader.NextRing"/>
    /// (DESIGN §16.4). Its <see cref="TagRecordHeader.RecordBytes"/> is <see cref="TagRecordHeader.Bytes"/>, and it has no key, type name or payload.
    /// </summary>
    public const ushort Jump = 2;
}

/// <summary>
/// Header of one tag record (DESIGN §16.3). A record is this header, the key (UTF-8), the type name (UTF-8) and the serializer's payload,
/// zero-padded to a multiple of 8. The tag log and the persistent-tag table hold the same records.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Bytes)]
internal struct TagRecordHeader
{
    public const int Bytes = 24;

    /// <summary>Whole record including header and padding (a multiple of 8).</summary>
    [FieldOffset(0)] public int RecordBytes;

    /// <summary><see cref="TagFlags"/> bits.</summary>
    [FieldOffset(4)] public ushort Flags;

    [FieldOffset(6)] public ushort KeyBytes;

    /// <summary>Absolute element offset the tag is attached to.</summary>
    [FieldOffset(8)] public ulong Offset;

    [FieldOffset(16)] public ushort TypeNameBytes;

    /// <summary>The ring size class the log continues in (<see cref="TagFlags.Jump"/> records only).</summary>
    [FieldOffset(18)] public ushort NextRing;

    [FieldOffset(20)] public int PayloadBytes;

    public readonly bool IsPersistent => (Flags & TagFlags.Persistent) != 0;

    public readonly bool IsJump => (Flags & TagFlags.Jump) != 0;

    /// <summary>The marker that ends a ring generation and continues the log in ring <paramref name="nextRing"/>.</summary>
    public static TagRecordHeader Jump(int nextRing) => new() { RecordBytes = Bytes, Flags = TagFlags.Jump, NextRing = (ushort)nextRing };
}

/// <summary>
/// The geometry of the tag reserve, record validation, and wrap-aware copies into and out of a ring (DESIGN §16.2-§16.3).
/// <code>
/// tag reserve (section offset 64 KiB + D):  [table region 2 GiB][ring 0: 64 KiB][ring 1: 128 KiB] ... [ring 18: 16 GiB]
/// </code>
/// </summary>
internal static unsafe class TagFormat
{
    public const int MaxNameBytes = ushort.MaxValue;

    /// <summary>Size of the smallest ring (size class 0); ring <c>c</c> holds <c>MinRingBytes &lt;&lt; c</c> bytes.</summary>
    public const long MinRingBytes = 1L << 16;

    /// <summary>Ring size classes: the largest ring holds 16 GiB.</summary>
    public const int RingClasses = 19;

    /// <summary>The region of the persistent-tag table (<see cref="ControlBlock.TagStateUsed"/> is an <see cref="int"/>).</summary>
    public const long TableReserveBytes = 1L << 31;

    /// <summary>The whole tag reserve: only reserved, so it costs no memory until the writer commits parts of it.</summary>
    public const long ReserveBytes = TableReserveBytes + (MinRingBytes * ((1L << RingClasses) - 1));

    /// <summary>Size of ring <paramref name="ring"/>.</summary>
    public static long RingBytes(int ring) => MinRingBytes << ring;

    /// <summary>Offset of ring <paramref name="ring"/> within the tag reserve.</summary>
    public static long RingOffset(int ring) => TableReserveBytes + (MinRingBytes * ((1L << ring) - 1));

    /// <summary>The smallest ring class of at least <paramref name="bytes"/> bytes, or -1 when even the largest is smaller.</summary>
    public static int RingFor(long bytes)
    {
        for (int ring = 0; ring < RingClasses; ring++)
        {
            if (RingBytes(ring) >= bytes)
            {
                return ring;
            }
        }

        return -1;
    }

    /// <summary>Rounds up to the view granularity (64 KiB).</summary>
    public static long AlignView(long bytes) => (bytes + Layout.HeaderViewBytes - 1) & ~(Layout.HeaderViewBytes - 1L);

    public static int Align(int bytes) => (bytes + 7) & ~7;

    /// <summary>Validates a record header against the bytes that can belong to it (at most the rest of the log and the ring).</summary>
    public static bool IsValid(in TagRecordHeader h, long availableBytes, long ringBytes)
    {
        long record = h.RecordBytes;
        if (record < TagRecordHeader.Bytes || (record & 7) != 0 || record > availableBytes || record > ringBytes)
        {
            return false;
        }

        if (h.IsJump)
        {
            return record == TagRecordHeader.Bytes && h.NextRing < RingClasses && h.KeyBytes == 0 && h.TypeNameBytes == 0 && h.PayloadBytes == 0;
        }

        return h.PayloadBytes >= 0 && (long)TagRecordHeader.Bytes + h.KeyBytes + h.TypeNameBytes + h.PayloadBytes <= record;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="length"/> bytes at physical offset <paramref name="physical"/> of a ring lie in committed memory: below
    /// <paramref name="committed"/>, or anywhere once the whole ring is committed (a range that wraps needs that).
    /// </summary>
    public static bool IsCommitted(long ringBytes, long committed, long physical, long length)
        => committed >= ringBytes || (physical + length <= committed && physical + length <= ringBytes);

    /// <summary>Copies <paramref name="source"/> into a ring at physical offset <paramref name="physical"/>, wrapping at the end of the ring.</summary>
    public static void Write(byte* ring, long ringBytes, long physical, ReadOnlySpan<byte> source)
    {
        if (source.Length > ringBytes || (ulong)physical >= (ulong)ringBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.Length, $"A copy into a {ringBytes}-byte ring cannot be longer than the ring or start outside it.");
        }

        int first = (int)Math.Min(source.Length, ringBytes - physical);
        source[..first].CopyTo(new Span<byte>(ring + physical, first));
        if (first < source.Length)
        {
            source[first..].CopyTo(new Span<byte>(ring, source.Length - first));
        }
    }

    /// <summary>Copies bytes out of a ring starting at physical offset <paramref name="physical"/>, wrapping at the end of the ring.</summary>
    /// <exception cref="RingBufferLayoutException">The copy is longer than the ring or starts outside it.</exception>
    public static void Read(byte* ring, long ringBytes, long physical, Span<byte> destination)
    {
        if (destination.Length > ringBytes || (ulong)physical >= (ulong)ringBytes)
        {
            throw new RingBufferLayoutException($"A tag record of {destination.Length} bytes at physical offset {physical} cannot come from a {ringBytes}-byte ring.");
        }

        int first = (int)Math.Min(destination.Length, ringBytes - physical);
        new ReadOnlySpan<byte>(ring + physical, first).CopyTo(destination);
        if (first < destination.Length)
        {
            new ReadOnlySpan<byte>(ring, destination.Length - first).CopyTo(destination[first..]);
        }
    }

    /// <summary><see langword="true"/> when <paramref name="length"/> bytes at physical offset <paramref name="physical"/> do not cross the end of the ring.</summary>
    public static bool IsContiguous(long ringBytes, long physical, long length) => physical + length <= ringBytes;
}
