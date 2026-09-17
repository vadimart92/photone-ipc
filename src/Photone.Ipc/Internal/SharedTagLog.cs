using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>
/// The writer's side of the tags in shared memory (DESIGN §16.4): tag records in ring generations inside the tag reserve, the records still reserved
/// together with the write cursor published when they were appended (it decides when their bytes may be reused), the last persistent record of every
/// key, and the seqlock-protected snapshot a joining reader starts from. The log starts in the smallest ring; when a commit's records do not fit behind
/// the records readers still need, a jump record ends the generation and the log continues at offset 0 of a larger ring, and after a burst it moves back
/// to a smaller one. Memory is committed as records reach it. Writer thread only.
/// <para>
/// A commit runs <see cref="Prepare"/>, which does everything that can fail (mapping, committing, allocating) before anything changes, then
/// <see cref="Append"/> for each record and <see cref="PublishEnd"/>, which cannot fail, then, after the write cursor, <see cref="PublishSnapshot"/>.
/// </para>
/// </summary>
internal sealed unsafe class SharedTagLog
{
    private const long ShrinkFactor = 16;                           // shrink when the live records and the commit take at most 1/16 of the ring ...
    private const long ShrinkPeriods = 4;                           // ... after at least 4 ring sizes were appended in this generation

    private readonly ControlBlock* _hdr;
    private readonly TagViews _views;

    // ---- the current generation ----
    private int _ring;                                              // size class; the log starts in ring 0 at position 0
    private long _ringStart;                                        // log position of physical offset 0
    private long _ringBytes = TagFormat.MinRingBytes;
    private long _generationBytes;                                  // bytes appended since the generation started
    private readonly long[] _ringCommitted = new long[TagFormat.RingClasses];
    private readonly int[] _ringGroups = new int[TagFormat.RingClasses];     // reserved groups per ring (a ring is reusable at 0)

    // ---- the log: reserved groups in position order, [_tail, _end); a group is what one commit appended to one ring (one AppendW) ----
    private LogGroup[] _groups = new LogGroup[16];                  // power-of-two ring
    private int _groupHead;
    private int _groupCount;
    private long _end;
    private long _tail;
    private long _reservedRecords;                                  // records (not jump records) whose bytes are still reserved: the tags the writer holds for readers

    // ---- persistent state: the last record of every key, in the order the keys appeared, as the table lays them out ----
    private readonly Dictionary<string, int> _stateIndex = new(StringComparer.Ordinal);
    private StateRecord[] _state = [];
    private int _stateCount;
    private int _stateValid;                                        // records [0, _stateValid) lie in the table at their Position with their length
    private int[] _stateChanged = [];                               // records below _stateValid overwritten in place since the last snapshot
    private int _stateChangedCount;
    private long _stateUsed;
    private long _tableCommitted;
    private bool _stateDirty;
    private bool _snapshotDirty;
    private ulong _version;

    // ---- the commit being prepared ----
    private readonly Dictionary<string, PreparedState> _prepared = new(StringComparer.Ordinal);   // key → its last persistent record in the commit

    public SharedTagLog(ControlBlock* hdr, TagViews views)
    {
        _hdr = hdr;
        _views = views;
    }

    /// <summary>Absolute log position after the last appended record (== <c>ControlBlock.TagEnd</c> once published).</summary>
    public long End => _end;

    /// <summary>Absolute log position of the oldest record whose bytes are still reserved.</summary>
    public long Tail => _tail;

    /// <summary>Tag records the writer still holds for the slowest reader (<see cref="RingBufferOptions.MaxUnreadTags"/>).</summary>
    public long UnreadRecords => _reservedRecords;

    /// <summary>Bytes of those records, including the jump records between them (<see cref="RingBufferOptions.MaxUnreadTagBytes"/>).</summary>
    public long UnreadBytes => _end - _tail;

    /// <summary><see langword="true"/> when any records are still reserved.</summary>
    public bool HasReserved => _groupCount != 0;

    /// <summary>The write cursor published when the oldest reserved group was appended; a reader cursor past it releases that group (<see cref="Free"/>).</summary>
    public long OldestAppendW => _groups[_groupHead].AppendW;

    /// <summary>Size class of the ring the log currently appends to.</summary>
    public int CurrentRing => _ring;

    /// <summary>Size of that ring.</summary>
    public long CurrentRingBytes => _ringBytes;

    /// <summary>Keys with a record in the persistent-tag table: one slot each, held for the buffer's life.</summary>
    public int PersistentKeys => _stateCount;

    /// <summary>Generations started after the first one (ring changes).</summary>
    public int RingSwitches { get; private set; }

    /// <summary>Bytes committed in every ring by this writer.</summary>
    public ReadOnlySpan<long> RingCommitted => _ringCommitted;

    /// <summary>Bytes of the table region committed by this writer.</summary>
    public long TableCommitted => _tableCommitted;

    /// <summary>Bytes of tag memory committed by this writer: the table region and every ring.</summary>
    public long CommittedBytes
    {
        get
        {
            long bytes = _tableCommitted;
            foreach (long ring in _ringCommitted)
            {
                bytes += ring;
            }

            return bytes;
        }
    }

    private long LiveInRing => _end - Math.Max(_ringStart, _tail);

    private bool ShrinkDue => _ring > 0 && _generationBytes >= ShrinkPeriods * _ringBytes;

    /// <summary><see langword="true"/> when the current ring has the room for <paramref name="bytes"/> of records and the jump record that may follow them.</summary>
    public bool Fits(long bytes) => LiveInRing + bytes + TagRecordHeader.Bytes <= _ringBytes;

    /// <summary>
    /// <see langword="true"/> when a commit of <paramref name="bytes"/> should first release what the readers passed: its records do not fit, or the
    /// generation is due for a look at shrinking (<see cref="Prepare"/> decides on the live records that remain).
    /// </summary>
    public bool NeedsFree(long bytes) => !Fits(bytes) || ShrinkDue;

    /// <summary>
    /// Releases the bytes of every record appended while the published write cursor was below <paramref name="min"/>, a minimum of the reader cursors
    /// (every reader that could still load such a record has a cursor at or below that write cursor, DESIGN §16.4). Writer-local: nothing is published.
    /// </summary>
    public void Free(long min)
    {
        int mask = _groups.Length - 1;
        while (_groupCount > 0 && _groups[_groupHead].AppendW < min)
        {
            _ringGroups[_groups[_groupHead].Ring]--;
            _reservedRecords -= _groups[_groupHead].Records;
            _groupHead = (_groupHead + 1) & mask;
            _groupCount--;
        }

        _tail = _groupCount == 0 ? _end : _groups[_groupHead].Position;
    }

    // ------------------------------------------------------------------ prepare (everything that can fail)

    /// <summary>
    /// Makes room for a commit of <paramref name="bytes"/> of records, whose persistent records are <paramref name="state"/> (the caller has freed what the
    /// readers passed): moves to a larger ring when they do not fit, or to a smaller one after a burst; commits the memory the records, the jump record and
    /// the table will use; allocates what <see cref="Append"/> and <see cref="PublishSnapshot"/> will need. Every step that can fail comes before anything
    /// changes, so a failure leaves the log as it was. A jump record is appended with <paramref name="appendW"/>, like the commit's records, and published
    /// with them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tags that readers still need exceed every free ring, or the persistent state exceeds its region.</exception>
    /// <exception cref="PhotoneIpcException">The system commit limit is reached.</exception>
    /// <exception cref="OutOfMemoryException">Not enough managed memory for the bookkeeping.</exception>
    public void Prepare(long bytes, ReadOnlySpan<StateChange> state, long appendW)
    {
        int next = _ring;
        if (!Fits(bytes))
        {
            next = LargerRing(bytes);
        }
        else if (ShrinkDue)
        {
            next = (LiveInRing + bytes + TagRecordHeader.Bytes) * ShrinkFactor <= _ringBytes ? SmallerRing(bytes) : _ring;
            if (next == _ring)
            {
                _generationBytes = 0;                               // still in use: look again after another period
            }
        }

        long table = PrepareState(state);
        EnsureGroupCapacity(_groupCount + 2);                       // the jump's group and the records' group
        if (next != _ring)
        {
            Commit(_ring, Physical(_end), TagRecordHeader.Bytes);   // the jump record
            Commit(next, 0, bytes);
        }
        else if (bytes != 0)
        {
            Commit(_ring, Physical(_end), bytes);
        }

        CommitTable(table);

        if (next != _ring)
        {
            TagRecordHeader jump = TagRecordHeader.Jump(next);
            TagFormat.Write(_views.Ring(_ring).Address, _ringBytes, Physical(_end), MemoryMarshal.AsBytes(new ReadOnlySpan<TagRecordHeader>(ref jump)));
            AddToGroup(TagRecordHeader.Bytes, appendW, records: 0);     // a jump record is not a tag
            _end += TagRecordHeader.Bytes;
            _ring = next;
            _ringStart = _end;
            _ringBytes = TagFormat.RingBytes(next);
            _generationBytes = 0;
            RingSwitches++;
        }
    }

    private long Physical(long position) => (position - _ringStart) & (_ringBytes - 1);

    /// <summary>
    /// The ring for a commit that does not fit: the smallest free one of at least twice the current ring or twice the commit, or failing that, of at least
    /// the commit. Free: not the current ring, and no reserved record in it.
    /// </summary>
    private int LargerRing(long bytes)
    {
        long need = bytes + TagRecordHeader.Bytes;
        int ring = FreeRingFrom(TagFormat.RingFor(Math.Max(2 * _ringBytes, 2 * need)));
        if (ring < 0)
        {
            ring = FreeRingFrom(TagFormat.RingFor(need));
        }

        if (ring < 0)
        {
            throw new InvalidOperationException(
                $"The tags that readers have not read past, and the {bytes} bytes of this commit, exceed the tag memory: no free ring holds them (the largest ring " +
                $"holds {TagFormat.RingBytes(TagFormat.RingClasses - 1)} bytes). The bucket was dropped.");
        }

        return ring;
    }

    private int FreeRingFrom(int ring)
    {
        for (; ring >= 0 && ring < TagFormat.RingClasses; ring++)
        {
            if (ring != _ring && _ringGroups[ring] == 0)
            {
                return ring;
            }
        }

        return -1;
    }

    /// <summary>After a burst: a free ring of about 8 times the live records and the commit, at least 4 times smaller; the current one when there is none.</summary>
    private int SmallerRing(long bytes)
    {
        long need = LiveInRing + bytes + TagRecordHeader.Bytes;
        for (int ring = TagFormat.RingFor(8 * need); ring >= 0 && ring < _ring - 1; ring++)
        {
            if (_ringGroups[ring] == 0)
            {
                return ring;
            }
        }

        return _ring;
    }

    /// <summary>Commits <c>[physical, physical + length)</c> of <paramref name="ring"/> (all of it when the range wraps), in doubling steps.</summary>
    private void Commit(int ring, long physical, long length)
    {
        long ringBytes = TagFormat.RingBytes(ring);
        long needed = physical + length > ringBytes ? ringBytes : physical + length;
        long committed = _ringCommitted[ring];
        if (needed <= committed)
        {
            return;
        }

        long target = Math.Min(ringBytes, Math.Max(TagFormat.AlignView(needed), 2 * committed));
        _views.Ring(ring).Commit(committed, target - committed);
        _ringCommitted[ring] = target;
        Volatile.Write(ref _hdr->TagRingCommitted[ring], target);   // after the commit, before any record lies beyond the old value
    }

    private void CommitTable(long bytes)
    {
        if (bytes <= _tableCommitted)
        {
            return;
        }

        long target = Math.Min(TagFormat.TableReserveBytes, Math.Max(TagFormat.AlignView(bytes), 2 * _tableCommitted));
        SectionView view = _views.Table(target);
        view.Commit(_tableCommitted, target - _tableCommitted);
        _tableCommitted = target;
        Volatile.Write(ref _hdr->TagTableCommitted, target);        // after the commit, before the table grows past the old value
    }

    private void EnsureGroupCapacity(int count)
    {
        if (count <= _groups.Length)
        {
            return;
        }

        var grown = new LogGroup[BitOperations.RoundUpToPowerOf2((uint)count)];
        for (int i = 0; i < _groupCount; i++)
        {
            grown[i] = _groups[(_groupHead + i) & (_groups.Length - 1)];
        }

        _groups = grown;
        _groupHead = 0;
    }

    /// <summary>
    /// The persistent records of the commit: which record is the last of its key (only that one reaches the table), the buffer it will be copied into when
    /// its key is new or it outgrows the old buffer, and room for new keys. Returns the size of the table after the commit.
    /// </summary>
    private long PrepareState(ReadOnlySpan<StateChange> state)
    {
        _prepared.Clear();
        if (state.IsEmpty)
        {
            return _stateUsed;
        }

        foreach (StateChange change in state)
        {
            _prepared[change.Key] = new PreparedState(change.Index, null);   // the last record of a key wins
        }

        long used = _stateUsed;
        int newKeys = 0;
        foreach (StateChange change in state)
        {
            ref PreparedState prepared = ref CollectionsMarshal.GetValueRefOrNullRef(_prepared, change.Key);
            if (prepared.Index != change.Index)
            {
                continue;                                           // a later record of the same key replaces this one
            }

            if (_stateIndex.TryGetValue(change.Key, out int i))
            {
                used += change.RecordBytes - _state[i].Length;
                if (change.RecordBytes > _state[i].Buffer.Length)
                {
                    prepared.Buffer = new byte[BufferBytes(change.RecordBytes)];
                }
            }
            else
            {
                used += change.RecordBytes;
                newKeys++;
                prepared.Buffer = new byte[BufferBytes(change.RecordBytes)];
            }
        }

        if (used > Array.MaxLength)
        {
            throw new InvalidOperationException($"The last persistent tag of every key would take {used} bytes; the persistent-tag table holds at most {Array.MaxLength}. The bucket was dropped.");
        }

        int capacity = _stateCount + newKeys;
        if (_state.Length < capacity)
        {
            Array.Resize(ref _state, Math.Max(capacity, Math.Max(4, 2 * _state.Length)));
            Array.Resize(ref _stateChanged, _state.Length);
        }

        _stateIndex.EnsureCapacity(capacity);
        return used;
    }

    /// <summary>A state buffer: the record's size, or a power of two above it for larger records whose size may drift.</summary>
    private static int BufferBytes(int recordBytes) => recordBytes <= 64 ? recordBytes : (int)Math.Min(Array.MaxLength, BitOperations.RoundUpToPowerOf2((uint)recordBytes));

    // ------------------------------------------------------------------ append (cannot fail)

    /// <summary>
    /// Copies record <paramref name="index"/> of the commit into the current ring (the caller prepared the room) and, when it is the last persistent record of
    /// its key in the commit, folds it into the table (writer-local).
    /// </summary>
    public void Append(ReadOnlySpan<byte> record, long appendW, bool persistent, string key, int index)
    {
        Debug.Assert(LiveInRing + record.Length <= _ringBytes, "Prepare made room");
        TagFormat.Write(_views.Ring(_ring).Address, _ringBytes, Physical(_end), record);
        AddToGroup(record.Length, appendW, records: 1);
        _end += record.Length;
        _generationBytes += record.Length;
        if (persistent)
        {
            ref PreparedState prepared = ref CollectionsMarshal.GetValueRefOrNullRef(_prepared, key);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref prepared) && prepared.Index == index)
            {
                SetState(key, record, prepared.Buffer);
            }
        }
    }

    /// <summary>Reserves <paramref name="length"/> bytes at <c>_end</c> in the current ring: extends the commit's group, or starts one (capacity was prepared).</summary>
    private void AddToGroup(long length, long appendW, int records)
    {
        _reservedRecords += records;
        int mask = _groups.Length - 1;
        if (_groupCount > 0)
        {
            ref LogGroup last = ref _groups[(_groupHead + _groupCount - 1) & mask];
            if (last.AppendW == appendW && last.Ring == _ring && last.Position + last.Length == _end)
            {
                last.Length += length;
                last.Records += records;
                return;
            }
        }

        _groups[(_groupHead + _groupCount) & mask] = new LogGroup { Position = _end, Length = length, AppendW = appendW, Ring = _ring, Records = records };
        _groupCount++;
        _ringGroups[_ring]++;
    }

    private void SetState(string key, ReadOnlySpan<byte> record, byte[]? buffer)
    {
        if (_stateIndex.TryGetValue(key, out int i))
        {
            ref StateRecord s = ref _state[i];
            if (record.Length == s.Length)
            {
                record.CopyTo(s.Buffer);
                if (i < _stateValid && !s.Changed)
                {
                    s.Changed = true;                               // same size: rewritten in place by the next snapshot
                    _stateChanged[_stateChangedCount++] = i;
                }
            }
            else
            {
                s.Buffer = buffer ?? s.Buffer;
                record.CopyTo(s.Buffer);
                _stateUsed += record.Length - s.Length;
                s.Length = record.Length;
                _stateValid = Math.Min(_stateValid, i);             // this record and every later one move
            }
        }
        else
        {
            i = _stateCount++;
            _state[i] = new StateRecord { Buffer = buffer!, Length = record.Length };
            record.CopyTo(_state[i].Buffer);
            _stateIndex[key] = i;
            _stateUsed += record.Length;
        }

        _stateDirty = true;
    }

    /// <summary>Publishes <c>TagEnd</c> after the appended records (and a jump record, if any); the caller publishes the write cursor next.</summary>
    public void PublishEnd()
    {
        Volatile.Write(ref _hdr->TagEnd, _end);                       // after the bytes: a reader that loads the new end finds complete records
        _snapshotDirty = true;
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>
    /// After the write cursor <paramref name="w"/> of a commit that appended records is published: the seqlock-protected snapshot for joining readers
    /// (the table where it changed, <c>TagSnapshotEnd</c> with the ring generation that holds it, and <c>TagSnapshotW</c>; DESIGN §16.6). A record that kept
    /// its size is rewritten in place; from the first record whose size changed on, the table is laid out again; new keys are appended. The table memory
    /// was committed by <see cref="Prepare"/>, so nothing here can fail.
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
            Debug.Assert(_stateUsed <= _tableCommitted, "Prepare committed the table");
            byte* table = _views.Table(_stateUsed).Address;
            for (int c = 0; c < _stateChangedCount; c++)
            {
                ref StateRecord s = ref _state[_stateChanged[c]];
                s.Changed = false;
                if (_stateChanged[c] < _stateValid)
                {
                    s.Buffer.AsSpan(0, s.Length).CopyTo(new Span<byte>(table + s.Position, s.Length));
                }
            }

            _stateChangedCount = 0;
            long position = _stateValid == 0 ? 0 : _state[_stateValid - 1].Position + _state[_stateValid - 1].Length;
            for (int i = _stateValid; i < _stateCount; i++)
            {
                ref StateRecord s = ref _state[i];
                s.Position = position;
                s.Changed = false;
                s.Buffer.AsSpan(0, s.Length).CopyTo(new Span<byte>(table + position, s.Length));
                position += s.Length;
            }

            Debug.Assert(position == _stateUsed, "the table holds every key's last record");
            _stateValid = _stateCount;
            _hdr->TagStateUsed = (int)position;
            _hdr->TagStateCount = _stateCount;
            _stateDirty = false;
        }

        _hdr->TagSnapshotEnd = _end;
        _hdr->TagSnapshotW = w;
        _hdr->TagSnapshotRing = _ring;
        _hdr->TagSnapshotRingStart = _ringStart;
        _version = odd + 1;
        Volatile.Write(ref _hdr->TagVersion, _version);
        _snapshotDirty = false;
    }

    /// <summary>A persistent record of the commit being prepared: its key, size, and index in the commit's selection.</summary>
    public readonly record struct StateChange(string Key, int RecordBytes, int Index);

    private record struct PreparedState(int Index, byte[]? Buffer);

    private struct LogGroup
    {
        public long Position;
        public long Length;
        public long AppendW;
        public int Ring;
        public int Records;
    }

    private struct StateRecord
    {
        public byte[] Buffer;
        public int Length;
        public long Position;
        public bool Changed;
    }
}
