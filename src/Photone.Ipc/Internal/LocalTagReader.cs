namespace Photone.Ipc.Internal;

/// <summary>
/// The tags of a reader created by the writer's own buffer (DESIGN §16.5): the entries of the writer's <see cref="LocalTagLog"/>, as the instances the
/// writer added. No serialization, no shared memory.
/// </summary>
internal sealed unsafe class LocalTagReader : TagReader
{
    private readonly LocalTagLog _log;      // keeps the pinned entry count alive
    private LocalTagLog.Chunk _chunk;
    private int _index;                     // item of _chunk holding entry _position (LocalTagLog.ChunkSize: the first item of the next chunk)
    private long _position;

    private LocalTagReader(LocalTagLog log, LocalTagLog.Chunk chunk, int index, long position)
    {
        _log = log;
        _chunk = chunk;
        _index = index;
        _position = position;
    }

    /// <inheritdoc/>
    public override long* EndPointer => _log.PublishedPointer;

    /// <inheritdoc/>
    public override long Position => _position;

    /// <summary>
    /// The tag state of a reader that joins at write cursor <paramref name="joinW"/> (its slot is already Active with that cursor), and where it starts
    /// (<paramref name="cursor"/>): <paramref name="joinW"/>, or the snapshot's write cursor when that is newer. The snapshot is immutable, so one load
    /// gives a consistent state; the entries published after it are replayed: before the start cursor they are state, from it on the first queued tags.
    /// </summary>
    public static LocalTagReader Join(LocalTagLog log, long joinW, out long cursor)
    {
        LocalTagLog.Snapshot snapshot = log.Current;
        cursor = Math.Max(joinW, snapshot.W);
        long end = log.Published;                                   // loaded after the snapshot: every entry beyond it has an offset of at least the cursor
        var reader = new LocalTagReader(log, snapshot.Chunk, snapshot.Index, snapshot.End);
        foreach (KeyValuePair<string, ITag> state in snapshot.State)
        {
            reader.Remember(state.Key, state.Value);
        }

        reader.Load(end, cursor);
        return reader;
    }

    /// <inheritdoc/>
    public override long Load(long end, long readCursor)
    {
        long position = _position;
        while (position < end)
        {
            if (_index == LocalTagLog.ChunkSize)
            {
                _chunk = Volatile.Read(ref _chunk.Next) ?? throw new InvalidOperationException("The tag log ends before its published count.");
                _index = 0;
            }

            LocalTagLog.Entry entry = _chunk.Items[_index++];
            Accept(entry.Tag, entry.Offset, entry.Key, entry.Persistent, readCursor);
            _position = ++position;
        }

        return NextOffset;
    }
}
