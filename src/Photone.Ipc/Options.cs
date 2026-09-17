using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>Options for <see cref="RingBuffer{T}.Create"/> and <see cref="RingBuffer{T}.Open(string, RingBufferOptions?)"/>.</summary>
public sealed class RingBufferOptions
{
    /// <summary>
    /// Initial writer spin budget in <see cref="RingBuffer{T}.GetBucket"/> before a kernel wait (the full ring waits for a reader).
    /// Zero = block immediately; negative = spin forever. Default 20 µs. The budget adapts between 0 and <see cref="MaxSpinTime"/>.
    /// </summary>
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);

    /// <summary>
    /// Upper bound of the adaptive writer spin budget. <see langword="null"/> (default) = <see cref="SpinTime"/>: the budget never exceeds it but drops
    /// to zero while space keeps arriving later than that. Larger values spin through any wait shorter than this bound (lower latency, more CPU).
    /// </summary>
    public TimeSpan? MaxSpinTime { get; init; }

    /// <summary>Slice used while the writer is blocked (liveness backstop for readers whose process handle is unavailable). Default 10 ms.</summary>
    public TimeSpan LivenessCheckInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long <c>Open</c> waits for the creator to finish initialising (creator liveness is checked meanwhile). Default 5 s.</summary>
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Exact 64 KiB-aligned base address to request (creator only). <see langword="null"/> = hashed hint in the quiet window; 0 = no hint.</summary>
    public ulong? PreferredBaseAddress { get; init; }

    /// <summary>Touch every data page once at creation (removes first-touch page faults from the hot path). Default <see langword="true"/>.</summary>
    public bool PreFault { get; init; } = true;

    /// <summary>
    /// Pool to take the shared-memory mapping from and to return it to when the buffer is released (creator and opener; see <see cref="RingBufferPool"/>).
    /// <see langword="null"/> (default) = every buffer maps and unmaps its own section.
    /// </summary>
    public RingBufferPool? Pool { get; init; }

    /// <summary>
    /// Creator only: bytes of tag records that can be written but not yet read past by the slowest reader (rounded up to a power of two, at least 4 KiB).
    /// 0 (default) = the buffer carries no tags. A commit whose tags do not fit waits, like <see cref="RingBuffer{T}.GetBucket"/> waits for space, until the
    /// slowest reader has read past older tags. See <see cref="Bucket{T}.AddTag{TTag}"/>.
    /// </summary>
    public long TagCapacity { get; init; }

    /// <summary>
    /// Creator only: bytes for the last persistent tag of every key (the state a joining reader starts from, see <see cref="ITag.IsPersistent"/>).
    /// Used only with <see cref="TagCapacity"/> &gt; 0; the tag area is rounded up to 64 KiB and the slack goes here. Default 16 KiB.
    /// </summary>
    public int PersistentTagCapacity { get; init; } = TagFormat.DefaultStateBytes;

    /// <summary>
    /// How this process turns tags into bytes and back (writer and readers). <see langword="null"/> (default): the writer cannot add tags, and readers
    /// deliver every tag as an <see cref="UnknownTag"/>. Typically <c>new JsonTagSerializer().Register&lt;MyTag&gt;()</c>.
    /// </summary>
    public ITagSerializer? TagSerializer { get; init; }

    internal static readonly RingBufferOptions Default = new();
}

/// <summary>Options for <see cref="RingBuffer{T}.CreateReader"/>.</summary>
public sealed class ReaderOptions
{
    /// <summary>
    /// Initial spin budget of a wait before it blocks in the kernel (<see cref="RingReader{T}.WaitSync(int)"/> on the calling thread,
    /// <see cref="RingReader{T}.Wait(int, CancellationToken)"/> on the reader's waiter thread). Zero = block immediately; negative = spin forever.
    /// Default 20 µs. The budget adapts between 0 and <see cref="MaxSpinTime"/> from the gaps the reader actually observes.
    /// </summary>
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);

    /// <summary>
    /// Upper bound of the adaptive spin budget. <see langword="null"/> (default) = <see cref="SpinTime"/>: the reader never spins longer than that, and stops
    /// spinning altogether while data keeps arriving later than that. A larger bound makes the reader spin through every gap shorter than it: delivery latency
    /// drops from a kernel wake-up (about 8 µs, or 60 µs once the core has gone into a deep idle state) to well under a microsecond, at the cost of up to
    /// one core while traffic is that dense. Typical low-latency setting: 1-2 ms.
    /// </summary>
    public TimeSpan? MaxSpinTime { get; init; }

    /// <summary>
    /// Spin budget of <see cref="RingReader{T}.Wait(int, CancellationToken)"/> on the caller's thread before handing the wait to the waiter thread
    /// (never more than the current adaptive budget). Default 5 µs.
    /// </summary>
    public TimeSpan AsyncSpinTime { get; init; } = TimeSpan.FromMicroseconds(5);

    /// <summary>
    /// Run the continuation of a suspended <see cref="RingReader{T}.Wait(int, CancellationToken)"/> directly on the reader's waiter thread instead of queueing
    /// it to the thread pool. Default <see langword="true"/>.
    /// <para>
    /// An <c>await</c> loop over the reader then runs on that thread (a thread that exists only for this reader) until it awaits something else, and
    /// every further <c>Wait</c> made there runs synchronously: no thread hop, no system call, no suspension while the loop keeps up. Measured
    /// cross-process, this turns an awaited delivery from 20-30 µs into the spinning latency of <see cref="RingReader{T}.WaitSync(int)"/>, and avoids the
    /// thread-pool stalls that spinning readers otherwise provoke.
    /// </para>
    /// <para>
    /// Set it to <see langword="false"/> when the code after the <c>await</c> must run on the thread pool, or when it may block on work that needs a
    /// different wait of this same reader to be served from another thread. Ignored when the awaiting code captured a synchronization context.
    /// </para>
    /// </summary>
    public bool AllowSynchronousContinuations { get; init; } = true;

    internal static readonly ReaderOptions Default = new();
}
