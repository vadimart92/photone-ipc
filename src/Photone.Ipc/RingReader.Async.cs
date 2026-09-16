using System.Globalization;
using System.Threading.Tasks.Sources;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

public sealed unsafe partial class RingReader<T> : IValueTaskSource<bool>
{
    /// <summary>An idle waiter thread retires after this long without a request (a new one is started by the next suspending <see cref="Wait(int, CancellationToken)"/>).</summary>
    private const int WaiterIdleExitMs = 5_000;

    private ManualResetValueTaskSourceCore<bool> _vts;
    private Thread? _waiter;
    private readonly AutoResetEvent _arm = new(false);          // process-local "a request is pending"
    private readonly object _gate = new();                      // guards _waiter / _armPending against the idle-retire race
    private bool _armPending;
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
    /// NOTE: a <see cref="Chunk{T}"/> (ref struct) must not share a block with an <c>await</c>; put <see cref="TryRead"/> + use in a nested block.
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

        if (SpinUntil(target, _options.AsyncSpinTime))
        {
            return new ValueTask<bool>(true);                       // a few µs on the caller's thread
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        BeginWait();
        _vts.Reset();
        _asyncTarget = target;
        _asyncDeadline = SpinClock.ToDeadline(timeout);
        _cancelRequested = 0;
        _ct = cancellationToken;
        _ctr = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static (s, _) => ((RingReader<T>)s!).OnCancel(), this)
            : default;
        Arm();
        return new ValueTask<bool>(this, _vts.Version);
    }

    private void OnCancel()
    {
        Volatile.Write(ref _cancelRequested, 1);
        _backend.WakeReader(_slot);                                 // wake our own waiter thread; a stale set is harmless
    }

    /// <summary>Hands the pending request to the waiter thread, starting one if none is parked (the previous one may have retired while idle).</summary>
    private void Arm()
    {
        lock (_gate)
        {
            _armPending = true;
            if (_waiter is null)
            {
                var t = new Thread(WaiterLoop, 256 * 1024)
                {
                    IsBackground = true,
                    Name = "photone-wait-r" + _slot.ToString(CultureInfo.InvariantCulture),
                };
                _waiter = t;
                t.Start();
            }
        }

        _arm.Set();                                                 // the one syscall on the arm path
    }

    private void StopWaiterThread()
    {
        Thread? waiter;
        lock (_gate)
        {
            waiter = _waiter;
        }

        if (waiter is not null)
        {
            Volatile.Write(ref _exit, 1);
            _arm.Set();
            waiter.Join();
        }
    }

    private void WaiterLoop()
    {
        while (true)
        {
            bool armed;
            try
            {
                armed = _arm.WaitOne(WaiterIdleExitMs);
            }
            catch (ObjectDisposedException)
            {
                return;                                             // Dispose raced with our retirement: nothing is pending
            }

            if (Volatile.Read(ref _exit) != 0)
            {
                return;
            }

            if (!armed)
            {
                lock (_gate)
                {
                    if (!_armPending)
                    {
                        _waiter = null;                             // idle: retire; also lets an abandoned reader be collected (its finalizer frees the slot)
                        return;
                    }
                }

                continue;                                           // a request arrived while we were deciding
            }

            lock (_gate)
            {
                _armPending = false;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            bool result = false;
            Exception? ex = null;
            try
            {
                result = BlockUntil(_asyncTarget, _asyncDeadline, honorCancel: true);
            }
            catch (Exception e)
            {
                ex = e;                                             // OperationCanceled / ReaderEvicted / ObjectDisposed / PhotoneIpc
            }

            _ctr.Dispose();
            _ctr = default;
            Volatile.Write(ref _waitOutstanding, 0);                // before completion: the continuation may call Wait again
            if (ex is not null)
            {
                _vts.SetException(ex);
            }
            else
            {
                _vts.SetResult(result);                             // continuation queued to the thread pool
            }

            _counters.WaiterAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _vts.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _vts.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _vts.OnCompleted(continuation, state, token, flags);
}
