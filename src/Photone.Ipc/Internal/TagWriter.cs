using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Photone.Ipc.Internal;

/// <summary>
/// The writer's side of the tags (DESIGN §16.4): the records of the outstanding bucket, the records published to the log together with the write cursor
/// that was published when they were appended (it decides when their bytes may be reused), the last persistent record of every key, and the snapshot a
/// joining reader starts from. Used by the writer thread only. Staging implements <see cref="IBufferWriter{T}"/> for the serializer.
/// </summary>
internal sealed unsafe class TagWriter : IBufferWriter<byte>
{
    private const int MaxCachedNames = 1024;

    private static readonly PendingOrder s_pendingOrder = new();

    private readonly ControlBlock* _hdr;
    private readonly byte* _state;
    private readonly int _stateBytes;
    private readonly byte* _log;
    private readonly long _logBytes;

    // ---- the outstanding bucket ----
    private byte[] _staging = new byte[512];
    private int _stagingLength;
    private Pending[] _pending = new Pending[8];
    private int _pendingCount;
    private long _pendingBytes;
    private long _lastPendingOffset = long.MinValue;
    private bool _pendingSorted = true;
    private int _selectedCount;
    private long _projectedStateBytes;                              // upper bound of the table after the outstanding bucket commits
    private readonly Dictionary<string, int> _pendingStateMax = new(StringComparer.Ordinal);

    // ---- the log: records in position order, [_tail, _end) ----
    private LogRecord[] _records = new LogRecord[16];               // power-of-two ring
    private int _recordHead;
    private int _recordCount;
    private long _end;
    private long _tail;

    // ---- persistent state and the snapshot ----
    private readonly Dictionary<string, byte[]> _stateRecords = new(StringComparer.Ordinal);   // no removals: enumerates in insertion order
    private long _stateUsed;
    private bool _stateDirty;
    private bool _snapshotDirty;
    private ulong _version;

    private readonly Dictionary<string, byte[]> _typeNames = new(StringComparer.Ordinal);

    public TagWriter(ControlBlock* hdr, byte* state, int stateBytes, byte* log, long logBytes)
    {
        _hdr = hdr;
        _state = state;
        _stateBytes = stateBytes;
        _log = log;
        _logBytes = logBytes;
    }

    /// <summary>Tags added to the outstanding bucket.</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Absolute log position after the last published record (== <c>ControlBlock.TagEnd</c>).</summary>
    public long End => _end;

    /// <summary>Absolute log position of the oldest record whose bytes are still reserved.</summary>
    public long Tail => _tail;

    /// <summary>Records whose bytes are still reserved.</summary>
    public int RecordCount => _recordCount;

    /// <summary>The write cursor published when the oldest reserved record was appended (<see cref="RecordCount"/> must be positive).</summary>
    public long OldestAppendW => _records[_recordHead].AppendW;

    // ------------------------------------------------------------------ staging (AddTag)

    /// <summary>
    /// Serializes <paramref name="tag"/> into a record of the outstanding bucket. On failure nothing is kept.
    /// </summary>
    /// <exception cref="ArgumentException">The key is too long, or the record alone exceeds the log.</exception>
    /// <exception cref="InvalidOperationException">The bucket's tags exceed the log, or the persistent-tag table would overflow.</exception>
    public void Stage<TTag>(ITagSerializer serializer, TTag tag, long offset, string key) where TTag : ITag
    {
        bool persistent = TTag.IsPersistent;
        byte[] typeName = TypeNameBytes(serializer.GetTypeName<TTag>());
        int keyBytes = Encoding.UTF8.GetByteCount(key);
        if (keyBytes > TagFormat.MaxNameBytes)
        {
            throw new ArgumentException($"The tag key takes {keyBytes} UTF-8 bytes; at most {TagFormat.MaxNameBytes} are allowed.", nameof(tag));
        }

        int start = _stagingLength;
        int recordBytes;
        try
        {
            int fixedBytes = TagRecordHeader.Bytes + keyBytes + typeName.Length;
            Span<byte> head = GetSpan(fixedBytes)[..fixedBytes];
            head[..TagRecordHeader.Bytes].Clear();
            Encoding.UTF8.GetBytes(key, head.Slice(TagRecordHeader.Bytes, keyBytes));
            typeName.CopyTo(head[(TagRecordHeader.Bytes + keyBytes)..]);
            _stagingLength += fixedBytes;

            serializer.Serialize(tag, this);
            long payloadBytes = (long)_stagingLength - start - fixedBytes;
            long record = ((long)_stagingLength - start + 7) & ~7L;
            if (record > _logBytes)
            {
                throw new ArgumentException($"The tag takes {record} bytes, more than the whole tag log ({_logBytes} bytes, RingBufferOptions.TagCapacity).", nameof(tag));
            }

            if (_pendingBytes + record > _logBytes)
            {
                throw new InvalidOperationException($"The tags of this bucket take more than the whole tag log ({_logBytes} bytes, RingBufferOptions.TagCapacity).");
            }

            recordBytes = (int)record;
            int padding = recordBytes - (_stagingLength - start);
            if (padding != 0)
            {
                GetSpan(padding)[..padding].Clear();
                _stagingLength += padding;
            }

            var header = new TagRecordHeader
            {
                RecordBytes = recordBytes,
                Flags = persistent ? TagFlags.Persistent : (ushort)0,
                KeyBytes = (ushort)keyBytes,
                Offset = (ulong)offset,
                TypeNameBytes = (ushort)typeName.Length,
                PayloadBytes = (int)payloadBytes,
            };
            MemoryMarshal.Write(_staging.AsSpan(start, TagRecordHeader.Bytes), in header);
            if (persistent)
            {
                ProjectState(key, recordBytes);
            }
        }
        catch
        {
            _stagingLength = start;
            throw;
        }

        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        _pending[_pendingCount] = new Pending { Offset = offset, Start = start, Length = recordBytes, Sequence = _pendingCount, Persistent = persistent, Key = key };
        _pendingCount++;
        _pendingBytes += recordBytes;
        if (offset < _lastPendingOffset)
        {
            _pendingSorted = false;
        }

        _lastPendingOffset = Math.Max(_lastPendingOffset, offset);
    }

    /// <summary>Keeps the table within bounds whatever prefix of the bucket commits: every key counts with its largest committed or pending record.</summary>
    private void ProjectState(string key, int recordBytes)
    {
        int committed = _stateRecords.TryGetValue(key, out byte[]? current) ? current.Length : 0;
        _pendingStateMax.TryGetValue(key, out int pending);
        int before = Math.Max(committed, pending);
        int after = Math.Max(before, recordBytes);
        long projected = _projectedStateBytes + after - before;
        if (projected > _stateBytes)
        {
            throw new InvalidOperationException(
                $"The last persistent tag of every key would take {projected} bytes, more than the persistent-tag table holds ({_stateBytes} bytes, RingBufferOptions.PersistentTagCapacity).");
        }

        _projectedStateBytes = projected;
        if (recordBytes > pending)
        {
            _pendingStateMax[key] = recordBytes;
        }
    }

    private byte[] TypeNameBytes(string name)
    {
        if (!_typeNames.TryGetValue(name, out byte[]? bytes))
        {
            bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length is 0 or > TagFormat.MaxNameBytes)
            {
                throw new InvalidOperationException($"The serializer's type name '{name}' must take 1 to {TagFormat.MaxNameBytes} UTF-8 bytes.");
            }

            if (_typeNames.Count < MaxCachedNames)
            {
                _typeNames[name] = bytes;
            }
        }

        return bytes;
    }

    /// <inheritdoc/>
    public void Advance(int count)
    {
        if ((uint)count > (uint)(_staging.Length - _stagingLength))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Advanced past the requested buffer.");
        }

        _stagingLength += count;
    }

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureStaging(sizeHint);
        return _staging.AsMemory(_stagingLength);
    }

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureStaging(sizeHint);
        return _staging.AsSpan(_stagingLength);
    }

    private void EnsureStaging(int sizeHint)
    {
        int needed = Math.Max(sizeHint, 1);
        if (_staging.Length - _stagingLength >= needed)
        {
            return;
        }

        long size = Math.Max((long)_staging.Length * 2, (long)_stagingLength + needed);
        if (size > Array.MaxLength)
        {
            if ((long)_stagingLength + needed > Array.MaxLength)
            {
                throw new InvalidOperationException("The tags of this bucket do not fit in memory.");
            }

            size = Array.MaxLength;
        }

        Array.Resize(ref _staging, (int)size);
    }

    // ------------------------------------------------------------------ commit

    /// <summary>Orders the bucket's tags by offset (stable) and selects those below <paramref name="limit"/> (the committed prefix); returns their bytes.</summary>
    public long SelectForCommit(long limit)
    {
        if (!_pendingSorted)
        {
            Array.Sort(_pending, 0, _pendingCount, s_pendingOrder);
            _pendingSorted = true;
        }

        int n = 0;
        long bytes = 0;
        while (n < _pendingCount && _pending[n].Offset < limit)
        {
            bytes += _pending[n].Length;
            n++;
        }

        _selectedCount = n;
        return bytes;
    }

    /// <summary><see langword="true"/> when <paramref name="bytes"/> more fit behind the reserved records.</summary>
    public bool Fits(long bytes) => _end - _tail + bytes <= _logBytes;

    /// <summary>
    /// Releases the bytes of every record appended while the published write cursor was below <paramref name="min"/>, a minimum of the reader cursors
    /// (every reader that could still load such a record has a cursor at or below that write cursor, DESIGN §16.4). Writer-local: nothing is published.
    /// </summary>
    public void Free(long min)
    {
        int mask = _records.Length - 1;
        while (_recordCount > 0 && _records[_recordHead].AppendW < min)
        {
            _recordHead = (_recordHead + 1) & mask;
            _recordCount--;
        }

        _tail = _recordCount == 0 ? _end : _records[_recordHead].Position;
    }

    /// <summary>
    /// Copies the selected records into the log (the caller made room), remembers them with <paramref name="appendW"/> (the write cursor published
    /// before this commit), folds persistent ones into the table and publishes <c>TagEnd</c>. The caller publishes the write cursor next.
    /// </summary>
    public void AppendSelected(long appendW)
    {
        Debug.Assert(_selectedCount == 0 || Fits(SelectedBytes()), "the caller must make room first");
        for (int i = 0; i < _selectedCount; i++)
        {
            Pending p = _pending[i];
            ReadOnlySpan<byte> record = _staging.AsSpan(p.Start, p.Length);
            TagFormat.Write(_log, _logBytes, _end, record);
            PushRecord(new LogRecord { Position = _end, Length = p.Length, AppendW = appendW });
            _end += p.Length;
            if (p.Persistent)
            {
                SetState(p.Key, record);
            }
        }

        if (_selectedCount != 0)
        {
            Volatile.Write(ref _hdr->TagEnd, _end);                   // after the bytes: a reader that loads the new end finds complete records
            _snapshotDirty = true;
        }
    }

    private long SelectedBytes()
    {
        long bytes = 0;
        for (int i = 0; i < _selectedCount; i++)
        {
            bytes += _pending[i].Length;
        }

        return bytes;
    }

    private void PushRecord(LogRecord record)
    {
        if (_recordCount == _records.Length)
        {
            var grown = new LogRecord[_records.Length * 2];
            for (int i = 0; i < _recordCount; i++)
            {
                grown[i] = _records[(_recordHead + i) & (_records.Length - 1)];
            }

            _records = grown;
            _recordHead = 0;
        }

        _records[(_recordHead + _recordCount) & (_records.Length - 1)] = record;
        _recordCount++;
    }

    private void SetState(string key, ReadOnlySpan<byte> record)
    {
        if (_stateRecords.TryGetValue(key, out byte[]? current) && current.Length == record.Length)
        {
            record.CopyTo(current);
        }
        else
        {
            _stateUsed += record.Length - (current?.Length ?? 0);
            _stateRecords[key] = record.ToArray();
        }

        _stateDirty = true;
    }

    /// <summary>Forgets the outstanding bucket's tags (after publishing the selected ones, or when the bucket is dropped).</summary>
    public void ClearPending()
    {
        Array.Clear(_pending, 0, _pendingCount);
        _pendingCount = 0;
        _pendingBytes = 0;
        _stagingLength = 0;
        _selectedCount = 0;
        _pendingSorted = true;
        _lastPendingOffset = long.MinValue;
        if (_pendingStateMax.Count != 0)
        {
            _pendingStateMax.Clear();
        }

        _projectedStateBytes = _stateUsed;
    }

    /// <summary>
    /// After the write cursor <paramref name="w"/> of a commit that appended records is published: the seqlock-protected snapshot for joining readers
    /// (the table if it changed, <c>TagSnapshotEnd</c> and <c>TagSnapshotW</c>; DESIGN §16.5).
    /// </summary>
    public void PublishSnapshot(long w)
    {
        if (!_snapshotDirty)
        {
            return;
        }

        ulong odd = _version + 1;
        Interlocked.Exchange(ref _hdr->TagVersion, odd);               // full fence: the odd value is in memory before any table store, including non-temporal ones of a large copy
        if (_stateDirty)
        {
            int used = 0;
            int count = 0;
            foreach (byte[] record in _stateRecords.Values)
            {
                record.CopyTo(new Span<byte>(_state + used, record.Length));
                used += record.Length;
                count++;
            }

            Debug.Assert(used <= _stateBytes, "ProjectState bounds the table");
            _hdr->TagStateUsed = used;
            _hdr->TagStateCount = count;
            _stateDirty = false;
        }

        _hdr->TagSnapshotEnd = _end;
        _hdr->TagSnapshotW = w;
        _version = odd + 1;
        Volatile.Write(ref _hdr->TagVersion, _version);
        _snapshotDirty = false;
    }

    private struct Pending
    {
        public long Offset;
        public int Start;
        public int Length;
        public int Sequence;
        public bool Persistent;
        public string Key;
    }

    private struct LogRecord
    {
        public long Position;
        public long AppendW;
        public int Length;
    }

    private sealed class PendingOrder : IComparer<Pending>
    {
        public int Compare(Pending x, Pending y) => x.Offset != y.Offset ? x.Offset.CompareTo(y.Offset) : x.Sequence.CompareTo(y.Sequence);
    }
}
