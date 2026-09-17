using System.Diagnostics;
using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

public sealed unsafe partial class RingBuffer<T>
{
    private bool _tagWork;              // tags are staged: from the first AddTag until a commit has published all of them and the snapshots
    private int _committing;            // 1 while EndWriteWithTags runs (it may map and commit tag memory): CloseWriter waits for it to leave

    private long _nextTagSweep;         // Stopwatch timestamp before which a growing shared tag log does not look for dead readers again

    /// <summary>The writer's tag state (tests).</summary>
    internal TagWriter? TagWriter => _tagWriter;

    /// <summary>
    /// What this buffer's tags occupy right now (DESIGN §16.2): the shared tag memory committed in its section, what holds that memory, and the
    /// persistent keys that never give their table slot back. A snapshot, cheap enough to poll from a metrics thread; <see cref="TagMemoryInfo"/> says
    /// which of its numbers a process that only opened the buffer can see.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The buffer was disposed.</exception>
    public TagMemoryInfo TagMemory
    {
        get
        {
            ThrowIfDisposed();
            if (_tagWriter is not TagWriter tags)                   // an opener sees what the writer publishes; in-process tags it does not see at all
            {
                return _tagMode == TagMode.CrossProcess ? PublishedTagMemory() : default;
            }

            if (tags.Shared is not SharedTagLog shared)             // InProcess: tag objects, nothing committed
            {
                return new TagMemoryInfo
                {
                    UnreadTags = tags.Local.TracksUnread ? tags.Local.UnreadEntries : 0,
                    PersistentKeys = tags.Local.PersistentKeys,
                };
            }

            long table = shared.TableCommitted;
            long committed = shared.CommittedBytes;
            if (_pooled is PooledMapping pooled)                    // a pooled section keeps what the buffers before this one committed
            {
                table = Math.Max(table, pooled.TagTableCommitted);
                committed = pooled.TagBytesWith(shared.RingCommitted, shared.TableCommitted);
            }

            return new TagMemoryInfo
            {
                CommittedBytes = committed,
                TableCommittedBytes = table,
                CurrentRingBytes = shared.CurrentRingBytes,
                RingSwitches = shared.RingSwitches,
                UnreadTags = shared.UnreadRecords,
                UnreadBytes = shared.UnreadBytes,
                PersistentKeys = shared.PersistentKeys,
            };
        }
    }

    /// <summary>
    /// <see cref="TagMemory"/> in a process that opened the buffer: the committed sizes and the key count as the writer published them in the control
    /// block, each clamped to the region it describes (another process writes them).
    /// </summary>
    private TagMemoryInfo PublishedTagMemory()
    {
        long table = Math.Clamp(Volatile.Read(ref _hdr->TagTableCommitted), 0, TagFormat.TableReserveBytes);
        long committed = table;
        for (int ring = 0; ring < TagFormat.RingClasses; ring++)
        {
            committed += Math.Clamp(Volatile.Read(ref _hdr->TagRingCommitted[ring]), 0, TagFormat.RingBytes(ring));
        }

        return new TagMemoryInfo
        {
            CommittedBytes = committed,
            TableCommittedBytes = table,
            PersistentKeys = Math.Max(0, Volatile.Read(ref _hdr->TagStateCount)),
        };
    }

    /// <summary>
    /// Attaches <paramref name="tag"/> to the next element this writer publishes, without a bucket: sets <c>tag.Offset</c> to the current
    /// <see cref="WriteCursor"/> (the first element of the outstanding bucket, if there is one); with <see cref="TagMode.CrossProcess"/> the tag is serialized
    /// now. It is published with the first commit that publishes at least one element, and stays pending through commits that publish none; it is dropped
    /// if the writer is disposed first. Readers see it in the <see cref="Chunk{T}.Tags"/> of that element. Writer thread only, like <see cref="GetBucket"/>.
    /// </summary>
    /// <typeparam name="TTag">The concrete tag type: it decides <see cref="ITag.IsPersistent"/> and the serializer's type name.</typeparam>
    /// <param name="tag">The tag; its <see cref="ITag.Offset"/> is overwritten. Readers of this buffer receive this instance: do not change or add it again.</param>
    /// <exception cref="InvalidOperationException">Not the writer, or the writer is closed; the buffer carries no tags (<see cref="RingBufferOptions.Tags"/>).</exception>
    /// <exception cref="ArgumentException"><typeparamref name="TTag"/> is an interface, or the key is <see langword="null"/> or too long.</exception>
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
            ?? throw new InvalidOperationException("This buffer carries no tags: create it with RingBufferOptions.Tags set to TagMode.InProcess or TagMode.CrossProcess.");
        ArgumentNullException.ThrowIfNull(tag);
        if (typeof(TTag).IsInterface)
        {
            throw new ArgumentException($"AddTag needs the concrete tag type, not {typeof(TTag)}: persistence and the serialized type name come from it.", nameof(tag));
        }

        string key = tag.Key ?? throw new ArgumentException("The tag's Key is null.", nameof(tag));
        tag.Offset = (ulong)offset;
        tags.Stage(tag, offset, key, sticky);
        _tagWork = true;
    }

    /// <summary>
    /// <see cref="EndWrite"/> of a bucket with tags (DESIGN §16.4): the tags and their end before the write cursor, the snapshots after it.
    /// <para>
    /// It may map and commit tag memory, and <see cref="Dispose"/> on another thread must neither drop the bucket under it nor release the mapping. Dekker
    /// pair: this stores <c>_committing</c> (full fence) and then loads <c>_disposed</c>; <c>Dispose</c> stores <c>_disposed</c> (full fence) and then, in
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

            PublishTags(count);                                     // tags and their end BEFORE the write cursor: a reader that sees W finds the tags below it
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
            TestHooks.BeforeTagSnapshot?.Invoke(this);
            _tagWriter.PublishSnapshots(_w);                        // AFTER the write cursor: a snapshot never names a cursor that is not published
            _outstanding = false;
        }
        finally
        {
            Volatile.Write(ref _committing, 0);
        }
    }

    /// <summary>
    /// Commit, before the write cursor is published: publishes the tags of the committed prefix (DESIGN §16.4). With shared memory it first releases what
    /// the readers passed if the records do not fit, then moves to another ring or commits memory as needed; it never waits. If that fails (the commit limit,
    /// tag memory exhausted), the whole bucket is dropped: nothing of it is published. Tags added to the buffer rather than to the bucket stay pending in both
    /// cases until an element is published. Runs only inside <see cref="EndWriteWithTags"/>, after its disposal check.
    /// </summary>
    /// <summary>
    /// The commit's tags do not fit the buffer's limit (<see cref="RingBufferOptions.MaxUnreadTags"/> / <see cref="RingBufferOptions.MaxUnreadTagBytes"/>)
    /// by the counters the last release left: releases what the readers passed and, while it still does not fit, waits for them (DESIGN §16.4). It waits exactly as <see cref="GetBucket"/> waits for space, on the reader
    /// cursor that releases the oldest tags, and holds a local reference so that <see cref="Dispose"/> on another thread ends it with
    /// <see cref="ObjectDisposedException"/> (<see cref="CloseWriter"/> wakes it). The bucket is dropped in that case, as for any failed commit.
    /// <para>
    /// Nothing breaks a deadlock here: a reader that waits for more elements than are published while this commit waits for that reader to read past tags
    /// stops both. The limit has to leave room for the tags of the largest chunk a reader waits for.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The commit's own tags exceed the limit, so no reader could ever make room.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnsureTagRoom(TagWriter tags, int count, long bytes)
    {
        if ((_maxUnreadTags != 0 && count > _maxUnreadTags) || (_maxUnreadTagBytes != 0 && bytes > _maxUnreadTagBytes))
        {
            throw new InvalidOperationException(
                $"The {count} tags of this commit ({bytes} bytes) exceed the buffer's tag limit by themselves (MaxUnreadTags {_maxUnreadTags}, " +
                $"MaxUnreadTagBytes {_maxUnreadTagBytes}): no reader could make room for them. The bucket was dropped.");
        }

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
                ReleaseTags(tags, _min);
                if (TagRoom(tags, count, bytes))
                {
                    return;
                }

                long target = TagReleaseTarget(tags);
                if (target == long.MinValue)
                {
                    throw new InvalidOperationException(                    // unreachable: a commit that fits an empty log was let through above
                        $"The {count} tags of this commit ({bytes} bytes) do not fit the buffer's tag limit although the writer holds none. The bucket was dropped.");
                }

                _counters.TagWaits++;
                WaitForMin(target);                                 // a reader cursor past the oldest tags releases them
            }
        }
        finally
        {
            ReleaseLocalRef();
        }
    }

    /// <summary>
    /// <see langword="true"/> when this commit's tags fit in the buffer's limit, given what the writer still holds for the slowest reader (the counters as
    /// the last release left them: they only over-estimate, so a commit that passes here needs no scan and no wait).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TagRoom(TagWriter tags, int count, long bytes)
    {
        if (_maxUnreadTags != 0 && UnreadTags(tags) + count > _maxUnreadTags)
        {
            return false;
        }

        return _maxUnreadTagBytes == 0 || tags.Shared!.UnreadBytes + bytes <= _maxUnreadTagBytes;
    }

    private static long UnreadTags(TagWriter tags) => tags.Shared is SharedTagLog shared ? shared.UnreadRecords : tags.Local.UnreadEntries;

    private static void ReleaseTags(TagWriter tags, long min)
    {
        if (tags.Shared is SharedTagLog shared)
        {
            shared.Free(min);
        }
        else
        {
            tags.Local.Release(min);
        }
    }

    /// <summary>The minimum reader cursor that releases the oldest tags the writer holds; <see cref="long.MinValue"/> when it holds none.</summary>
    private static long TagReleaseTarget(TagWriter tags)
    {
        if (tags.Shared is SharedTagLog shared)
        {
            return shared.HasReserved ? shared.OldestAppendW + 1 : long.MinValue;
        }

        return tags.Local.UnreadEntries != 0 ? tags.Local.OldestUnreadOffset + 1 : long.MinValue;
    }

    /// <summary>
    /// The shared tags do not fit and the log would move to a larger ring: evicts readers whose process died first. Nothing else would while the data ring
    /// still has space, and a dead reader's cursor keeps every later record reserved (DESIGN §16.4). At most once per liveness interval: a sweep checks
    /// the process of every reader.
    /// </summary>
    /// <returns><see langword="true"/> when a reader was evicted.</returns>
    private bool SweepBeforeGrowing()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextTagSweep)
        {
            return false;
        }

        _nextTagSweep = now + SpinClock.ToTicks(TimeSpan.FromMilliseconds(_livenessMs));
        return SweepDeadSlots() != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PublishTags(int count)
    {
        TagWriter tags = _tagWriter!;
        try
        {
            int selected = tags.SelectForCommit(_w + count);
            if (selected != 0)
            {
                if ((_maxUnreadTags | _maxUnreadTagBytes) != 0 && !TagRoom(tags, selected, tags.SelectedBytes))
                {
                    EnsureTagRoom(tags, selected, tags.SelectedBytes);   // the only place a commit can wait for readers because of tags
                }

                tags.PrepareLocal();                                // everything that can fail comes first; the appends cannot
                if (tags.Shared is SharedTagLog shared)
                {
                    long bytes = tags.SelectedBytes;
                    if (shared.NeedsFree(bytes))
                    {
                        shared.Free(_min);
                        if (shared.NeedsFree(bytes))
                        {
                            _min = ScanMin();                       // a record is reusable once every reader cursor is past the write cursor it was appended at
                            shared.Free(_min);
                            if (!shared.Fits(bytes) && SweepBeforeGrowing())
                            {
                                _min = ScanMin();
                                shared.Free(_min);
                            }
                        }
                    }

                    shared.Prepare(bytes, tags.StateChanges, appendW: _w);
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
}
