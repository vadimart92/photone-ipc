using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Photone.Ipc.Internal;

/// <summary>
/// The tags of a reader of a buffer opened from shared memory (DESIGN §16.7): records loaded from the ring generations in the tag reserve and turned into
/// tags by the reader's <see cref="ITagSerializer"/>. Every range it touches is checked against the committed sizes the writer publishes, so a corrupt
/// header is reported instead of faulting.
/// </summary>
internal sealed unsafe class SharedTagReader : TagReader
{
    private const int MaxCachedNames = 1024;
    private const int StackNameChars = 256;
    private const int VersionCheckRecords = 64;

    private readonly ControlBlock* _hdr;
    private readonly TagViews _views;
    private readonly ITagSerializer? _serializer;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private long _position;                 // absolute log position of the next record to load
    private int _ring;                      // the ring generation that holds _position
    private long _ringStart;
    private long _committed;                // TagRingCommitted[_ring] as last loaded (it only grows)
    private byte[] _scratch = [];

    private SharedTagReader(ControlBlock* hdr, TagViews views, ITagSerializer? serializer, long position, int ring, long ringStart)
    {
        _hdr = hdr;
        _views = views;
        _serializer = serializer;
        _position = position;
        _ring = ring;
        _ringStart = ringStart;
    }

    /// <inheritdoc/>
    public override long* EndPointer => &_hdr->TagEnd;

    /// <inheritdoc/>
    public override long Position => _position;

    /// <summary>Size class of the ring generation that holds <see cref="Position"/> (tests).</summary>
    public int Ring => _ring;

    // ------------------------------------------------------------------ join (DESIGN §16.6)

    /// <summary>
    /// The tag state of a reader that joins at write cursor <paramref name="joinW"/> (its slot is already Active with that cursor): a consistent copy of the
    /// persistent-tag table and of the records published after it, taken under the <c>TagVersion</c> seqlock. <paramref name="cursor"/> is where the
    /// reader starts: <paramref name="joinW"/>, or the snapshot's write cursor when that is newer (the table already reflects the tags before it).
    /// </summary>
    /// <exception cref="RingBufferLayoutException">The snapshot is stable but malformed.</exception>
    public static SharedTagReader Join(ControlBlock* hdr, TagViews views, ITagSerializer? serializer, long joinW, out long cursor)
    {
        byte[] table = [];
        byte[] records = [];
        int used;
        int recordBytes;
        long snapshotW;
        long end;
        int ring;
        long ringStart;
        long nextLivenessCheck = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 1000);
        for (int attempt = 0; ; attempt++)
        {
            ulong v1 = Volatile.Read(ref hdr->TagVersion);
            if ((v1 & 1) == 0)
            {
                long snapshotEnd = hdr->TagSnapshotEnd;
                snapshotW = hdr->TagSnapshotW;
                used = hdr->TagStateUsed;
                ring = hdr->TagSnapshotRing;
                ringStart = hdr->TagSnapshotRingStart;
                end = Volatile.Read(ref hdr->TagEnd);
                long tableCommitted = Volatile.Read(ref hdr->TagTableCommitted);   // after used: every table size the writer stored was committed before it
                long w = Volatile.Read(ref hdr->WriteCursor);               // after TagSnapshotW, which the writer publishes after the write cursor
                recordBytes = 0;
                bool sane = used >= 0 && used <= tableCommitted && used <= Array.MaxLength && snapshotEnd >= 0 && end >= snapshotEnd
                    && end - snapshotEnd <= TagFormat.ReserveBytes && snapshotW >= 0 && snapshotW <= w
                    && (uint)ring < TagFormat.RingClasses && ringStart >= 0 && ringStart <= snapshotEnd;
                if (sane)
                {
                    if (table.Length < used)
                    {
                        table = new byte[used];
                    }

                    if (used != 0)
                    {
                        new ReadOnlySpan<byte>(views.Table(used).Address, used).CopyTo(table);
                    }

                    sane = TryCopyRecords(hdr, views, v1, snapshotEnd, end, ref ring, ref ringStart, ref records, out recordBytes);
                }

                if (Volatile.Read(ref hdr->TagVersion) == v1)
                {
                    if (!sane)
                    {
                        throw new RingBufferLayoutException($"The tag snapshot is malformed (table {used} bytes, end {snapshotEnd}, tag end {end}, ring {ring}).");
                    }

                    break;
                }

                // A new snapshot was published meanwhile: the writer is alive and well, retry.
            }
            else if (Stopwatch.GetTimestamp() >= nextLivenessCheck)
            {
                // The writer is publishing a snapshot (a few stores and at most one table copy): retry at once, then back off. A writer that died in the
                // middle of it leaves the version odd for good: start without the table, after every published record, at the final write cursor (loaded
                // after TagEnd, so every record before TagEnd with a lower offset is behind the reader; one beyond it belongs to elements never published).
                // Only an odd version leads here: a writer that closed normally always left an even one, which the next attempt copies. The version is
                // loaded again after the writer is found gone, because it may have completed the snapshot and closed since this attempt read it; a writer
                // that is gone cannot make an odd version even any more, so an odd one then stays odd.
                nextLivenessCheck = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 100);
                if (WriterGone(hdr) && (Volatile.Read(ref hdr->TagVersion) & 1) != 0)
                {
                    long final = Volatile.Read(ref hdr->TagEnd);
                    cursor = Math.Max(joinW, Volatile.Read(ref hdr->WriteCursor));
                    return new SharedTagReader(hdr, views, serializer, final, ring: 0, ringStart: final);   // nothing will ever be loaded
                }
            }

            if (attempt < 64)
            {
                Thread.SpinWait(16);
            }
            else if (attempt < 128)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        cursor = Math.Max(joinW, snapshotW);
        var reader = new SharedTagReader(hdr, views, serializer, end, ring, ringStart);
        reader.LoadCopied(table.AsSpan(0, used), long.MaxValue);        // every table record is state
        reader.LoadCopied(records.AsSpan(0, recordBytes), cursor);      // below the start cursor: state; at or after it: the first queued tags
        return reader;
    }

    private static bool WriterGone(ControlBlock* hdr)
        => Volatile.Read(ref hdr->WriterState) == 2 || !ProcessLiveness.IsAlive(Volatile.Read(ref hdr->WriterPid), Volatile.Read(ref hdr->WriterStartTime));

    /// <summary>
    /// Copies the records of <c>[from, to)</c> (without jump records), following the generations from <paramref name="ring"/> / <paramref name="ringStart"/>,
    /// which end as the generation of <paramref name="to"/>. The values come from an unvalidated snapshot, so every step is checked: a header that makes no
    /// sense, memory that is not committed, or a snapshot version that changed meanwhile ends the copy with <see langword="false"/>. Never touches memory
    /// outside committed tag memory.
    /// </summary>
    private static bool TryCopyRecords(ControlBlock* hdr, TagViews views, ulong version, long from, long to, ref int ring, ref long ringStart, ref byte[] buffer, out int length)
    {
        length = 0;
        long position = from;
        int records = 0;
        while (position < to)
        {
            if ((uint)ring >= TagFormat.RingClasses || ringStart > position
                || (++records % VersionCheckRecords == 0 && Volatile.Read(ref hdr->TagVersion) != version))
            {
                return false;
            }

            long ringBytes = TagFormat.RingBytes(ring);
            long committed = Volatile.Read(ref hdr->TagRingCommitted[ring]);
            long physical = (position - ringStart) & (ringBytes - 1);
            if (!TagFormat.IsCommitted(ringBytes, committed, physical, TagRecordHeader.Bytes))
            {
                return false;
            }

            byte* address = views.Ring(ring).Address;
            TagRecordHeader h = default;
            TagFormat.Read(address, ringBytes, physical, MemoryMarshal.AsBytes(new Span<TagRecordHeader>(ref h)));
            if (!TagFormat.IsValid(h, to - position, ringBytes) || !TagFormat.IsCommitted(ringBytes, committed, physical, h.RecordBytes))
            {
                return false;
            }

            if (h.IsJump)
            {
                position += TagRecordHeader.Bytes;
                ring = h.NextRing;
                ringStart = position;
                continue;
            }

            if ((long)length + h.RecordBytes > Array.MaxLength)
            {
                return false;
            }

            if (buffer.Length < length + h.RecordBytes)
            {
                Array.Resize(ref buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)length + h.RecordBytes, 2L * buffer.Length)));
            }

            TagFormat.Read(address, ringBytes, physical, buffer.AsSpan(length, h.RecordBytes));
            length += h.RecordBytes;
            position += h.RecordBytes;
        }

        return true;
    }

    private void LoadCopied(ReadOnlySpan<byte> records, long readCursor)
    {
        while (!records.IsEmpty)
        {
            TagRecordHeader h = records.Length >= TagRecordHeader.Bytes ? MemoryMarshal.Read<TagRecordHeader>(records) : default;
            if (!TagFormat.IsValid(h, records.Length, long.MaxValue) || h.IsJump)
            {
                throw new RingBufferLayoutException("A tag record of the snapshot is malformed.");
            }

            Accept(h, records[..h.RecordBytes], readCursor);
            records = records[h.RecordBytes..];
        }
    }

    // ------------------------------------------------------------------ steady state

    /// <inheritdoc/>
    public override long Load(long end, long readCursor)
    {
        long position = _position;
        if (end < position || end - position > TagFormat.ReserveBytes)
        {
            // Unloaded records are never released (DESIGN §16.4), so they fit in the reserve. Anything else is a corrupt header.
            throw new RingBufferLayoutException($"TagEnd {end} does not fit the reader's position {position}.");
        }

        while (position < end)
        {
            long ringBytes = TagFormat.RingBytes(_ring);
            long physical = (position - _ringStart) & (ringBytes - 1);
            EnsureCommitted(ringBytes, physical, TagRecordHeader.Bytes, position);
            byte* address = _views.Ring(_ring).Address;
            TagRecordHeader h = default;
            TagFormat.Read(address, ringBytes, physical, MemoryMarshal.AsBytes(new Span<TagRecordHeader>(ref h)));
            if (!TagFormat.IsValid(h, end - position, ringBytes))
            {
                throw new RingBufferLayoutException($"The tag record at log position {position} (ring {_ring}) is malformed.");
            }

            EnsureCommitted(ringBytes, physical, h.RecordBytes, position);
            if (h.IsJump)
            {
                position += TagRecordHeader.Bytes;
                _ring = h.NextRing;
                _ringStart = position;
                _committed = 0;
                _position = position;
                continue;
            }

            ReadOnlySpan<byte> record;
            if (TagFormat.IsContiguous(ringBytes, physical, h.RecordBytes))
            {
                record = new ReadOnlySpan<byte>(address + physical, h.RecordBytes);
            }
            else
            {
                if (_scratch.Length < h.RecordBytes)
                {
                    _scratch = new byte[Math.Max(h.RecordBytes, 2 * _scratch.Length)];
                }

                TagFormat.Read(address, ringBytes, physical, _scratch.AsSpan(0, h.RecordBytes));
                record = _scratch.AsSpan(0, h.RecordBytes);
            }

            Accept(h, record, readCursor);
            position += h.RecordBytes;
            _position = position;                                       // a record whose tag was accepted is never loaded twice, even if a later one throws
        }

        return NextOffset;
    }

    /// <summary>A published record lies in committed memory; reloads the committed size once before calling a range outside it corrupt.</summary>
    private void EnsureCommitted(long ringBytes, long physical, long length, long position)
    {
        if (TagFormat.IsCommitted(ringBytes, _committed, physical, length))
        {
            return;
        }

        _committed = Volatile.Read(ref _hdr->TagRingCommitted[_ring]);
        if (!TagFormat.IsCommitted(ringBytes, _committed, physical, length))
        {
            throw new RingBufferLayoutException($"The tag record at log position {position} lies outside the committed memory of ring {_ring}.");
        }
    }

    private void Accept(in TagRecordHeader h, ReadOnlySpan<byte> record, long readCursor)
    {
        ReadOnlySpan<byte> key = record.Slice(TagRecordHeader.Bytes, h.KeyBytes);
        ReadOnlySpan<byte> typeName = record.Slice(TagRecordHeader.Bytes + h.KeyBytes, h.TypeNameBytes);
        ReadOnlySpan<byte> payload = record.Slice(TagRecordHeader.Bytes + h.KeyBytes + h.TypeNameBytes, h.PayloadBytes);
        string keyText = Name(key);
        long offset = h.Offset > long.MaxValue ? long.MaxValue : (long)h.Offset;
        if (offset < readCursor && !h.IsPersistent)
        {
            return;                                                     // behind the reader and not state: nothing to materialize
        }

        ITag tag = Materialize(h, keyText, Name(typeName), payload);
        Accept(tag, offset, keyText, h.IsPersistent, readCursor);
    }

    private ITag Materialize(in TagRecordHeader h, string key, string typeName, ReadOnlySpan<byte> payload)
    {
        Exception? error = null;
        if (_serializer is not null)
        {
            try
            {
                if (_serializer.Deserialize(typeName, payload) is ITag tag)
                {
                    tag.Offset = h.Offset;                              // the record's offset is authoritative: a tag type need not serialize it
                    return tag;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        return new UnknownTag(h.Offset, key, typeName, h.IsPersistent, payload.ToArray(), error);
    }

    /// <summary>Decodes a UTF-8 key or type name, reusing the string of an earlier record with the same name.</summary>
    private string Name(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > StackNameChars)
        {
            return Encoding.UTF8.GetString(utf8);
        }

        Span<char> chars = stackalloc char[StackNameChars];
        int length = Encoding.UTF8.GetChars(utf8, chars);
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(chars[..length], out string? name))
        {
            return name;
        }

        name = new string(chars[..length]);
        if (_names.Count < MaxCachedNames)
        {
            _names[name] = name;
        }

        return name;
    }
}
