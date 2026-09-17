using System.Numerics;
using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>Bits of <see cref="TagRecordHeader.Flags"/>.</summary>
internal static class TagFlags
{
    /// <summary>The tag type declared <see cref="ITag.IsPersistent"/>.</summary>
    public const ushort Persistent = 1;
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

    [FieldOffset(18)] public ushort Reserved;

    [FieldOffset(20)] public int PayloadBytes;

    public readonly bool IsPersistent => (Flags & TagFlags.Persistent) != 0;
}

/// <summary>The tag area of a buffer: <c>[persistent-tag table][tag log]</c> at section offset 64 KiB, followed by the data (DESIGN §16.2).</summary>
internal readonly record struct TagArea(long LogBytes, int StateBytes)
{
    /// <summary>The header view: the 64 KiB control view and the tag area (<c>ControlBlock.DataOffset</c>).</summary>
    public long HeaderBytes => Layout.HeaderViewBytes + StateBytes + LogBytes;

    /// <summary>The area requested by creator options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A tag capacity is out of range.</exception>
    public static TagArea Choose(RingBufferOptions options)
    {
        (long log, int state) = TagFormat.ChooseArea(options.TagCapacity, options.PersistentTagCapacity);
        return new TagArea(log, state);
    }
}

/// <summary>Sizes of the tag area, record validation and wrap-aware copies into and out of the tag log (DESIGN §16).</summary>
internal static unsafe class TagFormat
{
    public const long MinLogBytes = 4096;
    public const long MaxLogBytes = 1L << 30;
    public const int MaxStateBytes = 64 << 20;
    public const int DefaultStateBytes = 16 << 10;
    public const int MaxNameBytes = ushort.MaxValue;

    /// <summary>
    /// The tag area for the requested capacities: a power-of-two log of at least <paramref name="tagCapacity"/> bytes (at least 4 KiB), and a persistent-tag table
    /// of at least <paramref name="persistentCapacity"/> bytes that also takes the rounding slack, so that the area is a multiple of 64 KiB.
    /// <c>(0, 0)</c> when <paramref name="tagCapacity"/> is 0 (no tags).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is negative or too large.</exception>
    public static (long LogBytes, int StateBytes) ChooseArea(long tagCapacity, int persistentCapacity)
    {
        if (tagCapacity < 0 || tagCapacity > MaxLogBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(tagCapacity), tagCapacity, $"TagCapacity must be between 0 and {MaxLogBytes} bytes.");
        }

        if (persistentCapacity < 0 || persistentCapacity > MaxStateBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(persistentCapacity), persistentCapacity, $"PersistentTagCapacity must be between 0 and {MaxStateBytes} bytes.");
        }

        if (tagCapacity == 0)
        {
            return (0, 0);
        }

        long log = Math.Max(MinLogBytes, (long)BitOperations.RoundUpToPowerOf2((ulong)tagCapacity));
        long granularity = Layout.HeaderViewBytes;
        long area = (log + persistentCapacity + granularity - 1) / granularity * granularity;
        return (log, (int)(area - log));
    }

    /// <summary><see langword="true"/> for a sane tag area as stored in a control block (either both sizes zero, or a power-of-two log and a 64 KiB-aligned area).</summary>
    public static bool IsValidArea(long logBytes, int stateBytes)
    {
        if (logBytes == 0)
        {
            return stateBytes == 0;
        }

        return logBytes >= MinLogBytes && logBytes <= MaxLogBytes && BitOperations.IsPow2(logBytes)
            && stateBytes >= 0 && stateBytes <= MaxStateBytes + Layout.HeaderViewBytes && (logBytes + stateBytes) % Layout.HeaderViewBytes == 0;
    }

    public static int Align(int bytes) => (bytes + 7) & ~7;

    /// <summary>Validates a record header against the bytes that can belong to it.</summary>
    public static bool IsValid(in TagRecordHeader h, long availableBytes)
    {
        long record = h.RecordBytes;
        return record >= TagRecordHeader.Bytes && (record & 7) == 0 && record <= availableBytes && h.PayloadBytes >= 0
            && (long)TagRecordHeader.Bytes + h.KeyBytes + h.TypeNameBytes + h.PayloadBytes <= record;
    }

    /// <summary>Copies <paramref name="source"/> into the log at absolute position <paramref name="position"/>, wrapping at the end of the log.</summary>
    public static void Write(byte* log, long logBytes, long position, ReadOnlySpan<byte> source)
    {
        if (source.Length > logBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.Length, "A copy into the tag log cannot be longer than the log.");
        }

        long start = position & (logBytes - 1);
        int first = (int)Math.Min(source.Length, logBytes - start);
        source[..first].CopyTo(new Span<byte>(log + start, first));
        if (first < source.Length)
        {
            source[first..].CopyTo(new Span<byte>(log, source.Length - first));
        }
    }

    /// <summary>Copies bytes out of the log starting at absolute position <paramref name="position"/>, wrapping at the end of the log.</summary>
    public static void Read(byte* log, long logBytes, long position, Span<byte> destination)
    {
        if (destination.Length > logBytes)
        {
            throw new RingBufferLayoutException($"A tag record of {destination.Length} bytes cannot come from a {logBytes}-byte tag log.");
        }

        long start = position & (logBytes - 1);
        int first = (int)Math.Min(destination.Length, logBytes - start);
        new ReadOnlySpan<byte>(log + start, first).CopyTo(destination);
        if (first < destination.Length)
        {
            new ReadOnlySpan<byte>(log, destination.Length - first).CopyTo(destination[first..]);
        }
    }

    /// <summary><see langword="true"/> when <paramref name="length"/> bytes at <paramref name="position"/> do not cross the end of the log.</summary>
    public static bool IsContiguous(long logBytes, long position, int length) => (position & (logBytes - 1)) + length <= logBytes;
}
