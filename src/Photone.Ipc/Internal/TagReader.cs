namespace Photone.Ipc.Internal;

/// <summary>
/// A reader's side of the tags (DESIGN §16.7): the tags loaded whose offset the reader has not passed yet (in offset order), and the last persistent tag of
/// every key before the reader's position. <see cref="LocalTagReader"/> takes them from the writer's object log in this process,
/// <see cref="SharedTagReader"/> from the records in shared memory. Single-consumer, like the reader that owns it.
/// </summary>
internal abstract unsafe class TagReader
{
    private ITag[] _items = new ITag[8];    // queue [_head, _head + _count): never compacted in place, so memory handed out in a chunk keeps its contents
    private long[] _offsets = new long[8];
    private string[] _keys = new string[8];
    private bool[] _persistent = new bool[8];
    private int _head;
    private int _count;
    private ITag[] _lastValues = [];        // the last persistent tag of each key, in the order the keys first appeared
    private int _lastCount;
    private Dictionary<string, int>? _lastIndex;

    /// <summary>Where the published end of the log lives (<c>TagEnd</c>, or the object log's entry count); loaded after the write cursor.</summary>
    public abstract long* EndPointer { get; }

    /// <summary>The position of the next record or entry to load (the same unit as <see cref="EndPointer"/>).</summary>
    public abstract long Position { get; }

    /// <summary>Offset of the first queued tag; <see cref="long.MaxValue"/> when the queue is empty.</summary>
    public long NextOffset => _count == 0 ? long.MaxValue : _offsets[_head];

    /// <summary>Queued tags (tests).</summary>
    public int QueuedCount => _count;

    /// <summary>
    /// Loads everything published before <paramref name="end"/> (loaded from <see cref="EndPointer"/> after the write cursor the reader reads up to). The
    /// reader has not published a read cursor past any of it, so none of it is released meanwhile (DESIGN §16.4).
    /// </summary>
    /// <returns><see cref="NextOffset"/>.</returns>
    /// <exception cref="RingBufferLayoutException">A record in shared memory is malformed.</exception>
    public abstract long Load(long end, long readCursor);

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

    /// <summary>A loaded tag: state when it lies before <paramref name="readCursor"/> (a joining reader's snapshot), otherwise the next queued tag.</summary>
    protected void Accept(ITag tag, long offset, string key, bool persistent, long readCursor)
    {
        if (offset < readCursor)
        {
            if (persistent)
            {
                Remember(key, tag);
            }

            return;
        }

        Enqueue(tag, offset, key, persistent);
    }

    /// <summary>Makes <paramref name="tag"/> the last value of <paramref name="key"/>.</summary>
    protected void Remember(string key, ITag tag)
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
}
