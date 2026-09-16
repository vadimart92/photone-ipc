using Photone.Ipc.Internal;

namespace Photone.Ipc.Signaling;

/// <summary>Identifiers stored in <c>ControlBlock.SignalBackendId</c>; an opener instantiates whatever the header names.</summary>
internal static class SignalBackendId
{
    /// <summary>33 named auto-reset events (v1 baseline).</summary>
    public const uint NamedEvent = 1;

    // 2 = NtAlertThread, 3 = RawNtEvent, 4 = WaitOnAddress (in-process), 5 = Spin — reserved for the next phase.
}

/// <summary>
/// The swappable signaling layer (DESIGN §6). The protocol (WaitersMask / WriterWaiting / WaitFor / WriterWaitFor handshakes, spin phases,
/// ScanMin, join, eviction) lives entirely in <c>RingBuffer</c>/<c>RingReader</c>; a backend is called only at the "now block" and
/// "now signal" points and may use only the reserved layout words (<c>SignalBackendId</c>, per-slot <c>WaiterThreadId</c>/<c>BackendWord</c>,
/// <c>WriterWaiterThreadId</c>/<c>WriterBackendWord</c>, the 128-byte area at 2560), so switching backends never changes the layout.
/// </summary>
internal abstract unsafe class SignalBackend : IDisposable
{
    /// <summary>The backend id (see <see cref="SignalBackendId"/>).</summary>
    public abstract uint Id { get; }

    /// <summary>
    /// Factory. The creator passes the configured id (and has already written the header); openers pass <c>hdr->SignalBackendId</c>.
    /// </summary>
    /// <param name="id">Backend id.</param>
    /// <param name="instanceId">The buffer's random instance id (names kernel objects).</param>
    /// <param name="hdr">The control block (backend-private words may be used).</param>
    /// <param name="isCreator"><see langword="true"/> for the creating process.</param>
    /// <param name="globalNamespace"><see langword="true"/> when the section lives in <c>Global\</c> (creator only; openers read the backend area).</param>
    /// <exception cref="RingBufferLayoutException">Unknown backend id.</exception>
    public static SignalBackend Create(uint id, ulong instanceId, ControlBlock* hdr, bool isCreator, bool globalNamespace)
    {
        return id switch
        {
            SignalBackendId.NamedEvent => new NamedEventBackend(instanceId, hdr, isCreator, globalNamespace),
            _ => throw new RingBufferLayoutException($"Unknown signal backend id {id}; this library supports id 1 (NamedEvent) only."),
        };
    }

    // ---- reader side (called only by the slot owner) ----

    /// <summary>At claim, before the slot becomes Active: discard stale state of a previous owner, publish backend words.</summary>
    public abstract void OnSlotClaimed(int slot, ReaderSlot* s);

    /// <summary>At release: clear backend words.</summary>
    public abstract void OnSlotReleased(int slot, ReaderSlot* s);

    /// <summary>
    /// Block until woken, until <paramref name="writerProcess"/> (0 = none) is signaled, or until <paramref name="timeoutMs"/> elapses.
    /// Called AFTER the Dekker handshake; spurious returns are allowed (the caller re-checks).
    /// A backend that cannot wait on a process handle must return <see cref="WaitOutcome.Timeout"/> no later than <paramref name="timeoutMs"/>.
    /// </summary>
    public abstract WaitOutcome WaitForData(int slot, nint writerProcess, uint timeoutMs);

    // ---- writer side ----

    /// <summary>Called immediately before <c>Exchange(WriterWaiting, 1)</c>: publish backend words.</summary>
    public abstract void OnWriterBlocking(ControlBlock* hdr);

    /// <summary>
    /// Block until <see cref="WakeWriter"/>, until one of <paramref name="processes"/> is signaled (<paramref name="exitedIndex"/>), or until
    /// <paramref name="timeoutMs"/>. <c>processes.Length &lt;= 32</c>. Spurious returns allowed.
    /// </summary>
    public abstract WaitOutcome WaitForSpace(ReadOnlySpan<nint> processes, uint timeoutMs, out int exitedIndex);

    // ---- signals (callable from ANY process that has the buffer open, including the waiter's own) ----

    /// <summary>Wake reader <paramref name="slot"/> (after the caller consumed its WaitersMask bit).</summary>
    public abstract void WakeReader(int slot);

    /// <summary>Wake the writer (after <c>Exchange(WriterWaiting, 0) == 1</c>).</summary>
    public abstract void WakeWriter();

    /// <summary>Wake every reader (writer close).</summary>
    public abstract void WakeAllReaders();

    /// <inheritdoc/>
    public abstract void Dispose();
}
