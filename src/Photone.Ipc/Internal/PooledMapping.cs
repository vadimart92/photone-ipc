using Photone.Ipc.Signaling;

namespace Photone.Ipc.Internal;

/// <summary>
/// One section's mapping in this process together with its signaling objects, as a <see cref="RingBufferPool"/> hands it out and takes it back
/// (DESIGN §15). While a buffer uses it, the buffer owns it; while it is idle, the pool does.
/// </summary>
internal sealed class PooledMapping
{
    private int _idle;                                              // 1 while the pool holds the mapping

    public PooledMapping(MirroredSection mapping, SignalBackend backend, long dataBytes, ulong sectionId, string? sectionName, bool global, bool isCreator)
    {
        Mapping = mapping;
        Backend = backend;
        HeaderBytes = (long)mapping.HeaderBytes;
        DataBytes = dataBytes;
        SectionId = sectionId;
        SectionName = sectionName;
        Global = global;
        IsCreator = isCreator;
    }

    /// <summary>The three views; an idle opener mapping has its section handle detached.</summary>
    public MirroredSection Mapping { get; }

    /// <summary>The 33 events, named from <see cref="SectionId"/>.</summary>
    public SignalBackend Backend { get; }

    /// <summary>Size of the header view: the control view and the tag area (part of the pool's size class; <c>ControlBlock.DataOffset</c>).</summary>
    public long HeaderBytes { get; }

    /// <summary>Size of the data region (the pool's size class, with <see cref="HeaderBytes"/>).</summary>
    public long DataBytes { get; }

    /// <summary>What the mapping counts against <see cref="RingBufferPool.IdleBytes"/>: the data region and the tag area.</summary>
    public long PooledBytes => DataBytes + HeaderBytes - Layout.HeaderViewBytes;

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
