using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

public sealed unsafe partial class RingBuffer<T>
{
    private bool _tagWork;              // tags are staged: from the first AddTag until a commit has published all of them and the tag snapshot
    private int _committing;            // 1 while EndWriteWithTags runs (it may wait for tag space): CloseWriter waits for it to leave

    /// <summary>The writer's tag state (tests).</summary>
    internal TagWriter? TagWriter => _tagWriter;

    /// <summary>
    /// Attaches <paramref name="tag"/> to the next element this writer publishes, without a bucket: sets <c>tag.Offset</c> to the current
    /// <see cref="WriteCursor"/> (the first element of the outstanding bucket, if there is one) and serializes the tag now. It is published with the
    /// first commit that publishes at least one element, and stays pending through commits that publish none; it is dropped if the writer is disposed
    /// first. Readers see it in the <see cref="Chunk{T}.Tags"/> of that element. Writer thread only, like <see cref="GetBucket"/>.
    /// </summary>
    /// <typeparam name="TTag">The concrete tag type: it decides <see cref="ITag.IsPersistent"/> and the serializer's type name.</typeparam>
    /// <param name="tag">The tag; its <see cref="ITag.Offset"/> is overwritten.</param>
    /// <exception cref="InvalidOperationException">
    /// Not the writer, or the writer is closed; the buffer carries no tags or has no <see cref="RingBufferOptions.TagSerializer"/>; the pending tags exceed
    /// the tag log, or the last persistent tag of every key would exceed the persistent-tag table.
    /// </exception>
    /// <exception cref="ArgumentException"><typeparamref name="TTag"/> is an interface, the key is <see langword="null"/> or too long, or the tag alone exceeds the tag log.</exception>
    /// <exception cref="ObjectDisposedException">The buffer was disposed.</exception>
    public void AddTag<TTag>(TTag tag) where TTag : ITag
    {
        ThrowIfDisposed();
        if (!_isWriter)
        {
            throw new InvalidOperationException("Only the creating process can write tags; this buffer was opened in the reader role.");
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The writer has been closed.");
        }

        StageTag(tag, _w, sticky: true);
    }

    /// <summary><see cref="Bucket{T}.AddTag{TTag}"/>: element <paramref name="index"/> of the bucket that starts at <paramref name="cursor"/> and holds <paramref name="length"/> elements.</summary>
    internal void AddBucketTag<TTag>(long cursor, int length, int index, TTag tag) where TTag : ITag
    {
        ThrowIfDisposed();
        if (!_outstanding || cursor != _w || cursor + length != _e || Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The bucket is no longer outstanding.");
        }

        StageTag(tag, cursor + index, sticky: false);
    }

    private void StageTag<TTag>(TTag tag, long offset, bool sticky) where TTag : ITag
    {
        TagWriter tags = _tagWriter
            ?? throw new InvalidOperationException("This buffer carries no tags: create it with RingBufferOptions.TagCapacity greater than 0.");
        ITagSerializer serializer = _options.TagSerializer
            ?? throw new InvalidOperationException("RingBufferOptions.TagSerializer is not set: the writer cannot serialize tags (for example new JsonTagSerializer()).");
        ArgumentNullException.ThrowIfNull(tag);
        if (typeof(TTag).IsInterface)
        {
            throw new ArgumentException($"AddTag needs the concrete tag type, not {typeof(TTag)}: persistence and the serialized type name come from it.", nameof(tag));
        }

        string key = tag.Key ?? throw new ArgumentException("The tag's Key is null.", nameof(tag));
        tag.Offset = (ulong)offset;
        tags.Stage(serializer, tag, offset, key, sticky);
        _tagWork = true;
    }

    /// <summary>
    /// <see cref="EndWrite"/> of a bucket with tags (DESIGN §16.4): the records and <c>TagEnd</c> before the write cursor, the snapshot after it.
    /// <para>
    /// It may wait for tag space, and <see cref="Dispose"/> on another thread must neither drop the bucket under it nor release the mapping. Dekker pair:
    /// this stores <c>_committing</c> (full fence) and then loads <c>_disposed</c>; <c>Dispose</c> stores <c>_disposed</c> (full fence) and then, in
    /// <see cref="CloseWriter"/>, loads <c>_committing</c>. So either this sees the disposal and leaves without touching anything (the closing thread drops
    /// the bucket), or the closing thread sees this commit and waits until it has completed or given up.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndWriteWithTags(int count)
    {
        Interlocked.Exchange(ref _committing, 1);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            PublishTags(count);                                     // records and TagEnd BEFORE the write cursor: a reader that sees W finds the tags below it
            _w += count;
            _e = _w;
            Interlocked.Exchange(ref Hdr.WriteCursor, _w);
            _counters.Commits++;
            ulong m = Volatile.Read(ref Hdr.WaitersMask);
            if (m != 0)
            {
                SignalReaders(m, _w);
            }

            _tagWork = _tagWriter!.PendingCount != 0;               // tags added to the buffer wait for a commit that publishes an element
            _tagWriter.PublishSnapshot(_w);                         // AFTER the write cursor: a snapshot never names a cursor that is not published
            _outstanding = false;
        }
        finally
        {
            Volatile.Write(ref _committing, 0);
        }
    }

    /// <summary>
    /// Commit, before the write cursor is published: appends the records of the committed prefix to the log and publishes <c>TagEnd</c> (DESIGN §16.4).
    /// Waits while the log is full. If it fails (the buffer disposed meanwhile, <see cref="TagLogFullException"/>), the whole bucket is dropped: nothing
    /// of it is published. Tags added to the buffer rather than to the bucket stay pending in both cases until an element is published.
    /// Runs only inside <see cref="EndWriteWithTags"/>, after its disposal check, so no closing thread touches the bucket meanwhile.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PublishTags(int count)
    {
        TagWriter tags = _tagWriter!;
        try
        {
            long bytes = tags.SelectForCommit(_w + count);
            if (bytes != 0)
            {
                if (!tags.Fits(bytes))
                {
                    tags.Free(_min);
                    if (!tags.Fits(bytes))
                    {
                        MakeTagSpace(tags, bytes);
                    }
                }

                TestHooks.BeforeTagAppend?.Invoke(this);
                tags.AppendSelected(appendW: _w);
            }

            tags.ClearPending(keepSticky: true);
        }
        catch
        {
            tags.ClearPending(keepSticky: true);
            _tagWork = tags.PendingCount != 0;
            _e = _w;
            _outstanding = false;
            throw;
        }
    }

    /// <summary>
    /// The tag log is full: rescans the reader cursors, releases what they passed, and otherwise waits (spin, then kernel) until the slowest reader passes
    /// the write cursor of the oldest record. Holds a local reference like <see cref="SlowGetBucket"/>, so a <see cref="Dispose"/> on another thread ends
    /// the wait with <see cref="ObjectDisposedException"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MakeTagSpace(TagWriter tags, long bytes)
    {
        if (!TryAddLocalRef())
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        try
        {
            while (true)
            {
                ThrowIfDisposed();
                _min = ScanMin();
                tags.Free(_min);
                if (tags.Fits(bytes))
                {
                    return;
                }

                _counters.TagWaits++;
                WaitForMin(tags.OldestAppendW + 1, tagWait: true);   // a record is reusable once every reader cursor is past the write cursor it was appended at
            }
        }
        finally
        {
            ReleaseLocalRef();
        }
    }
}
