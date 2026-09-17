namespace Photone.Ipc;

/// <summary>
/// What a buffer's tags occupy, as a snapshot (DESIGN §16.2). Shared tag memory is committed as tags need it and never decommitted while the section
/// exists, so <see cref="CommittedBytes"/> is a high-water mark: it settles at the peak the writer needed and falls only when the section is destroyed
/// (every <see cref="RingBuffer{T}"/> and <see cref="RingReader{T}"/> on it disposed, in every process; with a pool, when the mapping expires). It is a
/// commit charge against the system limit, backed by the pagefile, not private bytes of this process.
/// </summary>
/// <remarks>
/// What only the writer knows (<see cref="UnreadTags"/>, <see cref="UnreadBytes"/>, <see cref="CurrentRingBytes"/>, <see cref="RingSwitches"/>) is 0 in a
/// process that opened the buffer; the committed sizes and <see cref="PersistentKeys"/> come from the control block and are the same everywhere. Read from
/// a thread other than the writer's, every field is atomic on its own, but they need not all belong to the same instant.
/// </remarks>
public readonly record struct TagMemoryInfo
{
    /// <summary>
    /// Shared tag memory committed in the buffer's section: <see cref="TableCommittedBytes"/> and every ring, including what buffers before this one
    /// committed in a pooled section. 0 unless the buffer carries <see cref="TagMode.CrossProcess"/> tags.
    /// </summary>
    public long CommittedBytes { get; init; }

    /// <summary>Of <see cref="CommittedBytes"/>: the persistent-tag table, which grows with <see cref="PersistentKeys"/> and never shrinks.</summary>
    public long TableCommittedBytes { get; init; }

    /// <summary>
    /// Of <see cref="CommittedBytes"/>: the ring generations. One commit per size class at most — a class the log returns to is reused, not committed
    /// again — so this stays near twice the largest ring the writer has ever needed.
    /// </summary>
    public long RingCommittedBytes => CommittedBytes - TableCommittedBytes;

    /// <summary>Size of the ring the writer appends to now: the plateau a steady state settles at. 0 outside the writer's process.</summary>
    public long CurrentRingBytes { get; init; }

    /// <summary>Ring generations the writer has started beyond the first: how much the tag volume swings. 0 outside the writer's process.</summary>
    public long RingSwitches { get; init; }

    /// <summary>
    /// Tags the writer still holds because the slowest reader has not read past them: what drives the high-water mark, and what
    /// <see cref="RingBufferOptions.MaxUnreadTags"/> bounds. An upper bound — they are released lazily, so it is exact only just after a release.
    /// 0 outside the writer's process, and for <see cref="TagMode.InProcess"/> without a tag limit, which tracks nothing.
    /// </summary>
    public long UnreadTags { get; init; }

    /// <summary>
    /// Bytes of those tags' records, the jump records between them included (<see cref="RingBufferOptions.MaxUnreadTagBytes"/>); an upper bound, like
    /// <see cref="UnreadTags"/>. 0 unless the buffer carries <see cref="TagMode.CrossProcess"/> tags.
    /// </summary>
    public long UnreadBytes { get; init; }

    /// <summary>
    /// Distinct keys of persistent tags. Each one holds a table slot and its last record for as long as the buffer lives: this is the one part of tag
    /// memory that only grows, so keep the set of keys bounded. A key per message — a GUID, a timestamp — fills the 2 GiB table and then throws.
    /// </summary>
    public int PersistentKeys { get; init; }
}
