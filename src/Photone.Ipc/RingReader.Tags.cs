using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

public sealed unsafe partial class RingReader<T>
{
    private readonly TagReader? _tags;  // null when the buffer carries no tags
    private long _tagLoadedW;           // every record of a tag with an offset below this is loaded
    private long _tagNextOffset;        // offset of the first queued tag (long.MaxValue when none)
    private long _tagPosition;          // log position loaded up to (== _tags.Position): an unchanged TagEnd means nothing new to load
    private long _tagThreshold;         // min(_tagLoadedW, _tagNextOffset): TryRead and Advance below it skip the tag code (long.MaxValue without tags)

    /// <summary>
    /// The last persistent tag (<see cref="ITag.IsPersistent"/>) of every key whose offset lies before <see cref="ReadCursor"/>: the state in effect at the
    /// reader's position, one tag per <see cref="ITag.Key"/>, in the order the keys first appeared. It includes tags written before the reader joined (from
    /// the writer's persistent-tag table) and advances with <see cref="Advance"/>; the tags at or after <see cref="ReadCursor"/> arrive in
    /// <see cref="Chunk{T}.Tags"/> instead. A view of the reader's own array, without a copy: valid until the next <see cref="TryRead"/> or
    /// <see cref="Advance"/>. Empty when the buffer carries no tags.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    public ReadOnlySpan<ITag> ReadLastTagValues()
    {
        ThrowIfDisposed();
        return _tags is null ? default : _tags.LastValues();
    }

    /// <summary>The reader's tag state (tests).</summary>
    internal TagReader? Tags => _tags;

    /// <summary>
    /// <see cref="TryRead"/> of <c>[_r, end)</c> when <c>_tagThreshold &lt; end</c>: makes the tags below <c>end</c> loaded and returns the tag source of the
    /// chunk, or <see langword="null"/> when none of its elements carries a tag.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TagReader? TagsForRead(long end)
    {
        if (_tagLoadedW < end)
        {
            RefreshTags();
        }

        _tagThreshold = Math.Min(_tagLoadedW, _tagNextOffset);
        return _tagNextOffset < end ? _tags : null;
    }

    /// <summary>
    /// <see cref="Advance"/> to <paramref name="next"/> when <c>_tagThreshold &lt; next</c>, BEFORE the cursor store: loads what the new cursor passes (the
    /// writer reuses a record's bytes only once every reader cursor is past its commit, DESIGN §16.4), then consumes the tags passed over (persistent ones
    /// become <see cref="ReadLastTagValues"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TagsForAdvance(long next)
    {
        if (_tagLoadedW < next)
        {
            RefreshTags();
        }

        if (_tagNextOffset < next)
        {
            _tagNextOffset = _tags!.Consume(next);
        }

        _tagThreshold = Math.Min(_tagLoadedW, _tagNextOffset);
    }

    /// <summary>
    /// Makes every tag with an offset below <c>_wc</c> loaded. <c>_wc</c> was loaded before <c>TagEnd</c> is loaded here, and a commit publishes its records
    /// before its write cursor, so they are all before that <c>TagEnd</c> (DESIGN §16.6). When <c>TagEnd</c> has not moved since the last load there is
    /// nothing to parse.
    /// </summary>
    private void RefreshTags()
    {
        long w = _wc;
        long end = Volatile.Read(ref Hdr.TagEnd);
        if (end != _tagPosition)
        {
            _tagNextOffset = _tags!.Load(end, _r);
            _tagPosition = _tags.Position;
        }

        _tagLoadedW = w;
    }
}
