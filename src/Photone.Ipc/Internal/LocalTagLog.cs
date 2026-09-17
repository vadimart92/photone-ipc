using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>
/// The writer's tags as objects, for the readers of the writer's own <see cref="RingBuffer{T}"/> (DESIGN §16.5): an append-only chain of chunks that
/// the writer thread fills and readers walk concurrently, a published entry count (stored before the write cursor, like <c>TagEnd</c>), and an immutable
/// snapshot that a joining reader starts from. Nothing is ever freed explicitly: a chunk is garbage once no reader and no snapshot references it, so the
/// memory follows the slowest reader without any bookkeeping.
/// </summary>
internal sealed unsafe class LocalTagLog
{
    public const int ChunkSize = 256;

    private readonly long[] _published = GC.AllocateArray<long>(1, pinned: true);   // pinned: readers load it through a pointer, like TagEnd
    private readonly Dictionary<string, int> _lastIndex = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, ITag>> _lastValues = [];              // the last persistent tag of every key, in first-seen order
    private Chunk _tail;
    private int _tailCount;
    private Chunk _last;                                            // the end of the chain: chunks after _tail were linked by Reserve, still empty
    private long _room = ChunkSize;                                 // free entries in _tail and the chunks after it
    private long _count;
    private Snapshot _snapshot;

    public LocalTagLog()
    {
        _tail = new Chunk();
        _last = _tail;
        _snapshot = new Snapshot(0, 0, _tail, 0, []);
    }

    /// <summary>The published entry count, loaded by readers after the write cursor.</summary>
    public long* PublishedPointer => (long*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_published));

    /// <summary>The published entry count.</summary>
    public long Published => Volatile.Read(ref _published[0]);

    /// <summary>The snapshot a joining reader starts from.</summary>
    public Snapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Makes room for <paramref name="entries"/> more entries, <paramref name="persistent"/> of them persistent (chunks linked at the end of the chain, room
    /// for new keys), so that the <see cref="Append"/>s of a commit allocate nothing. A linked chunk is invisible to readers until a published count reaches it.
    /// </summary>
    /// <exception cref="OutOfMemoryException">Not enough managed memory.</exception>
    public void Reserve(int entries, int persistent)
    {
        while (_room < entries)
        {
            var chunk = new Chunk();
            Volatile.Write(ref _last.Next, chunk);                  // before any count that reaches into it is published
            _last = chunk;
            _room += ChunkSize;
        }

        if (persistent != 0)
        {
            _lastIndex.EnsureCapacity(_lastIndex.Count + persistent);
            _lastValues.EnsureCapacity(_lastValues.Count + persistent);
        }
    }

    /// <summary>Appends a tag (writer thread; <see cref="Reserve"/> made room). Readers cannot see it before <see cref="Publish"/>.</summary>
    public void Append(ITag tag, string key, long offset, bool persistent)
    {
        if (_tailCount == ChunkSize)
        {
            _tail = _tail.Next!;
            _tailCount = 0;
        }

        _tail.Items[_tailCount++] = new Entry(tag, key, offset, persistent);
        _room--;
        _count++;
        if (persistent)
        {
            if (_lastIndex.TryGetValue(key, out int index))
            {
                _lastValues[index] = new KeyValuePair<string, ITag>(key, tag);
            }
            else
            {
                _lastIndex[key] = _lastValues.Count;
                _lastValues.Add(new KeyValuePair<string, ITag>(key, tag));
            }
        }
    }

    /// <summary>Publishes the appended entries; the commit stores its write cursor next.</summary>
    public void Publish() => Volatile.Write(ref _published[0], _count);

    /// <summary>
    /// After the write cursor <paramref name="w"/> is published: once a chunk's worth of entries has accumulated since the current snapshot, replaces it
    /// with one as of <paramref name="w"/>. A joiner replays the entries after its snapshot, so a stale snapshot is still exact; renewing it bounds that
    /// replay and releases the chunks before it.
    /// </summary>
    public void AfterCommit(long w)
    {
        if (_count - _snapshot.End >= ChunkSize)
        {
            Volatile.Write(ref _snapshot, new Snapshot(w, _count, _tail, _tailCount, [.. _lastValues]));
        }
    }

    /// <summary>A block of entries; <see cref="Next"/> is set before the first entry after the block is published.</summary>
    public sealed class Chunk
    {
        public readonly Entry[] Items = new Entry[ChunkSize];
        public Chunk? Next;
    }

    /// <summary>A tag as the writer added it.</summary>
    public readonly record struct Entry(ITag Tag, string Key, long Offset, bool Persistent);

    /// <summary>
    /// Every entry before <see cref="End"/> has an offset below <see cref="W"/>, and <see cref="State"/> holds the last persistent tag of every key among them.
    /// The entry at <see cref="End"/> is item <see cref="Index"/> of <see cref="Chunk"/> (<see cref="ChunkSize"/>: the first item of its successor).
    /// </summary>
    public sealed class Snapshot(long w, long end, Chunk chunk, int index, KeyValuePair<string, ITag>[] state)
    {
        public long W { get; } = w;

        public long End { get; } = end;

        public Chunk Chunk { get; } = chunk;

        public int Index { get; } = index;

        public KeyValuePair<string, ITag>[] State { get; } = state;
    }
}
