using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>Values of <c>ReaderSlot.EvictReason</c>.</summary>
internal static class EvictReason
{
    public const int None = 0;
    public const int Dead = 1;
    public const int StuckClaim = 2;
    public const int Lag = 3;   // reserved
}

public sealed unsafe partial class RingBuffer<T>
{
    // ------------------------------------------------------------------ CreateReader (DESIGN §5.6)

    /// <summary>
    /// Creates an independent reader starting at the current head (it sees only later commits). Usable from any process and role.
    /// A reader is a single-consumer object. After a sweep of dead/stuck slots, <see cref="TooManyReadersException"/> if all 32 slots are active.
    /// </summary>
    public RingReader<T> CreateReader(ReaderOptions? options = null)
    {
        // The reader will own this local reference. Taking it before the claim means that a concurrent Dispose can no longer release the
        // mapping under the claim (or return it to a pool, and from there to another buffer: DESIGN §15.2).
        if (!TryAddLocalRef())
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        bool handedOver = false;
        try
        {
            ThrowIfDisposed();
            return ClaimReader(options ?? ReaderOptions.Default, ref handedOver);
        }
        finally
        {
            if (!handedOver)
            {
                ReleaseLocalRef();
            }
        }
    }

    /// <summary>The slot claim of <see cref="CreateReader"/>; <paramref name="handedOver"/> is set once a reader owns the caller's local reference.</summary>
    private RingReader<T> ClaimReader(ReaderOptions options, ref bool handedOver)
    {
        int pid = Environment.ProcessId;
        long st = ProcessLiveness.OwnStartTime;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int i = 0; i < Layout.MaxReaders; i++)
            {
                ref ReaderSlot s = ref Slot(i);
                long w = Volatile.Read(ref s.Word);
                if (SlotWord.State(w) != SlotState.Free)
                {
                    continue;
                }

                uint seq = (SlotWord.Seq(w) + 1) & 0x3FFF_FFFF;
                long claimed = SlotWord.Make(SlotState.Claimed, seq, pid);
                if (Interlocked.CompareExchange(ref s.Word, claimed, w) != w)
                {
                    continue;                                                               // lost the race; next slot
                }

                s.ProcessStartTime = st;
                s.ClaimTick = Kernel.GetTickCount64();
                s.WaitFor = long.MaxValue;
                s.Flags = _atCreatorAddress ? 1 : 0;
                s.EvictReason = EvictReason.None;
                s.WaiterThreadId = 0;
                s.BackendWord = 0;
                _backend.OnSlotClaimed(i, (ReaderSlot*)Unsafe.AsPointer(ref s));              // NamedEvent: ResetEvent — discards a stale set from a previous owner
                TestHooks.AfterClaim?.Invoke();
                s.ReadCursor = Volatile.Read(ref Hdr.WriteCursor);                          // (a) provisional start = head
                long active = SlotWord.Make(SlotState.Active, seq, pid);
                if (Interlocked.CompareExchange(ref s.Word, active, claimed) != claimed)     // (b) publish [full fence]; fails only if a sweeper reclaimed a stuck claim
                {
                    throw new ReaderEvictedException(i, "The slot claim was reclaimed by a sweeper before the reader became active.");
                }

                long w2 = Volatile.Read(ref Hdr.WriteCursor);                               // (c) re-read AFTER the fence
                Volatile.Write(ref s.ReadCursor, w2);                                       // (d) adopt the newest head (monotone bump)
                Interlocked.Or(ref Hdr.ActiveMask, 1UL << i);
                Interlocked.Increment(ref Hdr.ReaderGeneration);
                RingReader<T>? reader = null;
                try
                {
                    long start = w2;
                    TagReader? tags = null;
                    if (_tagWriter is not null)
                    {
                        // (e) the tag snapshot, taken with the cursor published: the last persistent tag per key, and where this reader's tags start;
                        // the start cursor is w2, or the snapshot's newer write cursor (a monotone bump). The writer's own readers take the tag
                        // objects (DESIGN §16.5), readers of an opened buffer the records in shared memory (§16.6).
                        tags = LocalTagReader.Join(_tagWriter.Local, w2, out start);
                    }
                    else if (_tagViews is not null)
                    {
                        tags = SharedTagReader.Join(_hdr, _tagViews, _options.TagSerializer, w2, out start);
                    }

                    // (f) publish the start cursor with a full fence, then look for a writer waiting on this reader. Its scan may have seen the
                    // provisional cursor of (a), which a commit since then can leave below its target, while (d) and the bump of (e) store more
                    // without waking it; a slow tag join may also have held it back.
                    // Fenced store then flag load pairs with the writer's flag store then cursor scan (Dekker), as in Advance.
                    Interlocked.Exchange(ref s.ReadCursor, start);
                    if (Volatile.Read(ref Hdr.WriterWaiting) != 0 && start >= Volatile.Read(ref Hdr.WriterWaitFor)
                        && Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1)
                    {
                        _backend.WakeWriter();
                    }

                    reader = new RingReader<T>(this, i, active, start, options, tags);
                    handedOver = true;                                                      // from here on the reader releases the reference (Dispose / finalizer)
                    reader.ResolveWriterProcess();
                    return reader;
                }
                catch
                {
                    if (reader is null)
                    {
                        ReleaseFailedClaim(i, active);                                     // CreateReader gives the local ref back
                    }
                    else
                    {
                        reader.Dispose();
                    }

                    throw;
                }
            }

            if (attempt == 0)
            {
                SweepDeadSlots();
            }
        }

        throw new TooManyReadersException($"All {Layout.MaxReaders} reader slots are active.");
    }

    /// <summary>
    /// A claim that failed after its slot became Active (a malformed tag snapshot, the reader's constructor): gives the slot back the way a disposed reader
    /// does. The identity fields are zeroed before the slot is freed, so a sweeper never reads them behind the next claim, and a writer blocked on the slot
    /// is woken to scan again without it.
    /// </summary>
    private void ReleaseFailedClaim(int i, long active)
    {
        ref ReaderSlot s = ref Slot(i);
        Interlocked.And(ref Hdr.WaitersMask, ~(1UL << i));
        s.WaitFor = long.MaxValue;
        _backend.OnSlotReleased(i, (ReaderSlot*)Unsafe.AsPointer(ref s));
        s.ProcessStartTime = 0;
        s.ClaimTick = 0;
        if (Interlocked.CompareExchange(ref s.Word, SlotWord.Make(SlotState.Free, SlotWord.Seq(active), 0), active) == active)
        {
            Interlocked.And(ref Hdr.ActiveMask, ~(1UL << i));
            Interlocked.Increment(ref Hdr.ReaderGeneration);
        }

        if (Volatile.Read(ref Hdr.WriterWaiting) != 0 && Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1)
        {
            _backend.WakeWriter();
        }
    }

    // ------------------------------------------------------------------ eviction (DESIGN §5.7)

    /// <summary>
    /// Evicts slots whose owner process is provably dead (Active, or Claimed = crashed between the two claim CASes). Any process may run it.
    /// A claim held by a live process is never reclaimed, however old: the claimant's remaining claim-time stores (ResetEvent, the
    /// provisional cursor) would otherwise land in a slot that already belongs to somebody else.
    /// </summary>
    internal int SweepDeadSlots()
    {
        int evicted = 0;
        for (int i = 0; i < Layout.MaxReaders; i++)
        {
            ref ReaderSlot s = ref Slot(i);
            long w = Volatile.Read(ref s.Word);
            switch (SlotWord.State(w))
            {
                case SlotState.Claimed:
                {
                    // Identity fields are zeroed before a slot is freed, so a value seen here belongs to this claimant or is still 0.
                    long st = Volatile.Read(ref s.ProcessStartTime);
                    bool dead = st == 0 ? !ProcessLiveness.ProcessExists(SlotWord.Pid(w)) : !ProcessLiveness.IsAlive(SlotWord.Pid(w), st);
                    if (dead && Evict(i, w, EvictReason.StuckClaim))
                    {
                        evicted++;
                    }

                    break;
                }

                case SlotState.Active:
                    if (!ProcessLiveness.IsAlive(SlotWord.Pid(w), Volatile.Read(ref s.ProcessStartTime)) && Evict(i, w, EvictReason.Dead))
                    {
                        evicted++;
                    }

                    break;

                default:
                    break;                                                                  // Free, or a Dead tombstone (v1 never leaves one)
            }
        }

        return evicted;
    }

    /// <summary>
    /// ABA-proof eviction: CAS the full expected word to Dead, clear the masks, then Free the slot (v1 evicts only dead owners).
    /// Returns <see langword="false"/> if the slot changed under us (released or re-claimed): the new owner is never touched.
    /// </summary>
    internal bool Evict(int i, long expectedWord, int reason)
    {
        ref ReaderSlot s = ref Slot(i);
        long dead = SlotWord.Make(SlotState.Dead, SlotWord.Seq(expectedWord), SlotWord.Pid(expectedWord));
        if (Interlocked.CompareExchange(ref s.Word, dead, expectedWord) != expectedWord)
        {
            return false;
        }

        s.EvictReason = reason;
        Hdr.LastEvictedPid = SlotWord.Pid(expectedWord);
        ulong bit = 1UL << i;
        Interlocked.And(ref Hdr.WaitersMask, ~bit);
        Interlocked.And(ref Hdr.ActiveMask, ~bit);
        Interlocked.Increment(ref Hdr.EvictedReaders);
        Interlocked.Increment(ref Hdr.ReaderGeneration);
        _counters.Evictions++;
        s.ProcessStartTime = 0;                                     // never let a sweeper see the dead owner's identity behind the next claim
        s.ClaimTick = 0;
        s.WaitFor = long.MaxValue;
        Interlocked.Exchange(ref s.Word, SlotWord.Make(SlotState.Free, SlotWord.Seq(expectedWord), 0));
        // A cached laggard handle for this slot (writer only) is closed by the next RefreshLaggards / ReleaseNative, never here:
        // the writer thread may be waiting on it right now.
        return true;
    }
}
