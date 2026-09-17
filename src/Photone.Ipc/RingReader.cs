using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Photone.Ipc.Internal;
using Photone.Ipc.Signaling;

namespace Photone.Ipc;

/// <summary>
/// An independent cursor over a <see cref="RingBuffer{T}"/> (one of up to 32). Single-consumer: one thread (or one logical flow) at a time.
/// Reads are zero-copy windows into the shared ring; <see cref="Advance"/> frees space for the writer.
/// Stays fully usable after the buffer that created it is disposed; dispose it to release its slot.
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
public sealed unsafe partial class RingReader<T> : IDisposable where T : unmanaged
{
    private const int WriterStateClosed = 2;

    private readonly RingBuffer<T> _owner;
    private readonly ControlBlock* _hdr;
    private readonly byte* _data;
    private readonly SignalBackend _backend;
    private readonly ReaderOptions _options;
    private readonly int _slot;
    private readonly long _word;
    private readonly ulong _bit;
    private readonly long _capacity;
    private readonly long _mask;
    private readonly uint _livenessMs;

    private long _r;                    // == own ReadCursor
    private long _wc;                   // cached W (only ever raised)
    private ReaderStatus _status;
    private bool _writerExited;
    private SafeProcessHandle? _writerProcHandle;
    private nint _writerProc;
    private int _disposed;
    private int _disposing;
    private int _waitOutstanding;
    private long _nextLivenessProbe;    // Stopwatch timestamp of the next writer-liveness probe made by Status (polling readers)
    private readonly long _livenessTicks;
    private readonly long _asyncSpinTicks;
    private SpinPolicy _spin;           // data waits (WaitSync on the caller, Wait on the waiter thread); one wait at a time, so no sharing
    private SpinPolicy _idleSpin;       // the waiter thread waiting for the next request
    private Counters _counters;

    internal RingReader(RingBuffer<T> owner, int slot, long word, long cursor, ReaderOptions options, TagReader? tags = null)
    {
        _tags = tags;
        _tagEnd = tags is null ? &owner.Header->TagEnd : tags.EndPointer;
        _tagLoadedW = tags is null ? long.MaxValue : cursor;
        _tagNextOffset = tags?.NextOffset ?? long.MaxValue;
        _tagPosition = tags?.Position ?? 0;
        _tagThreshold = Math.Min(_tagLoadedW, _tagNextOffset);
        _owner = owner;
        _hdr = owner.Header;
        _data = owner.Data;
        _backend = owner.Backend;
        _options = options;
        _slot = slot;
        _word = word;
        _bit = 1UL << slot;
        _capacity = owner.Capacity;
        _mask = owner.Mask;
        _livenessMs = owner.LivenessCheckIntervalMs;
        _livenessTicks = SpinClock.ToTicks(TimeSpan.FromMilliseconds(_livenessMs));
        _asyncSpinTicks = options.AsyncSpinTime < TimeSpan.Zero ? long.MaxValue : SpinClock.ToTicks(options.AsyncSpinTime);
        _spin = new SpinPolicy(options.SpinTime, options.MaxSpinTime);
        _idleSpin = new SpinPolicy(options.SpinTime, options.MaxSpinTime);
        _r = cursor;
        _wc = cursor;
        _vts.RunContinuationsAsynchronously = !options.AllowSynchronousContinuations;
    }

    // ------------------------------------------------------------------ properties

    /// <summary>Elements readable right now (reloads the write cursor).</summary>
    public long Available
    {
        get
        {
            ThrowIfDisposed();
            _wc = Volatile.Read(ref Hdr.WriteCursor);
            return _wc - _r;
        }
    }

    /// <summary>Absolute element index of the next unread element.</summary>
    public long ReadCursor => _r;

    /// <summary>Current status (cheap: cached state plus one volatile load of the slot word and the writer state).</summary>
    public ReaderStatus Status
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ReaderStatus.Disposed;
            }

            if (_status != ReaderStatus.Active)
            {
                return _status;
            }

            if (Volatile.Read(ref MySlot.Word) != _word)
            {
                _status = ReaderStatus.Evicted;
                return ReaderStatus.Evicted;
            }

            if (Volatile.Read(ref Hdr.WriterState) == WriterStateClosed)
            {
                _status = ReaderStatus.WriterClosed;
                return ReaderStatus.WriterClosed;
            }

            if (!_writerExited)
            {
                ProbeWriterLiveness();                              // a reader that never blocks still learns about a crashed writer (rate-limited)
            }

            if (_writerExited)
            {
                _status = ReaderStatus.WriterTerminated;
                return ReaderStatus.WriterTerminated;
            }

            return ReaderStatus.Active;
        }
    }

    /// <summary>At most once per <c>LivenessCheckInterval</c>: one <c>WaitForSingleObject(writer, 0)</c>, or a PID/start-time check in poll mode.</summary>
    private void ProbeWriterLiveness()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextLivenessProbe)
        {
            return;
        }

        _nextLivenessProbe = now + _livenessTicks;
        if (_writerProc != 0)
        {
            if (Kernel.WaitForSingleObject(_writerProc, 0) == Kernel.WAIT_OBJECT_0)
            {
                _writerExited = true;
            }
        }
        else
        {
            PollWriterLiveness();
        }
    }

    /// <summary><see langword="true"/> when the writer is gone (closed or terminated) and nothing is left to read.</summary>
    public bool IsCompleted => Status is ReaderStatus.WriterClosed or ReaderStatus.WriterTerminated && Available == 0;

    /// <summary>The slot index (0..31).</summary>
    public int Slot => _slot;

    /// <summary>Internal diagnostics counters.</summary>
    internal Counters Counters => _counters;

    /// <summary>Current adaptive spin budget of data waits, in Stopwatch ticks (tests and benchmarks).</summary>
    internal long SpinWindowTicks => _spin.Window;

    private ref ControlBlock Hdr => ref Unsafe.AsRef<ControlBlock>(_hdr);

    private ref ReaderSlot MySlot => ref ControlBlock.SlotRef(_hdr, _slot);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint Offset(long cursor) => (nint)((cursor & _mask) * sizeof(T));

    // ------------------------------------------------------------------ TryRead / Advance (DESIGN §5.4)

    /// <summary>
    /// Returns a window of exactly <paramref name="count"/> elements starting at <see cref="ReadCursor"/>, or <see langword="false"/> if fewer are
    /// published. <c>count == 0</c> ⇒ <see langword="true"/> with an empty chunk. Never blocks. The chunk stays valid until <see cref="Advance"/> moves past it.
    /// <see cref="Chunk{T}.Tags"/> holds the tags attached to the chunk's elements.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> exceeds the capacity.</exception>
    /// <exception cref="RingBufferLayoutException">A tag record in shared memory is malformed.</exception>
    public bool TryRead(int count, out Chunk<T> chunk)
    {
        ThrowIfDisposed();
        if ((uint)count > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and Capacity.");
        }

        if (_wc - _r < count)
        {
            _wc = Volatile.Read(ref Hdr.WriteCursor);               // ACQUIRE: later data loads see everything published <= _wc
            if (_wc - _r < count)
            {
                chunk = default;
                return false;
            }
        }

        TagReader? tags = null;
        long end = _r + count;
        if (_tagThreshold < end)                                    // never without tags
        {
            if (_tagNextOffset >= end && Volatile.Read(ref *_tagEnd) == _tagPosition)
            {
                _tagLoadedW = _wc;                                  // nothing published since the last load and nothing queued here: the common case of sparse tags
                _tagThreshold = Math.Min(_wc, _tagNextOffset);
            }
            else
            {
                tags = TagsForRead(end);                            // load what was published; the chunk's tags, if any
            }
        }

        chunk = new Chunk<T>(new ReadOnlySpan<T>(_data + Offset(_r), count), _r, tags);
        return true;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> elements (<c>0 &lt;= count &lt;=</c> what the last <see cref="TryRead"/>/<see cref="Available"/> observed),
    /// freeing them for the writer. May be less than the last chunk.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> exceeds the observed readable range.</exception>
    /// <exception cref="ReaderEvictedException">The slot was taken away.</exception>
    public void Advance(int count)
    {
        ThrowIfDisposed();
        if ((ulong)count > (ulong)(_wc - _r))                       // validated against the cached W; W is monotone so it holds for the real W too
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count exceeds the number of elements observed as readable.");
        }

        if (count == 0)
        {
            return;
        }

        if (Volatile.Read(ref MySlot.Word) != _word)                  // one L1 load of our own line, BEFORE the cursor store: a slot we no longer own is never written
        {
            ThrowEvicted();
        }

        if (_tagThreshold < _r + count)
        {
            TagsForAdvance(_r + count);                             // BEFORE the cursor store (DESIGN §16.4); never without tags
        }

        long old = _r;
        _r += count;
        Interlocked.Exchange(ref MySlot.ReadCursor, _r);              // RELEASE of all prior data loads + full fence (one xchg)
        if (Volatile.Read(ref Hdr.WriterWaiting) != 0)
        {
            SignalWriterIfCrossing(old);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SignalWriterIfCrossing(long old)
    {
        long t = Volatile.Read(ref Hdr.WriterWaitFor);              // stored by the writer before its Exchange(WriterWaiting, 1)
        if (old < t && _r >= t)                                     // only the Advance that can complete "min >= target" pays (DESIGN §5.10)
        {
            if (Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1)
            {
                _backend.WakeWriter();                              // exactly one reader pays the syscall per writer sleep
                _counters.Signals++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowEvicted()
    {
        _status = ReaderStatus.Evicted;
        throw new ReaderEvictedException(_slot, $"Reader slot {_slot} was evicted (the process was believed dead).");
    }

    // ------------------------------------------------------------------ WaitSync (DESIGN §5.5)

    /// <summary>Blocks until at least <paramref name="count"/> elements are readable. Equivalent to <c>WaitSync(count, Timeout.InfiniteTimeSpan)</c>.</summary>
    public bool WaitSync(int count) => WaitSync(count, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Blocks (spin, then kernel wait) until at least <paramref name="count"/> elements are readable. <see cref="TimeSpan.Zero"/> polls once;
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits forever. Returns <see langword="false"/> on timeout, or when the writer closed/terminated and
    /// fewer than <paramref name="count"/> elements will ever arrive (check <see cref="Status"/> and <see cref="Available"/>, drain with <see cref="TryRead"/>).
    /// <c>count == 0</c> ⇒ <see langword="true"/>. One outstanding wait per reader.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> exceeds the capacity.</exception>
    /// <exception cref="InvalidOperationException">Another wait is outstanding.</exception>
    /// <exception cref="ReaderEvictedException">The slot was taken away.</exception>
    /// <exception cref="ObjectDisposedException">The reader was disposed (also while waiting).</exception>
    public bool WaitSync(int count, TimeSpan timeout)
    {
        ThrowIfDisposed();
        if ((uint)count > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and Capacity.");
        }

        if (count == 0)
        {
            return true;
        }

        long target = _r + count;
        if (_wc >= target || (_wc = Volatile.Read(ref Hdr.WriteCursor)) >= target)
        {
            return true;                                            // fast path; the cached cursor is consulted first (it only ever rises)
        }

        if (timeout == TimeSpan.Zero)
        {
            return false;
        }

        BeginWait();
        try
        {
            return WaitCore(target, SpinClock.ToDeadline(timeout), honorCancel: false);
        }
        finally
        {
            Volatile.Write(ref _waitOutstanding, 0);
        }
    }

    /// <summary>
    /// Two-phase wait shared by <see cref="WaitSync(int, TimeSpan)"/> (caller thread) and the async waiter thread: spin for the adaptive budget
    /// (never past the deadline), then block; a wait that had to block feeds its total duration back into the spin policy.
    /// </summary>
    private bool WaitCore(long target, long deadline, bool honorCancel)
    {
        long start = Stopwatch.GetTimestamp();
        long budget = _spin.Window;
        if (deadline != long.MaxValue && deadline - start < budget)
        {
            budget = deadline - start;
        }

        if (budget > 0 && SpinUntil(target, budget, honorCancel))
        {
            if (_spin.IsAdaptive)
            {
                _spin.OnSatisfied(Stopwatch.GetTimestamp() - start);
            }

            return true;
        }

        bool satisfied = BlockUntil(target, deadline, honorCancel);
        if (satisfied)
        {
            _spin.OnSatisfied(Stopwatch.GetTimestamp() - start);
        }

        return satisfied;
    }

    /// <summary>
    /// Publishes "a wait is in progress" (one per reader) and re-checks for a concurrent <see cref="Dispose"/>: Dispose stores
    /// <c>_disposing</c> with a full fence and then loads <c>_waitOutstanding</c>; we store <c>_waitOutstanding</c> with a full fence and
    /// then load <c>_disposing</c>, so at least one side sees the other (Dekker) and a wait can never start on a reader being disposed.
    /// </summary>
    private void BeginWait()
    {
        if (Interlocked.Exchange(ref _waitOutstanding, 1) != 0)
        {
            throw new InvalidOperationException("Only one Wait/WaitSync may be outstanding per reader.");
        }

        if (Volatile.Read(ref _disposing) != 0)
        {
            Volatile.Write(ref _waitOutstanding, 0);
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    /// <summary>
    /// Polls W on every pause for <paramref name="budgetTicks"/> Stopwatch ticks (<see cref="long.MaxValue"/> = forever); reads the clock every 16 iterations.
    /// Returns <see langword="true"/> only when the target is reached; gives up early on dispose, on cancellation (when honoured) and on the cold checks.
    /// </summary>
    private bool SpinUntil(long target, long budgetTicks, bool honorCancel)
    {
        if (budgetTicks <= 0)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        long end = budgetTicks >= long.MaxValue - now ? long.MaxValue : now + budgetTicks;
        int i = 0;
        while (true)
        {
            long w = Volatile.Read(ref Hdr.WriteCursor);
            if (w >= target)
            {
                _wc = w;
                _counters.SpinSuccesses++;
                return true;
            }

            Thread.SpinWait(1);
            if ((++i & 15) != 0)
            {
                continue;
            }

            if ((end != long.MaxValue && Stopwatch.GetTimestamp() >= end)
                || Volatile.Read(ref _disposing) != 0
                || (honorCancel && Volatile.Read(ref _cancelRequested) != 0))
            {
                return false;
            }

            if ((i & 4095) == 0)
            {
                // unbounded spinners must still notice a closed / dead writer and an eviction (cold checks, ~every 100 µs)
                if (Volatile.Read(ref Hdr.WriterState) == WriterStateClosed || _writerExited || Volatile.Read(ref MySlot.Word) != _word)
                {
                    return false;
                }

                if ((i & 65535) == 0 && _writerProc != 0 && Kernel.WaitForSingleObject(_writerProc, 0) == Kernel.WAIT_OBJECT_0)
                {
                    _writerExited = true;
                    return false;
                }
            }
        }
    }

    /// <summary>Shared blocking loop of <see cref="WaitSync(int, TimeSpan)"/> (caller thread) and the async waiter thread.</summary>
    private bool BlockUntil(long target, long deadline, bool honorCancel)
    {
        while (true)
        {
            if (Volatile.Read(ref _disposing) != 0)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            if (Volatile.Read(ref MySlot.Word) != _word)
            {
                ThrowEvicted();
            }

            MySlot.WaitFor = target;                                  // plain store; ordered before the mask bit by the RMW below
            Interlocked.Or(ref Hdr.WaitersMask, _bit);              // publish "I will block" [full fence]
            long w = Volatile.Read(ref Hdr.WriteCursor);            // re-check AFTER the fence
            if (w >= target)
            {
                Withdraw();
                _wc = w;
                return true;
            }

            if (CheckWriterGone())
            {
                Withdraw();
                _wc = Volatile.Read(ref Hdr.WriteCursor);
                return _wc >= target;                               // drain what is published; false if fewer than count remain
            }

            if (honorCancel && Volatile.Read(ref _cancelRequested) != 0)
            {
                Withdraw();
                throw new OperationCanceledException(_ct);
            }

            uint ms = SpinClock.RemainingMs(deadline);
            if (_writerProc == 0 && ms > _livenessMs)
            {
                ms = _livenessMs;                                   // poll mode: liveness backstop
            }

            WaitOutcome rc = _backend.WaitForData(_slot, _writerProc, ms);
            Withdraw();                                             // the writer may already have consumed our bit: harmless
            _counters.KernelWaits++;
            switch (rc)
            {
                case WaitOutcome.Signaled:
                    if (Volatile.Read(ref Hdr.WriteCursor) < target)
                    {
                        _counters.SpuriousWakes++;
                    }

                    continue;
                case WaitOutcome.ProcessExited:
                    _writerExited = true;                           // CheckWriterGone decides Closed vs Terminated
                    continue;
                case WaitOutcome.Timeout:
                    if (_writerProc == 0)
                    {
                        PollWriterLiveness();
                    }

                    if (deadline != long.MaxValue && Stopwatch.GetTimestamp() >= deadline)
                    {
                        return false;
                    }

                    continue;
                default:
                    int err = Kernel.LastError();
                    if (Volatile.Read(ref _disposing) != 0)
                    {
                        throw new ObjectDisposedException(GetType().FullName);
                    }

                    throw Kernel.Fail("WaitForMultipleObjects", err, "reader waiting for data");
            }
        }
    }

    private void Withdraw()
    {
        Interlocked.And(ref Hdr.WaitersMask, ~_bit);
        MySlot.WaitFor = long.MaxValue;
    }

    private bool CheckWriterGone()
    {
        if (Volatile.Read(ref Hdr.WriterState) == WriterStateClosed)
        {
            _status = ReaderStatus.WriterClosed;
            return true;
        }

        if (_writerExited)
        {
            _status = ReaderStatus.WriterTerminated;
            return true;
        }

        return false;
    }

    private void PollWriterLiveness()
    {
        if (!ProcessLiveness.IsAlive(Volatile.Read(ref Hdr.WriterPid), Volatile.Read(ref Hdr.WriterStartTime)))
        {
            _writerExited = true;
        }
    }

    // ------------------------------------------------------------------ writer process (DESIGN §5.7)

    /// <summary>At join: open the writer's process handle (dead-at-join / poll mode handled).</summary>
    internal void ResolveWriterProcess()
    {
        if (Volatile.Read(ref Hdr.WriterState) == WriterStateClosed)
        {
            _status = ReaderStatus.WriterClosed;
            return;
        }

        int pid = Volatile.Read(ref Hdr.WriterPid);
        long st = Volatile.Read(ref Hdr.WriterStartTime);
        switch (ProcessLiveness.TryOpen(pid, st, out SafeProcessHandle? h))
        {
            case ProcessLiveness.OpenResult.Dead:
                _writerExited = true;                               // dead at join: Wait returns per the drained state immediately
                break;
            case ProcessLiveness.OpenResult.AccessDenied:
                _writerProc = 0;                                    // poll mode
                break;
            default:
                bool added = false;
                h!.DangerousAddRef(ref added);
                _writerProcHandle = h;
                _writerProc = h.DangerousGetHandle();
                break;
        }
    }

    // ------------------------------------------------------------------ Dispose (DESIGN §5.9)

    /// <summary>
    /// Releases the slot, wakes the writer if it was blocked on this reader, and completes a pending <see cref="Wait(int, CancellationToken)"/> /
    /// unblocks a <see cref="WaitSync(int, TimeSpan)"/> on another thread with <see cref="ObjectDisposedException"/>. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
        Interlocked.Exchange(ref _disposing, 1);                    // full fence: pairs with BeginWait (Dekker), see there
        Volatile.Write(ref _cancelRequested, 1);
        if (Thread.CurrentThread == _waiter)
        {
            // Called from a synchronous continuation on the waiter thread. If that continuation had already armed another wait,
            // the waiter loop (this very thread) cannot serve it until we return: complete it here, or the loop below never ends.
            CompletePendingRequestOnDispose();
        }

        while (Volatile.Read(ref _waitOutstanding) != 0)
        {
            _backend.WakeReader(_slot);                             // wakes OUR OWN blocked wait (sync on another thread, or the waiter thread)
            Thread.Sleep(1);                                        // every wait path clears _waitOutstanding in its finally; bounded by one wake
        }

        StopWaiterThread();
        ReleaseSlot();
        ReleaseWriterProcess();
        _ctr.Dispose();
        _ctr = default;
        _arm.Dispose();
        _status = ReaderStatus.Disposed;
        _owner.ReaderReleased();
    }

    /// <summary>
    /// Safety net for a reader that was never disposed: frees the slot (so the writer does not wait for a ghost forever) and drops the local
    /// reference that keeps the mapping alive. Runs only when nothing references the reader any more, i.e. no wait is in progress and the
    /// async waiter thread has retired. Always prefer <see cref="Dispose"/>.
    /// </summary>
    ~RingReader()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ReleaseSlot();
            ReleaseWriterProcess();
        }
        catch (Exception)
        {
            // never let a finalizer throw
        }
        finally
        {
            _owner.ReaderReleased(finalizing: true);
        }
    }

    /// <summary>Gives the slot back (only if we still own it), and wakes a writer that was blocked on us. Shared state is touched only behind the ownership check.</summary>
    private void ReleaseSlot()
    {
        ref ReaderSlot s = ref MySlot;
        if (Volatile.Read(ref s.Word) != _word)
        {
            return;                                                 // evicted meanwhile (our process was believed dead): the slot may belong to somebody else now
        }

        Interlocked.And(ref Hdr.WaitersMask, ~_bit);
        s.WaitFor = long.MaxValue;
        _backend.OnSlotReleased(_slot, (ReaderSlot*)Unsafe.AsPointer(ref s));
        s.ProcessStartTime = 0;                                     // a sweeper must never read a previous owner's identity behind a fresh claim
        s.ClaimTick = 0;
        if (Interlocked.CompareExchange(ref s.Word, SlotWord.Make(SlotState.Free, SlotWord.Seq(_word), 0), _word) == _word)   // fails harmlessly if evicted meanwhile
        {
            Interlocked.And(ref Hdr.ActiveMask, ~_bit);
            Interlocked.Increment(ref Hdr.ReaderGeneration);
        }

        if (Volatile.Read(ref Hdr.WriterWaiting) != 0 && Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1)
        {
            _backend.WakeWriter();                                  // unconditional (no crossing filter): the writer must re-scan without us
            _counters.Signals++;
        }
    }

    private void ReleaseWriterProcess()
    {
        if (_writerProcHandle is not null)
        {
            if (_writerProc != 0)
            {
                _writerProcHandle.DangerousRelease();
                _writerProc = 0;
            }

            _writerProcHandle.Dispose();
            _writerProcHandle = null;
        }
    }
}
