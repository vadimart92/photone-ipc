using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Photone.Ipc.Internal;

/// <summary>
/// The writer's tags (DESIGN §16.4): the tags of the outstanding bucket and the tags added to the buffer that wait for an element, the selection a commit
/// publishes, and the two places it publishes them to: the object log for the writer's own readers (<see cref="LocalTagLog"/>, always) and the records in
/// shared memory for readers in other processes (<see cref="SharedTagLog"/>, <see cref="TagMode.CrossProcess"/> only). Used by the writer thread only.
/// Staging implements <see cref="IBufferWriter{T}"/> for the serializer.
/// </summary>
internal sealed class TagWriter : IBufferWriter<byte>
{
    private const int MaxCachedNames = 1024;

    private static readonly PendingOrder s_pendingOrder = new();

    private readonly ITagSerializer? _serializer;                   // CrossProcess: records are serialized when the tag is added

    // ---- the outstanding bucket and the tags added to the buffer ----
    private byte[] _staging = [];
    private int _stagingLength;
    private Pending[] _pending = new Pending[8];
    private int _pendingCount;
    private long _lastPendingOffset = long.MinValue;
    private bool _pendingSorted = true;
    private int _selectedCount;
    private long _selectedBytes;
    private int _selectedPersistent;
    private int _appendedCount;                                     // the selected tags AppendSelected published (0 until it completes)
    private SharedTagLog.StateChange[] _stateChanges = [];
    private int _stateChangeCount;
    private readonly Dictionary<string, byte[]> _typeNames = new(StringComparer.Ordinal);

    public TagWriter(ITagSerializer? serializer, SharedTagLog? shared)
    {
        _serializer = serializer;
        Shared = shared;
    }

    /// <summary>The tags as objects, for the readers of the writer's own buffer.</summary>
    public LocalTagLog Local { get; } = new();

    /// <summary>The tags in shared memory; <see langword="null"/> unless the buffer's tags cross processes.</summary>
    public SharedTagLog? Shared { get; }

    /// <summary>Tags waiting for a commit.</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Bytes of the records <see cref="SelectForCommit"/> selected (0 without shared memory).</summary>
    public long SelectedBytes => _selectedBytes;

    // ------------------------------------------------------------------ staging (AddTag)

    /// <summary>
    /// Stages <paramref name="tag"/> at <paramref name="offset"/>; with shared memory, serializes it into a record now. On failure nothing is kept.
    /// A <paramref name="sticky"/> tag (added to the buffer, not to a bucket) stays pending through commits that do not publish its element.
    /// </summary>
    /// <exception cref="ArgumentException">The key is too long for a record.</exception>
    /// <exception cref="InvalidOperationException">The tags waiting for a commit do not fit in memory.</exception>
    public void Stage<TTag>(TTag tag, long offset, string key, bool sticky) where TTag : ITag
    {
        bool persistent = TTag.IsPersistent;
        int start = _stagingLength;
        int recordBytes = 0;
        if (_serializer is not null)
        {
            recordBytes = Serialize(_serializer, tag, offset, key, persistent);
        }

        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        _pending[_pendingCount] = new Pending { Offset = offset, Start = start, Length = recordBytes, Sequence = _pendingCount, Persistent = persistent, Sticky = sticky, Key = key, Tag = tag };
        _pendingCount++;
        if (offset < _lastPendingOffset)
        {
            _pendingSorted = false;
        }

        _lastPendingOffset = Math.Max(_lastPendingOffset, offset);
    }

    private int Serialize<TTag>(ITagSerializer serializer, TTag tag, long offset, string key, bool persistent) where TTag : ITag
    {
        byte[] typeName = TypeNameBytes(serializer.GetTypeName<TTag>());
        int keyBytes = Encoding.UTF8.GetByteCount(key);
        if (keyBytes > TagFormat.MaxNameBytes)
        {
            throw new ArgumentException($"The tag key takes {keyBytes} UTF-8 bytes; at most {TagFormat.MaxNameBytes} are allowed.", nameof(tag));
        }

        int start = _stagingLength;
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
            if (record > int.MaxValue)
            {
                throw new InvalidOperationException($"The tag takes {record} bytes; a record holds at most {int.MaxValue}.");
            }

            int padding = (int)record - (_stagingLength - start);
            if (padding != 0)
            {
                GetSpan(padding)[..padding].Clear();
                _stagingLength += padding;
            }

            var header = new TagRecordHeader
            {
                RecordBytes = (int)record,
                Flags = persistent ? TagFlags.Persistent : (ushort)0,
                KeyBytes = (ushort)keyBytes,
                Offset = (ulong)offset,
                TypeNameBytes = (ushort)typeName.Length,
                PayloadBytes = (int)payloadBytes,
            };
            MemoryMarshal.Write(_staging.AsSpan(start, TagRecordHeader.Bytes), in header);
            return (int)record;
        }
        catch
        {
            _stagingLength = start;
            throw;
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

        long size = Math.Max(Math.Max((long)_staging.Length * 2, 512), (long)_stagingLength + needed);
        if (size > Array.MaxLength)
        {
            if ((long)_stagingLength + needed > Array.MaxLength)
            {
                throw new InvalidOperationException("The tags waiting for a commit do not fit in memory.");
            }

            size = Array.MaxLength;
        }

        Array.Resize(ref _staging, (int)size);
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Orders the pending tags by offset (stable) and selects those below <paramref name="limit"/> (the committed prefix); returns how many. With shared
    /// memory, <see cref="SelectedBytes"/> is their record bytes and <see cref="StateChanges"/> their persistent records.
    /// </summary>
    public int SelectForCommit(long limit)
    {
        if (!_pendingSorted)
        {
            Array.Sort(_pending, 0, _pendingCount, s_pendingOrder);
            _pendingSorted = true;
        }

        int n = 0;
        long bytes = 0;
        _stateChangeCount = 0;
        _selectedPersistent = 0;
        while (n < _pendingCount && _pending[n].Offset < limit)
        {
            bytes += _pending[n].Length;
            if (_pending[n].Persistent)
            {
                _selectedPersistent++;
                if (Shared is not null)
                {
                    if (_stateChangeCount == _stateChanges.Length)
                    {
                        Array.Resize(ref _stateChanges, Math.Max(4, _stateChanges.Length * 2));
                    }

                    _stateChanges[_stateChangeCount++] = new SharedTagLog.StateChange(_pending[n].Key, _pending[n].Length, n);
                }
            }

            n++;
        }

        _selectedCount = n;
        _selectedBytes = bytes;
        return n;
    }

    /// <summary>Reserves what appending the selection to the object log needs (chunks, room for new keys), so that <see cref="AppendSelected"/> cannot fail.</summary>
    /// <exception cref="OutOfMemoryException">Not enough managed memory.</exception>
    public void PrepareLocal() => Local.Reserve(_selectedCount, _selectedPersistent);

    /// <summary>The persistent records among the selected tags, in commit order.</summary>
    public ReadOnlySpan<SharedTagLog.StateChange> StateChanges => _stateChanges.AsSpan(0, _stateChangeCount);

    /// <summary>
    /// Publishes the selected tags (the caller ran <see cref="PrepareLocal"/> and <see cref="SharedTagLog.Prepare"/>): appends them to the object log and,
    /// with shared memory, copies their records with <paramref name="appendW"/> (the write cursor published before this commit). Then publishes the entry
    /// count and <c>TagEnd</c>; the caller publishes the write cursor next. Allocates nothing and cannot fail.
    /// </summary>
    public void AppendSelected(long appendW)
    {
        for (int i = 0; i < _selectedCount; i++)
        {
            ref Pending p = ref _pending[i];
            Local.Append(p.Tag, p.Key, p.Offset, p.Persistent);
            Shared?.Append(_staging.AsSpan(p.Start, p.Length), appendW, p.Persistent, p.Key, index: i);
        }

        if (_selectedCount != 0)
        {
            Local.Publish();
            Shared?.PublishEnd();
        }

        _appendedCount = _selectedCount;
    }

    /// <summary>After the write cursor <paramref name="w"/> of a commit is published: the snapshots joining readers start from.</summary>
    public void PublishSnapshots(long w)
    {
        Local.AfterCommit(w);
        Shared?.PublishSnapshot(w);
    }

    /// <summary>
    /// After a commit (or when its bucket is dropped): forgets the published tags and the unpublished tags of the bucket. With <paramref name="keepSticky"/>,
    /// the unpublished tags that were added to the buffer stay pending, their records moved to the front of the staging area (in offset order, as they were).
    /// </summary>
    public void ClearPending(bool keepSticky)
    {
        int kept = 0;
        int length = 0;
        if (keepSticky)
        {
            for (int i = _appendedCount; i < _pendingCount; i++)
            {
                Pending p = _pending[i];
                if (!p.Sticky)
                {
                    continue;
                }

                if (p.Length != 0)
                {
                    _staging.AsSpan(p.Start, p.Length).CopyTo(_staging.AsSpan(length));   // overlapping copies move correctly
                }

                p.Start = length;
                p.Sequence = kept;
                _pending[kept++] = p;
                length += p.Length;
            }
        }

        Array.Clear(_pending, kept, _pendingCount - kept);                  // the dropped tags are not kept alive
        _pendingCount = kept;
        _stagingLength = length;
        _selectedCount = 0;
        _selectedBytes = 0;
        _selectedPersistent = 0;
        _appendedCount = 0;
        Array.Clear(_stateChanges, 0, _stateChangeCount);
        _stateChangeCount = 0;
        _pendingSorted = true;                                          // a sorted selection leaves its remainder sorted
        _lastPendingOffset = kept == 0 ? long.MinValue : _pending[kept - 1].Offset;
    }

    private struct Pending
    {
        public long Offset;
        public int Start;
        public int Length;
        public int Sequence;
        public bool Persistent;
        public bool Sticky;
        public string Key;
        public ITag Tag;
    }

    private sealed class PendingOrder : IComparer<Pending>
    {
        public int Compare(Pending x, Pending y) => x.Offset != y.Offset ? x.Offset.CompareTo(y.Offset) : x.Sequence.CompareTo(y.Sequence);
    }
}
