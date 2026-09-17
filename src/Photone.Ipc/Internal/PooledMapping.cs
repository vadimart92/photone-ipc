using Photone.Ipc.Signaling;

namespace Photone.Ipc.Internal;

/// <summary>
/// One section's mapping in this process together with its signaling objects, as a <see cref="RingBufferPool"/> hands it out and takes it back
/// (DESIGN §15). While a buffer uses it, the buffer owns it; while it is idle, the pool does.
/// </summary>
internal sealed class PooledMapping
{
    private readonly long[] _tagRingCommitted = new long[TagFormat.RingClasses];
    private int _idle;                                              // 1 while the pool holds the mapping

    public PooledMapping(MirroredSection mapping, SignalBackend backend, long dataBytes, long tagReserveBytes, ulong sectionId, string? sectionName, bool global, bool isCreator)
    {
        Mapping = mapping;
        Backend = backend;
        DataBytes = dataBytes;
        TagReserveBytes = tagReserveBytes;
        SectionId = sectionId;
        SectionName = sectionName;
        Global = global;
        IsCreator = isCreator;
    }

    /// <summary>The three views; an idle opener mapping has its section handle detached.</summary>
    public MirroredSection Mapping { get; }

    /// <summary>The 33 events, named from <see cref="SectionId"/>.</summary>
    public SignalBackend Backend { get; }

    /// <summary>Size of the data region (the pool's size class, with <see cref="TagReserveBytes"/>).</summary>
    public long DataBytes { get; }

    /// <summary>Size of the tag reserve after the data: <see cref="TagFormat.ReserveBytes"/> for a section made for cross-process tags, otherwise 0.</summary>
    public long TagReserveBytes { get; }

    /// <summary>
    /// Bytes committed in every ring of the tag reserve by the buffers that used this section as creators, in this process: the union of what each one
    /// committed, since pages stay committed for the section's life (DESIGN §16.2). Kept here rather than read from the shared control block, which other
    /// processes can write.
    /// </summary>
    public ReadOnlySpan<long> TagRingCommitted => _tagRingCommitted;

    /// <summary>Bytes of the table region committed, as <see cref="TagRingCommitted"/>.</summary>
    public long TagTableCommitted { get; private set; }

    /// <summary>The committed tag memory: <see cref="TagTableCommitted"/> and every ring.</summary>
    public long TagBytes
    {
        get
        {
            long bytes = TagTableCommitted;
            foreach (long ring in _tagRingCommitted)
            {
                bytes += ring;
            }

            return bytes;
        }
    }

    /// <summary>What the mapping counts against <see cref="RingBufferPool.IdleBytes"/>: the data region and the committed tag memory.</summary>
    public long PooledBytes => DataBytes + TagBytes;

    /// <summary>Adds what a creator's writer committed (before the mapping goes back to the pool; the value must not change while it is idle).</summary>
    public void AddTagCommitted(ReadOnlySpan<long> rings, long table)
    {
        for (int i = 0; i < _tagRingCommitted.Length && i < rings.Length; i++)
        {
            _tagRingCommitted[i] = Math.Max(_tagRingCommitted[i], rings[i]);
        }

        TagTableCommitted = Math.Max(TagTableCommitted, table);
    }

    /// <summary><c>ControlBlock.SectionId</c>: constant for the section's life.</summary>
    public ulong SectionId { get; }

    /// <summary>Kernel name of the section (creator mappings only; openers look parked mappings up by <see cref="SectionId"/>).</summary>
    public string? SectionName { get; }

    /// <summary>The section and its events live in <c>Global\</c>.</summary>
    public bool Global { get; }

    /// <summary><see langword="true"/>: this process created the section and may reuse it for its next buffer; <see langword="false"/>: a parked opener mapping.</summary>
    public bool IsCreator { get; }

    /// <summary>Stopwatch timestamp of the return to the pool (guarded by the pool's lock).</summary>
    public long IdleSince { get; set; }

    /// <summary>Called by the pool when it takes the mapping back.</summary>
    /// <exception cref="InvalidOperationException">The mapping is already idle: it would be handed to two buffers.</exception>
    public void MarkIdle()
    {
        if (Interlocked.Exchange(ref _idle, 1) != 0)
        {
            throw new InvalidOperationException("The mapping has already been returned to its pool.");
        }
    }

    /// <summary>Called by the pool when it hands the mapping to a buffer.</summary>
    public void MarkInUse() => Volatile.Write(ref _idle, 0);

    /// <summary>Unmaps the views, closes the section handle and the events.</summary>
    public void Release()
    {
        Backend.Dispose();
        Mapping.Dispose();
    }
}
