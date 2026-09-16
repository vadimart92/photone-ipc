using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Photone.Ipc.Internal;
using Photone.Ipc.Signaling;

namespace Photone.Ipc;

public sealed unsafe partial class RingBuffer<T>
{
    private static readonly TimeSpan s_rescanInterval = TimeSpan.FromMicroseconds(1);

    // writer-local state (DESIGN §5 notation): _w == W, _e = reserve end (authoritative), _min = cached minimum reader cursor
    private long _w;
    private long _e;
    private long _min;
    private bool _outstanding;
    private Counters _counters;

    // laggard bookkeeping (writer only; sized 32 for the writer, empty otherwise)
    private readonly LaggardEntry[] _laggards;
    private readonly nint[] _laggardHandles;
    private readonly int[] _laggardSlot;
    private readonly long[] _laggardWord;
    private readonly int[] _pollLaggards;
    private int _laggardCount;
    private int _pollLaggardCount;

    /// <summary>Cached validated process handle of a reader that the writer has waited on (DESIGN §5.7).</summary>
    private struct LaggardEntry
    {
        public long Word;
        public SafeProcessHandle? Handle;
        public nint Raw;
        public bool PollMode;
    }

    // ------------------------------------------------------------------ GetBucket / TryGetBucket

    /// <summary>
    /// Reserves exactly <paramref name="count"/> elements at the head of the ring, blocking (spin, then kernel wait) until the slowest
    /// reader has freed enough space. With zero readers it never blocks. Only one bucket may be outstanding.
    /// </summary>
    /// <param name="count">Elements to reserve; <c>1 &lt;= count &lt;= Capacity</c>.</param>
    /// <exception cref="InvalidOperationException">Not the writer, the writer is closed, or a bucket is outstanding.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is 0 or exceeds <see cref="Capacity"/>.</exception>
    /// <exception cref="ObjectDisposedException">The buffer was disposed.</exception>
    public Bucket<T> GetBucket(int count)
    {
        CheckWriter(count);
        if (_capacity - (_e - _min) < count)             // cached view insufficient (invariant: _min <= true min, so this only under-estimates)
        {
            SlowGetBucket(count);
        }

        return Reserve(count);
    }

    /// <summary>Non-blocking form of <see cref="GetBucket"/>: <see langword="false"/> when fewer than <paramref name="count"/> elements are free right now.</summary>
    public bool TryGetBucket(int count, out Bucket<T> bucket)
    {
        CheckWriter(count);
        if (_capacity - (_e - _min) < count)
        {
            _min = ScanMin();
            if (_capacity - (_e - _min) < count)
            {
                bucket = default;
                return false;
            }
        }

        bucket = Reserve(count);
        return true;
    }

    /// <summary>Free elements from the writer's point of view (rescans every reader cursor). Writer only.</summary>
    public long FreeSpace
    {
        get
        {
            ThrowIfDisposed();
            if (!_isWriter)
            {
                throw new InvalidOperationException("FreeSpace is available on the writer only.");
            }

            _min = ScanMin();
            return _capacity - (_e - _min);
        }
    }

    private void CheckWriter(int count)
    {
        ThrowIfDisposed();
        if (!_isWriter)
        {
            throw new InvalidOperationException("Only the creating process can write; this buffer was opened in the reader role.");
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The writer has been closed.");
        }

        if ((uint)(count - 1) >= (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 1 and Capacity.");
        }

        if (_outstanding)
        {
            throw new InvalidOperationException("A bucket is already outstanding; commit or dispose it first.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Bucket<T> Reserve(int count)
    {
        _outstanding = true;
        long start = _e;
        _e += count;
        return new Bucket<T>(this, new Span<T>(_data + Offset(start), count), start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint Offset(long cursor) => (nint)((cursor & _mask) * sizeof(T));

    /// <summary>
    /// Slow path of <see cref="GetBucket"/>. Holds a local reference for its whole duration so that <see cref="Dispose"/> from another
    /// thread (the supported way to abort a blocked writer) cannot unmap the control block under the scan/wait loops; the blocked
    /// call then ends with <see cref="ObjectDisposedException"/> instead of an access violation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SlowGetBucket(int count)
    {
        if (!TryAddLocalRef())
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        try
        {
            ThrowIfDisposed();                                      // after the ref: Dispose may have started, but nothing is unmapped while we hold it
            _min = ScanMin();
            if (_capacity - (_e - _min) < count)
            {
                WaitForSpace(count);
            }
        }
        finally
        {
            ReleaseLocalRef();
        }
    }

    // ------------------------------------------------------------------ Commit

    /// <summary>Publishes <paramref name="count"/> elements of the outstanding bucket (called by <see cref="Bucket{T}"/>).</summary>
    internal void EndWrite(int count)
    {
        if (!_outstanding)
        {
            // The bucket was already closed by RingBuffer.Dispose (CloseWriter drops it): Dispose() is a no-op, Commit(k > 0) is an error.
            // Checked before any header access: the mapping may already be released.
            if (count == 0)
            {
                return;
            }

            throw new InvalidOperationException("The bucket is no longer outstanding (the writer was disposed).");
        }

        _w += count;
        _e = _w;                                                    // shrink the reservation: next bucket starts at W + count (no holes)
        Interlocked.Exchange(ref Hdr.WriteCursor, _w);              // RELEASE of all bucket data stores + STORE-LOAD fence (Dekker) in one op
        _counters.Commits++;
        ulong m = Volatile.Read(ref Hdr.WaitersMask);               // stays Shared in the writer's L1 while nobody blocks
        if (m != 0)
        {
            SignalReaders(m, _w);
        }

        _outstanding = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SignalReaders(ulong m, long w)
    {
        while (m != 0)
        {
            int i = BitOperations.TrailingZeroCount(m);
            ulong bit = 1UL << i;
            m &= m - 1;
            if (Volatile.Read(ref Slot(i).WaitFor) <= w)           // only readers whose request is now satisfied
            {
                Interlocked.And(ref Hdr.WaitersMask, ~bit);         // consume the flag FIRST ...
                _backend.WakeReader(i);                             // ... then signal (safe order, DESIGN §5.10)
                _counters.Signals++;
            }
        }
    }

    // ------------------------------------------------------------------ ScanMin / WaitForSpace

    /// <summary>
    /// Minimum <c>ReadCursor</c> over every Active slot (32 acquire loads); with no readers everything is free, so the current write cursor
    /// is returned (a joiner adopts the head, never less). Slow path only.
    /// </summary>
    private long ScanMin()
    {
        long min = long.MaxValue;
        for (int i = 0; i < Layout.MaxReaders; i++)
        {
            long w = Volatile.Read(ref Slot(i).Word);               // acquire: if Active, the cursor stored before Active is visible
            if (SlotWord.State(w) != SlotState.Active)
            {
                continue;
            }

            long r = Volatile.Read(ref Slot(i).ReadCursor);
            if (r < min)
            {
                min = r;
            }
        }

        _counters.Scans++;
        long result = min == long.MaxValue ? _w : min;
        AssertMinInvariant(result);
        return result;
    }

    [Conditional("DEBUG")]
    private void AssertMinInvariant(long min)
    {
        for (int i = 0; i < Layout.MaxReaders; i++)
        {
            if (SlotWord.State(Volatile.Read(ref Slot(i).Word)) == SlotState.Active)
            {
                Debug.Assert(min <= Volatile.Read(ref Slot(i).ReadCursor), "I2: _min must not exceed any active reader cursor");
            }
        }
    }

    private void WaitForSpace(int count)
    {
        long target = _e + count - _capacity;                       // min reader cursor that frees `count` elements (fixed for this episode)

        // phase 1: spin, rescanning at most every ~1 µs
        if (_options.SpinTime != TimeSpan.Zero)
        {
            SpinClock clock = SpinClock.Start(_options.SpinTime);
            int i = 0;
            while (true)
            {
                Thread.SpinWait(1);
                if ((++i & 7) != 0)
                {
                    continue;
                }

                if (clock.Tick(s_rescanInterval))
                {
                    _min = ScanMin();
                    if (_capacity - (_e - _min) >= count)
                    {
                        return;
                    }

                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }
                }

                if (clock.Expired)
                {
                    break;
                }
            }
        }

        // phase 2: kernel (the caller holds a local ref: the mapping stays valid even if Dispose runs on another thread)
        _counters.KernelWaits++;
        Volatile.Write(ref Hdr.WriterWaitSinceTick, Kernel.GetTickCount64());
        while (true)
        {
            ThrowIfDisposed();                                      // Dispose (any thread) wakes us via WakeWriter and we leave here
            RefreshLaggards(target);                                // every Active blocker gets a validated process handle (or poll mode); dead ones evicted now
            if (_capacity - (_e - _min) >= count)                   // RefreshLaggards rescanned and may have evicted
            {
                return;
            }

            Volatile.Write(ref Hdr.WriterWaitFor, target);          // release store BEFORE the flag
            _backend.OnWriterBlocking(_hdr);
            Interlocked.Exchange(ref Hdr.WriterWaiting, 1);         // publish intent [full fence]
            _min = ScanMin();                                       // re-check AFTER the fence (Dekker)
            if (_capacity - (_e - _min) >= count)
            {
                Interlocked.Exchange(ref Hdr.WriterWaiting, 0);
                return;
            }

            WaitOutcome rc = _backend.WaitForSpace(_laggardHandles.AsSpan(0, _laggardCount), _livenessMs, out int exited);
            Interlocked.Exchange(ref Hdr.WriterWaiting, 0);         // we are awake; a reader that consumed the flag already signalled (stale set: harmless)
            switch (rc)
            {
                case WaitOutcome.Signaled:
                    _min = ScanMin();
                    continue;
                case WaitOutcome.ProcessExited:
                    Evict(_laggardSlot[exited], _laggardWord[exited], EvictReason.Dead);
                    _min = ScanMin();
                    continue;
                case WaitOutcome.Timeout:
                    SweepLaggards();
                    continue;
                default:
                    int err = Kernel.LastError();
                    ThrowIfDisposed();
                    throw Kernel.Fail("WaitForMultipleObjects", err, "writer waiting for space");
            }
        }
    }

    /// <summary>Rebuilds the set of blocking readers (<c>ReadCursor &lt; target</c>) with validated process handles; evicts provably dead ones.</summary>
    private void RefreshLaggards(long target)
    {
        _laggardCount = 0;
        _pollLaggardCount = 0;
        for (int i = 0; i < Layout.MaxReaders; i++)
        {
            long w = Volatile.Read(ref Slot(i).Word);
            ref LaggardEntry e = ref _laggards[i];
            if (SlotWord.State(w) != SlotState.Active)
            {
                if (e.Word != 0)
                {
                    CloseLaggard(ref e);                            // stale entry (slot released / evicted elsewhere)
                }

                continue;
            }

            if (Volatile.Read(ref Slot(i).ReadCursor) >= target)
            {
                continue;                                           // not a blocker
            }

            if (e.Word != w)
            {
                CloseLaggard(ref e);
                long st = Volatile.Read(ref Slot(i).ProcessStartTime);
                switch (ProcessLiveness.TryOpen(SlotWord.Pid(w), st, out SafeProcessHandle? h))
                {
                    case ProcessLiveness.OpenResult.Dead:
                        Evict(i, w, EvictReason.Dead);
                        continue;
                    case ProcessLiveness.OpenResult.AccessDenied:
                        e.Word = w;
                        e.PollMode = true;
                        break;
                    default:
                        bool added = false;
                        h!.DangerousAddRef(ref added);
                        e.Word = w;
                        e.Handle = h;
                        e.Raw = h.DangerousGetHandle();
                        e.PollMode = false;
                        break;
                }
            }

            if (!e.PollMode)
            {
                _laggardHandles[_laggardCount] = e.Raw;
                _laggardSlot[_laggardCount] = i;
                _laggardWord[_laggardCount] = w;
                _laggardCount++;
            }
            else
            {
                _pollLaggards[_pollLaggardCount++] = i;
            }
        }

        _min = ScanMin();
    }

    /// <summary>On every timeout slice while blocked: evict laggards whose process is signaled or (poll mode) provably dead.</summary>
    private void SweepLaggards()
    {
        for (int k = 0; k < _laggardCount; k++)
        {
            if (Kernel.WaitForSingleObject(_laggardHandles[k], 0) == Kernel.WAIT_OBJECT_0)
            {
                Evict(_laggardSlot[k], _laggardWord[k], EvictReason.Dead);
            }
        }

        for (int k = 0; k < _pollLaggardCount; k++)
        {
            int i = _pollLaggards[k];
            long w = Volatile.Read(ref Slot(i).Word);
            if (SlotWord.State(w) == SlotState.Active && !ProcessLiveness.IsAlive(SlotWord.Pid(w), Volatile.Read(ref Slot(i).ProcessStartTime)))
            {
                Evict(i, w, EvictReason.Dead);
            }
        }

        _min = ScanMin();
    }

    private static void CloseLaggard(ref LaggardEntry e)
    {
        if (e.Handle is not null)
        {
            if (e.Raw != 0)
            {
                e.Handle.DangerousRelease();
            }

            e.Handle.Dispose();
        }

        e = default;
    }

    // ------------------------------------------------------------------ close

    /// <summary>
    /// Drops the outstanding bucket, publishes <c>Closed</c>, wakes every reader (DESIGN §5.8) and a writer thread blocked in
    /// <see cref="GetBucket"/> (it then throws <see cref="ObjectDisposedException"/>). Idempotent.
    /// </summary>
    private void CloseWriter()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        if (_outstanding)
        {
            EndWrite(0);
        }

        Volatile.Write(ref Hdr.WriterState, WriterStateClosed);
        Interlocked.MemoryBarrier();
        _backend.WakeAllReaders();
        _backend.WakeWriter();                                      // a stale set is consumed harmlessly; a blocked GetBucket sees _disposed and exits
    }
}
