namespace Photone.Ipc.Internal;

/// <summary>
/// Internal diagnostics counters (DESIGN §5.12); one per <c>RingBuffer</c> (writer side) and one per <c>RingReader</c>.
/// Plain increments; only <see cref="Commits"/> is on a fast path (a writer-local field).
/// </summary>
internal struct Counters
{
    /// <summary>Buckets committed (writer).</summary>
    public long Commits;

    /// <summary><c>ScanMin</c> executions (writer slow path).</summary>
    public long Scans;

    /// <summary>Kernel waits entered (writer: <c>WaitForSpace</c>; reader: <c>BlockUntil</c>).</summary>
    public long KernelWaits;

    /// <summary>Kernel signals issued (writer: reader wakes; reader: writer wakes).</summary>
    public long Signals;

    /// <summary>Waits satisfied while spinning (reader).</summary>
    public long SpinSuccesses;

    /// <summary>Kernel wakes that found the condition still false (reader).</summary>
    public long SpuriousWakes;

    /// <summary>Slots evicted by this object.</summary>
    public long Evictions;

    /// <summary>Bytes allocated on the reader's async waiter thread across all suspensions (diagnostic for the zero-allocation contract).</summary>
    public long WaiterAllocatedBytes;
}
