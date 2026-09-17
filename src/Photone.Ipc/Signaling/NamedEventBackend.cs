using System.Globalization;
using Windows.Win32.Foundation;
using Microsoft.Win32.SafeHandles;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Signaling;

/// <summary>
/// v1 backend (<see cref="SignalBackendId.NamedEvent"/>): 32 auto-reset events <c>Local\photone.{SectionId:x16}.r{i:D2}</c> (one per reader slot)
/// and one auto-reset event <c>Local\photone.{SectionId:x16}.space</c> for the writer, all created-or-opened with <c>CreateEventW</c>
/// (DESIGN §6.2). Auto-reset plus at most one waiter per event means a stale set is always consumed by the owner's next wait as a harmless
/// spurious wake. The only backend-private state is byte 0 of the backend area: 1 when the objects live in <c>Global\</c>.
/// The names follow the section, not the buffer, so a pooled section keeps its events for every buffer it serves (DESIGN §15).
/// </summary>
internal sealed unsafe class NamedEventBackend : SignalBackend
{
    private const int SlotCount = Layout.MaxReaders;
    private const int SpaceIndex = SlotCount;

    private readonly SafeWaitHandle[] _handles = new SafeWaitHandle[SlotCount + 1];
    private readonly nint[] _raw = new nint[SlotCount + 1];
    private int _disposed;

    /// <summary>Creates (creator) or opens (opener) the 33 events for <paramref name="sectionId"/>.</summary>
    public NamedEventBackend(ulong sectionId, ControlBlock* hdr, bool isCreator, bool globalNamespace)
    {
        if (isCreator)
        {
            hdr->BackendArea[0] = globalNamespace ? (byte)1 : (byte)0;
        }
        else
        {
            globalNamespace = hdr->BackendArea[0] == 1;
        }

        string prefix = (globalNamespace ? "Global\\photone." : "Local\\photone.") + sectionId.ToString("x16", CultureInfo.InvariantCulture);
        bool createdAny = false;
        try
        {
            for (int i = 0; i <= SlotCount; i++)
            {
                string name = i == SpaceIndex ? prefix + ".space" : prefix + ".r" + i.ToString("D2", CultureInfo.InvariantCulture);
                SafeWaitHandle h = Kernel.CreateEvent(null, manualReset: false, initialState: false, name);
                int err = Kernel.LastError();                                  // 183 = opened an existing event (normal for openers)
                if (h.IsInvalid)
                {
                    h.Dispose();
                    throw Kernel.Fail("CreateEventW", err, name);
                }

                if (isCreator && err == (int)WIN32_ERROR.ERROR_ALREADY_EXISTS)
                {
                    // Cannot happen with a fresh random SectionId; if it does, make sure no stale set survives.
                    Kernel.ResetEvent(h.DangerousGetHandle());
                }

                if (!isCreator && err != (int)WIN32_ERROR.ERROR_ALREADY_EXISTS)
                {
                    createdAny = true;
                }

                bool added = false;
                h.DangerousAddRef(ref added);
                _handles[i] = h;
                _raw[i] = h.DangerousGetHandle();
            }

            // An opener must find the creator's objects. If it had to create them while the writer is still active, the two processes do not
            // share a namespace (e.g. a section handle duplicated into another logon session while the events live in Local\): wakes would
            // never arrive. Fail loudly instead of hanging later. (After the writer closed, its events may legitimately be gone: draining is fine.)
            if (createdAny && Volatile.Read(ref hdr->WriterState) != 2 && ProcessLiveness.IsAlive(Volatile.Read(ref hdr->WriterPid), Volatile.Read(ref hdr->WriterStartTime)))
            {
                throw new RingBufferInitializationException(
                    "The buffer's signaling events are not visible from this process (different session namespace). " +
                    "Share the buffer under a Global\\ name, or open it from the creator's session.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public override uint Id => SignalBackendId.NamedEvent;

    /// <inheritdoc/>
    public override void OnSlotClaimed(int slot, ReaderSlot* s)
    {
        s->WaiterThreadId = 0;
        s->BackendWord = 0;
        Kernel.ResetEvent(_raw[slot]);                                          // a stale set from a previous owner cannot cause a wrong-condition wake
    }

    /// <inheritdoc/>
    public override void OnSlotReleased(int slot, ReaderSlot* s)
    {
    }

    /// <inheritdoc/>
    public override WaitOutcome WaitForData(int slot, nint writerProcess, uint timeoutMs)
    {
        nint* h = stackalloc nint[2];
        h[0] = _raw[slot];
        uint n = 1;
        if (writerProcess != 0)
        {
            h[1] = writerProcess;
            n = 2;
        }

        uint rc = Kernel.WaitForMultipleObjects(n, h, false, timeoutMs);
        return rc switch
        {
            (uint)WAIT_EVENT.WAIT_OBJECT_0 => WaitOutcome.Signaled,
            (uint)WAIT_EVENT.WAIT_OBJECT_0 + 1 => WaitOutcome.ProcessExited,
            (uint)WAIT_EVENT.WAIT_TIMEOUT => WaitOutcome.Timeout,
            _ => WaitOutcome.Failed,
        };
    }

    /// <inheritdoc/>
    public override void OnWriterBlocking(ControlBlock* hdr)
    {
    }

    /// <inheritdoc/>
    public override WaitOutcome WaitForSpace(ReadOnlySpan<nint> processes, uint timeoutMs, out int exitedIndex)
    {
        exitedIndex = -1;
        if (processes.Length > SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(processes));
        }

        nint* h = stackalloc nint[SlotCount + 1];
        h[0] = _raw[SpaceIndex];
        for (int i = 0; i < processes.Length; i++)
        {
            h[1 + i] = processes[i];
        }

        uint rc = Kernel.WaitForMultipleObjects((uint)(1 + processes.Length), h, false, timeoutMs);
        if (rc == (uint)WAIT_EVENT.WAIT_OBJECT_0)
        {
            return WaitOutcome.Signaled;
        }

        if (rc > (uint)WAIT_EVENT.WAIT_OBJECT_0 && rc < (uint)WAIT_EVENT.WAIT_OBJECT_0 + 1 + (uint)processes.Length)
        {
            exitedIndex = (int)(rc - (uint)WAIT_EVENT.WAIT_OBJECT_0 - 1);
            return WaitOutcome.ProcessExited;
        }

        return rc == (uint)WAIT_EVENT.WAIT_TIMEOUT ? WaitOutcome.Timeout : WaitOutcome.Failed;
    }

    /// <inheritdoc/>
    public override void WakeReader(int slot) => Kernel.SetEvent(_raw[slot]);

    /// <inheritdoc/>
    public override void WakeWriter() => Kernel.SetEvent(_raw[SpaceIndex]);

    /// <inheritdoc/>
    public override void WakeAllReaders()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            Kernel.SetEvent(_raw[i]);
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int i = 0; i <= SlotCount; i++)
        {
            SafeWaitHandle? h = _handles[i];
            if (h is null)
            {
                continue;
            }

            if (_raw[i] != 0)
            {
                h.DangerousRelease();
                _raw[i] = 0;
            }

            h.Dispose();
            _handles[i] = null!;
        }
    }
}
