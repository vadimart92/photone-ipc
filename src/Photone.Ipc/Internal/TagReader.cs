using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Photone.Ipc.Internal;

/// <summary>
/// A reader's side of the tags (DESIGN §16.6): the tags loaded from the log whose offset the reader has not passed yet (in offset order), and the last
/// persistent tag of every key before the reader's position. Single-consumer, like the reader that owns it.
/// </summary>
internal sealed unsafe class TagReader
{
    private const int MaxCachedNames = 1024;
    private const int StackNameChars = 256;

    private readonly ControlBlock* _hdr;
    private readonly byte* _log;
    private readonly long _logBytes;
    private readonly ITagSerializer? _serializer;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private long _position;                 // absolute log position of the next record to load
    private ITag[] _items = new ITag[8];    // queue [_head, _head + _count): never compacted in place, so memory handed out in a chunk keeps its contents
    private long[] _offsets = new long[8];
    private string[] _keys = new string[8];
    private bool[] _persistent = new bool[8];
    private int _head;
    private int _count;
    private ITag[] _lastValues = [];        // the last persistent tag of each key, in the order the keys first appeared
    private int _lastCount;
    private Dictionary<string, int>? _lastIndex;
    private byte[] _scratch = [];

    private TagReader(ControlBlock* hdr, byte* log, long logBytes, ITagSerializer? serializer, long position)
    {
        _hdr = hdr;
        _log = log;
        _logBytes = logBytes;
        _serializer = serializer;
        _position = position;
    }

    /// <summary>Offset of the first queued tag; <see cref="long.MaxValue"/> when the queue is empty.</summary>
    public long NextOffset => _count == 0 ? long.MaxValue : _offsets[_head];

    /// <summary>Queued tags (tests).</summary>
    public int QueuedCount => _count;

    /// <summary>Absolute log position of the next record to load (tests).</summary>
    public long Position => _position;

    // ------------------------------------------------------------------ join (DESIGN §16.5)

    /// <summary>
    /// The tag state of a reader that joins at write cursor <paramref name="joinW"/> (its slot is already Active with that cursor): a consistent copy of the
    /// persistent-tag table and of the records published after it, taken under the <c>TagVersion</c> seqlock. <paramref name="cursor"/> is where the
    /// reader starts: <paramref name="joinW"/>, or the snapshot's write cursor when that is newer (the table already reflects the tags before it).
    /// </summary>
    /// <exception cref="RingBufferLayoutException">The snapshot is stable but malformed.</exception>
    public static TagReader Join(ControlBlock* hdr, byte* state, int stateBytes, byte* log, long logBytes, ITagSerializer? serializer, long joinW, out long cursor)
    {
        byte[] table = [];
        byte[] records = [];
        int used;
        long snapshotW;
        long end;
        int span;
        long started = Stopwatch.GetTimestamp();
        long nextLivenessCheck = started + Stopwatch.Frequency / 1000;
        for (int attempt = 0; ; attempt++)
        {
            ulong v1 = Volatile.Read(ref hdr->TagVersion);
            if ((v1 & 1) == 0)
            {
                long snapshotEnd = hdr->TagSnapshotEnd;
                snapshotW = hdr->TagSnapshotW;
                used = hdr->TagStateUsed;
                end = Volatile.Read(ref hdr->TagEnd);
                bool sane = used >= 0 && used <= stateBytes && snapshotEnd >= 0 && end >= snapshotEnd && end - snapshotEnd <= logBytes && snapshotW >= 0;
                if (sane)
                {
                    span = (int)(end - snapshotEnd);
                    if (table.Length < used)
                    {
                        table = new byte[used];
                    }

                    if (records.Length < span)
                    {
                        records = new byte[span];
                    }

                    new ReadOnlySpan<byte>(state, used).CopyTo(table);
                    TagFormat.Read(log, logBytes, snapshotEnd, records.AsSpan(0, span));
                }
                else
                {
                    span = 0;
                }

                if (Volatile.Read(ref hdr->TagVersion) == v1)
                {
                    if (!sane)
                    {
                        throw new RingBufferLayoutException($"The tag snapshot is malformed (table {used} bytes, end {snapshotEnd}, tag end {end}).");
                    }

                    break;
                }
            }

            // The writer is publishing a snapshot (a few stores and at most one table copy): retry at once, then back off. A writer that died in the
            // middle of it leaves the version odd for good: start without the table, after every published record, at the final write cursor (loaded
            // after TagEnd, so every record before TagEnd with a lower offset is behind the reader; one beyond it belongs to elements never published).
            if (Stopwatch.GetTimestamp() >= nextLivenessCheck)
            {
                nextLivenessCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 100;
                if (WriterGone(hdr))
                {
                    var orphan = new TagReader(hdr, log, logBytes, serializer, Volatile.Read(ref hdr->TagEnd));
                    cursor = Math.Max(joinW, Volatile.Read(ref hdr->WriteCursor));
                    return orphan;
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
        var reader = new TagReader(hdr, log, logBytes, serializer, end);
        reader.LoadCopied(table.AsSpan(0, used), long.MaxValue);        // every table record is state
        reader.LoadCopied(records.AsSpan(0, span), cursor);             // below the start cursor: state; at or after it: the first queued tags
        return reader;
    }

    private static bool WriterGone(ControlBlock* hdr)
        => Volatile.Read(ref hdr->WriterState) == 2 || !ProcessLiveness.IsAlive(Volatile.Read(ref hdr->WriterPid), Volatile.Read(ref hdr->WriterStartTime));

    private void LoadCopied(ReadOnlySpan<byte> records, long readCursor)
    {
        while (!records.IsEmpty)
        {
            TagRecordHeader h = records.Length >= TagRecordHeader.Bytes ? MemoryMarshal.Read<TagRecordHeader>(records) : default;
            if (!TagFormat.IsValid(h, records.Length))
            {
                throw new RingBufferLayoutException("A tag record of the snapshot is malformed.");
            }

            Accept(h, records[..h.RecordBytes], readCursor);
            records = records[h.RecordBytes..];
        }
    }

    // ------------------------------------------------------------------ steady state

    /// <summary>
    /// Loads every record in <c>[Position, end)</c>. The caller loaded <paramref name="end"/> (<c>TagEnd</c>) after the write cursor it reads up to, and has
    /// not published a read cursor past any of these records' offsets, so the writer does not reuse their bytes meanwhile (DESIGN §16.4).
    /// </summary>
    /// <returns><see cref="NextOffset"/>.</returns>
    /// <exception cref="RingBufferLayoutException">A record is malformed.</exception>
    public long Load(long end, long readCursor)
    {
        long position = _position;
        if (end - position > _logBytes)
        {
            // Unloaded records are never released (DESIGN §16.4), so they span at most the log. Anything else is a corrupt header, and trusting it could
            // copy beyond the mapping.
            throw new RingBufferLayoutException($"TagEnd {end} lies more than the tag log ({_logBytes} bytes) beyond the reader's position {position}.");
        }

        while (position < end)
        {
            TagRecordHeader h = default;
            TagFormat.Read(_log, _logBytes, position, MemoryMarshal.AsBytes(new Span<TagRecordHeader>(ref h)));
            if (!TagFormat.IsValid(h, end - position))
            {
                throw new RingBufferLayoutException($"The tag record at log position {position} is malformed.");
            }

            ReadOnlySpan<byte> record;
            if (TagFormat.IsContiguous(_logBytes, position, h.RecordBytes))
            {
                record = new ReadOnlySpan<byte>(_log + (position & (_logBytes - 1)), h.RecordBytes);
            }
            else
            {
                if (_scratch.Length < h.RecordBytes)
                {
                    _scratch = new byte[Math.Max(h.RecordBytes, 2 * _scratch.Length)];
                }

                TagFormat.Read(_log, _logBytes, position, _scratch.AsSpan(0, h.RecordBytes));
                record = _scratch.AsSpan(0, h.RecordBytes);
            }

            Accept(h, record, readCursor);
            position += h.RecordBytes;
            _position = position;                                       // a record whose tag was accepted is never loaded twice, even if a later one throws
        }

        return NextOffset;
    }

    /// <summary>The queued tags with an offset below <paramref name="end"/>, as a view of the queue.</summary>
    public ReadOnlyMemory<ITag> Before(long end)
    {
        int n = 0;
        while (n < _count && _offsets[_head + n] < end)
        {
            n++;
        }

        return new ReadOnlyMemory<ITag>(_items, _head, n);
    }

    /// <summary>
    /// Drops the queued tags with an offset below <paramref name="next"/> (the new read cursor); persistent ones become the last value of their key.
    /// Dropped entries are not cleared: a chunk that was advanced into only partly still shows every tag it held. They are overwritten once the queue has
    /// emptied, or released with the array when the queue moves to a new one.
    /// </summary>
    /// <returns><see cref="NextOffset"/>.</returns>
    public long Consume(long next)
    {
        while (_count > 0 && _offsets[_head] < next)
        {
            if (_persistent[_head])
            {
                Remember(_keys[_head], _items[_head]);
            }

            _head++;
            _count--;
        }

        if (_count == 0)
        {
            _head = 0;
        }

        return NextOffset;
    }

    /// <summary>
    /// The last persistent tag of every key before the reader's position, one per key, in the order the keys first appeared; a view of the reader's own array
    /// (no copy), valid until the reader loads or passes more tags.
    /// </summary>
    public ReadOnlySpan<ITag> LastValues() => new(_lastValues, 0, _lastCount);

    private void Accept(in TagRecordHeader h, ReadOnlySpan<byte> record, long readCursor)
    {
        ReadOnlySpan<byte> key = record.Slice(TagRecordHeader.Bytes, h.KeyBytes);
        ReadOnlySpan<byte> typeName = record.Slice(TagRecordHeader.Bytes + h.KeyBytes, h.TypeNameBytes);
        ReadOnlySpan<byte> payload = record.Slice(TagRecordHeader.Bytes + h.KeyBytes + h.TypeNameBytes, h.PayloadBytes);
        string keyText = Name(key);
        ITag tag = Materialize(h, keyText, Name(typeName), payload);
        long offset = h.Offset > long.MaxValue ? long.MaxValue : (long)h.Offset;
        if (offset < readCursor)
        {
            if (h.IsPersistent)
            {
                Remember(keyText, tag);                                 // state before the reader's position (a joining reader's snapshot)
            }

            return;
        }

        Enqueue(tag, offset, keyText, h.IsPersistent);
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

    private void Remember(string key, ITag tag)
    {
        _lastIndex ??= new Dictionary<string, int>(StringComparer.Ordinal);
        if (_lastIndex.TryGetValue(key, out int index))
        {
            _lastValues[index] = tag;
            return;
        }

        if (_lastCount == _lastValues.Length)
        {
            var grown = new ITag[Math.Max(4, _lastValues.Length * 2)];     // a new array: a span handed out earlier keeps showing the old one
            Array.Copy(_lastValues, grown, _lastCount);
            _lastValues = grown;
        }

        _lastIndex[key] = _lastCount;
        _lastValues[_lastCount++] = tag;
    }

    private void Enqueue(ITag tag, long offset, string key, bool persistent)
    {
        if (_head + _count == _items.Length)
        {
            // Never move live entries within an array: a chunk may still show them. A new array keeps the old one intact.
            int size = _count * 2 > _items.Length ? _items.Length * 2 : _items.Length;
            var items = new ITag[size];
            var offsets = new long[size];
            var keys = new string[size];
            var persistentFlags = new bool[size];
            Array.Copy(_items, _head, items, 0, _count);
            Array.Copy(_offsets, _head, offsets, 0, _count);
            Array.Copy(_keys, _head, keys, 0, _count);
            Array.Copy(_persistent, _head, persistentFlags, 0, _count);
            _items = items;
            _offsets = offsets;
            _keys = keys;
            _persistent = persistentFlags;
            _head = 0;
        }

        int i = _head + _count;
        _items[i] = tag;
        _offsets[i] = offset;
        _keys[i] = key;
        _persistent[i] = persistent;
        _count++;
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
