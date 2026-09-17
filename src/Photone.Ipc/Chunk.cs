using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>
/// A readable window of exactly <see cref="Length"/> elements returned by <see cref="RingReader{T}.TryRead"/>. Contiguous even when it wraps
/// around the end of the ring (double mapping). Valid until <see cref="RingReader{T}.Advance"/> moves the reader past it.
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
public readonly struct Chunk<T> where T : unmanaged
{
    private readonly ReadOnlyMemory<T> _data;
    private readonly long _cursor;
    private readonly TagReader? _tags;      // the reader's tags, when some of them belong to this chunk

    internal Chunk(ReadOnlyMemory<T> data, long cursor, TagReader? tags = null)
    {
        _data = data;
        _cursor = cursor;
        _tags = tags;
    }

    /// <summary>
    /// The readable elements: a window into the shared ring itself, without a copy (<c>Data.Span</c> to read it, <c>Data.Pin()</c> for its address).
    /// It may be stored and handed to code that needs <see cref="ReadOnlyMemory{T}"/> - async code, a stream, a pipeline - but the elements belong to
    /// the mapping, not to the chunk: past <see cref="RingReader{T}.Advance"/> the writer may overwrite them, and past the reader's
    /// <see cref="RingReader{T}.Dispose"/> the memory may be unmapped. Nothing here keeps either alive.
    /// </summary>
    public ReadOnlyMemory<T> Data => _data;

    /// <summary>Number of elements.</summary>
    public int Length => _data.Length;

    /// <summary>Absolute element index of <c>Data.Span[0]</c>.</summary>
    public long Cursor => _cursor;

    /// <summary>Absolute element index of <c>Data.Span[0]</c> as the unsigned offset tags use (<see cref="ITag.Offset"/>); equal to <see cref="Cursor"/>.</summary>
    public ulong StartOffset => (ulong)_cursor;

    /// <summary>
    /// The tags attached to this chunk's elements (<c>StartOffset &lt;= tag.Offset &lt; StartOffset + Length</c>), in offset order and, at equal offsets,
    /// in the order the writer added them. Empty when there are none. The memory belongs to the reader: valid until <see cref="RingReader{T}.Advance"/>
    /// moves past the tags, like the chunk itself (read after a partial advance, it holds the tags the reader has not passed yet).
    /// </summary>
    public ReadOnlyMemory<ITag> Tags => _tags is null ? default : _tags.Before(_cursor + _data.Length);
}
