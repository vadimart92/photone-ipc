using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

public sealed unsafe partial class RingReader<T> : IValueTaskSource<bool>
{
    /// <summary>An idle waiter thread retires after this long without a request (a new one is started by the next suspending <see cref="Wait(int, CancellationToken)"/>).</summary>
    private const int WaiterIdleExitMs = 5_000;

    private ManualResetValueTaskSourceCore<bool> _vts;
    private Thread? _waiter;
    private readonly AutoResetEvent _arm = new(false);          // process-local: wakes a parked waiter thread
    private readonly object _gate = new();                      // guards _waiter against the idle-retire race (slow path only)
    private int _request;                                       // 1 = a Wait is armed and not yet taken by the waiter thread
    private int _waiterParked;                                  // 1 = the waiter thread is blocked (or about to block) on _arm and must be signalled
    private long _asyncTarget;
    private long _asyncDeadline;
    private int _cancelRequested;
    private int _exit;
    private CancellationTokenRegistration _ctr;
    private CancellationToken _ct;

    /// <summary>
    /// Awaitable wait for at least <paramref name="count"/> readable elements. <see langword="true"/>: satisfied. <see langword="false"/>: the writer
    /// closed/terminated and fewer than <paramref name="count"/> elements will ever arrive (drain with <see cref="TryRead"/>/<see cref="Available"/>; see <see cref="Status"/>).
    /// <c>count == 0</c> ⇒ <see langword="true"/>. One outstanding <c>Wait</c>/<c>WaitSync</c> per reader; the returned task must be awaited exactly once.
    /// The wait itself runs on the reader's waiter thread, which spins for the adaptive budget before it blocks (see <see cref="ReaderOptions.MaxSpinTime"/>).
    /// NOTE: a <see cref="Chunk{T}"/> may cross an <c>await</c> - its <see cref="Chunk{T}.Data"/> is <see cref="ReadOnlyMemory{T}"/> - but it points into the
    /// ring: do not <see cref="Advance"/> past a chunk while an awaited operation still reads it.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled (via the returned task).</exception>
    /// <exception cref="ReaderEvictedException">The slot was taken away (via the returned task).</exception>
    /// <exception cref="ObjectDisposedException">The reader was disposed (via the returned task when disposed while waiting).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> exceeds the capacity.</exception>
    public ValueTask<bool> Wait(int count, CancellationToken cancellationToken = default) => Wait(count, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Awaitable wait with a timeout: <see langword="false"/> on timeout (<see cref="TimeSpan.Zero"/> polls once, without spinning), or when the
    /// writer closed/terminated and fewer than <paramref name="count"/> elements will ever arrive. See <see cref="Wait(int, CancellationToken)"/>.
    /// </summary>
    public ValueTask<bool> Wait(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if ((uint)count > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and Capacity.");
        }

        if (count == 0)
        {
            return new ValueTask<bool>(true);
        }

        long target = _r + count;
        if (_wc >= target || (_wc = Volatile.Read(ref Hdr.WriteCursor)) >= target)
        {
            return new ValueTask<bool>(true);                       // hot path: no state, no thread
        }

        if (timeout == TimeSpan.Zero)
        {
            return new ValueTask<bool>(false);                      // poll semantics, identical to WaitSync(count, TimeSpan.Zero)
        }

        if (Thread.CurrentThread == _waiter)
        {
            return WaitOnWaiterThread(target, timeout, cancellationToken);
        }

        long callerSpin = Math.Min(_asyncSpinTicks, _spin.Window);  // never more than the adaptive budget: no spinning on the caller when gaps are long
        if (callerSpin > 0 && SpinUntil(target, callerSpin, honorCancel: false))
        {
            return new ValueTask<bool>(true);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        BeginWait();
        _vts.Reset();
        short token = _vts.Version;
        _asyncTarget = target;
        _asyncDeadline = SpinClock.ToDeadline(timeout);
        Volatile.Write(ref _cancelRequested, 0);
        _ct = cancellationToken;
        _ctr = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static (s, _) => ((RingReader<T>)s!).OnCancel(), this)
            : default;
        try
        {
            Arm();
        }
        catch
        {
            // the waiter thread could not be started (resource exhaustion): undo the arm so the reader stays usable
            Interlocked.Exchange(ref _request, 0);
            _ctr.Dispose();
            _ctr = default;
            Volatile.Write(ref _waitOutstanding, 0);
            throw;
        }

        return new ValueTask<bool>(this, token);
    }

    /// <summary>
    /// <c>Wait</c> called on the reader's own waiter thread, i.e. from a synchronous continuation of the previous wait. Handing the request to the
    /// waiter would only queue it behind the code that is running right now, so the wait runs here, synchronously, and returns a completed task:
    /// an <c>await</c> loop over this reader then never suspends while it keeps up, and sync-over-async on this reader cannot deadlock.
    /// </summary>
    private ValueTask<bool> WaitOnWaiterThread(long target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        BeginWait();
        CancellationTokenRegistration registration = default;
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                Volatile.Write(ref _cancelRequested, 0);
                _ct = cancellationToken;
                registration = cancellationToken.UnsafeRegister(static (s, _) => ((RingReader<T>)s!).OnCancel(), this);
            }

            return new ValueTask<bool>(WaitCore(target, SpinClock.ToDeadline(timeout), honorCancel: cancellationToken.CanBeCanceled));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<bool>(ex);
        }
        finally
        {
            registration.Dispose();
            Volatile.Write(ref _waitOutstanding, 0);
        }
    }

    private void OnCancel()
    {
        Volatile.Write(ref _cancelRequested, 1);
        _backend.WakeReader(_slot);                                 // wake our own waiter thread if it is blocked; a stale set is harmless
    }

    /// <summary>
    /// Hands the pending request to the waiter thread. Dekker pair with the waiter's park: we store <c>_request</c> (full fence) and then load
    /// <c>_waiterParked</c>; the waiter stores <c>_waiterParked</c> (full fence) and then loads <c>_request</c>. So either the waiter sees the request and does
    /// not park, or we see it parked and signal it. While the waiter is spinning (or is this very thread, running a synchronous continuation) arming
    /// costs no system call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Arm()
    {
        Interlocked.Exchange(ref _request, 1);
        if (Volatile.Read(ref _waiterParked) != 0 || _waiter is null)
        {
            ArmSlow();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArmSlow()
    {
        lock (_gate)
        {
            if (_waiter is null)
            {
                Volatile.Write(ref _waiterParked, 0);
                var t = new Thread(WaiterLoop, 256 * 1024)
                {
                    IsBackground = true,
                    Name = "photone-wait-r" + _slot.ToString(CultureInfo.InvariantCulture),
                };
                _waiter = t;                                        // before Start: a synchronous continuation on the new thread must recognise it
                try
                {
                    t.Start();
                }
                catch
                {
                    _waiter = null;
                    Volatile.Write(ref _waiterParked, 1);           // the next Arm takes the slow path again
                    throw;
                }

                return;
            }
        }

        _counters.ArmSignals++;
        _arm.Set();
    }

    private void StopWaiterThread()
    {
        Thread? waiter;
        lock (_gate)
        {
            waiter = _waiter;
        }

        if (waiter is null)
        {
            return;
        }

        Volatile.Write(ref _exit, 1);
        if (waiter == Thread.CurrentThread)
        {
            return;                                                 // Dispose from a synchronous continuation: the loop exits when control returns to it
        }

        _arm.Set();
        waiter.Join();
    }

    /// <summary>Dispose on the waiter thread itself: an armed request that the loop has not taken yet is completed here with <see cref="ObjectDisposedException"/>.</summary>
    private void CompletePendingRequestOnDispose()
    {
        if (Volatile.Read(ref _waitOutstanding) == 0 || Interlocked.Exchange(ref _request, 0) == 0)
        {
            return;
        }

        _ctr.Dispose();
        _ctr = default;
        Volatile.Write(ref _waitOutstanding, 0);
        _vts.SetException(new ObjectDisposedException(GetType().FullName));
    }

    private void WaiterLoop()
    {
        while (true)
        {
            if (Volatile.Read(ref _exit) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _request, 0) != 0)
            {
                Serve();
                continue;
            }

            long idleStart = Stopwatch.GetTimestamp();
            if (SpinForRequest(idleStart))
            {
                if (_idleSpin.IsAdaptive)
                {
                    _idleSpin.OnSatisfied(Stopwatch.GetTimestamp() - idleStart);
                }

                continue;
            }

            Interlocked.Exchange(ref _waiterParked, 1);             // full fence: Dekker pair with Arm
            if (Volatile.Read(ref _request) == 0 && Volatile.Read(ref _exit) == 0)
            {
                bool signaled;
                try
                {
                    signaled = _arm.WaitOne(WaiterIdleExitMs);
                }
                catch (ObjectDisposedException)
                {
                    return;                                         // Dispose raced with an idle park: nothing is pending
                }

                if (!signaled)
                {
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _request) == 0)
                        {
                            _waiter = null;                         // retire (lets an abandoned reader be collected); _waiterParked stays 1, so the next Arm starts a thread
                            return;
                        }
                    }
                }
            }

            Volatile.Write(ref _waiterParked, 0);
            if (Volatile.Read(ref _request) != 0)
            {
                _idleSpin.OnSatisfied(Stopwatch.GetTimestamp() - idleStart);
            }
        }
    }

    /// <summary>Spins for the adaptive idle budget waiting for the next request (a consumer that re-arms quickly never needs the arm system call).</summary>
    private bool SpinForRequest(long start)
    {
        long budget = _idleSpin.Window;
        if (budget <= 0)
        {
            return false;
        }

        long end = budget >= long.MaxValue - start ? long.MaxValue : start + budget;
        int i = 0;
        while (true)
        {
            if (Volatile.Read(ref _request) != 0 || Volatile.Read(ref _exit) != 0)
            {
                return true;
            }

            Thread.SpinWait(1);
            if ((++i & 15) == 0 && end != long.MaxValue && Stopwatch.GetTimestamp() >= end)
            {
                return false;
            }
        }
    }

    private void Serve()
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool result = false;
        Exception? ex = null;
        try
        {
            result = WaitCore(_asyncTarget, _asyncDeadline, honorCancel: true);
        }
        catch (Exception e)
        {
            ex = e;                                                 // OperationCanceled / ReaderEvicted / ObjectDisposed / PhotoneIpc
        }

        _ctr.Dispose();
        _ctr = default;
        _counters.WaiterAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        Volatile.Write(ref _waitOutstanding, 0);                    // before completion: the continuation may call Wait again
        if (ex is not null)
        {
            _vts.SetException(ex);
        }
        else
        {
            _vts.SetResult(result);                                 // thread pool, or (AllowSynchronousContinuations) right here on this thread
        }
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _vts.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _vts.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _vts.OnCompleted(continuation, state, token, flags);
}
