using System.Diagnostics;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>Options for <see cref="RingBufferPool"/>.</summary>
public sealed class RingBufferPoolOptions
{
    /// <summary>
    /// How long a mapping may stay unused in the pool before it is released (views unmapped, section and events closed). Default 30 s.
    /// <see cref="TimeSpan.Zero"/> releases every mapping as soon as it is returned (no pooling); <see cref="Timeout.InfiniteTimeSpan"/> keeps
    /// mappings until <see cref="RingBufferPool.Trim"/>, <see cref="RingBufferPool.Dispose"/> or <see cref="MaxIdleBytes"/> releases them.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound of <see cref="RingBufferPool.IdleBytes"/>: returning a mapping beyond it releases the mappings that have been idle longest.
    /// Default: no bound (the pool never holds more than the buffers that were alive within the last <see cref="IdleTimeout"/>).
    /// </summary>
    public long MaxIdleBytes { get; init; } = long.MaxValue;

    /// <summary>
    /// Zero the data region when a section is reused for a new buffer. Without it the new buffer's pages still hold what the previous buffer wrote:
    /// never visible through a <see cref="RingReader{T}"/> (a reader sees only what is committed after it joined), but readable by any process that opens
    /// the new buffer and looks at the raw mapping. Default false.
    /// <para>
    /// This does not isolate the users of successive buffers from each other: a process that mapped an earlier buffer can keep a view of the section
    /// without holding it open (a pool's parked opener mapping does exactly that) and would see the later buffers too. Do not pool buffers that serve
    /// parties who must not see each other's data.
    /// </para>
    /// </summary>
    public bool ClearOnReuse { get; init; }
}

/// <summary>
/// Keeps the shared-memory mappings of released ring buffers for reuse, so that a process that creates or opens many buffers does not create, map,
/// pre-fault, unmap and free a section every time; a mapping left unused for <see cref="IdleTimeout"/> is released. Opt in per buffer with
/// <see cref="RingBufferOptions.Pool"/> on <see cref="RingBuffer{T}.Create"/> and on <c>Open</c>. See <c>docs/DESIGN.md</c> §15.
/// <para>
/// <b>Creator.</b> The ring lives in a section owned by the pool (the buffer's name becomes a small alias object) and goes back to the pool when the
/// buffer is released: disposed, and every <see cref="RingReader{T}"/> it created disposed too. A later <c>Create</c> with the same data size
/// (<c>Capacity * sizeof(T)</c>; the element type may differ) and the same namespace reuses it, but only once no other process has the section open
/// any more: readers elsewhere keep draining the old buffer undisturbed, and until they let go the new buffer gets another section. Reuse keeps the
/// address range, the three views, the resident pages and the 33 signaling events.
/// </para>
/// <para>
/// <b>Opener.</b> A buffer opened with a pool keeps its views and events when released (without holding the section open, so the creator can reuse
/// it), and opening the next buffer that the creator placed in the same section adopts them instead of mapping and faulting the pages in again.
/// </para>
/// <para>
/// A buffer that is never disposed (released by its finalizer) does not return its mapping. Thread-safe. The expiry timer runs only while the pool
/// holds idle mappings.
/// </para>
/// </summary>
public sealed class RingBufferPool : IDisposable
{
    /// <summary>Idle sections of the requested size that one <c>Create</c> checks for other holders (one system call each) before it maps a new one.</summary>
    private const int MaxReuseCandidates = 16;

    private const long MaxTimerDueMs = 0xFFFF_FFFE;

    private static readonly TimerCallback s_onTimer = static state =>
    {
        if (((WeakReference<RingBufferPool>)state!).TryGetTarget(out RingBufferPool? pool))
        {
            pool.ReleaseExpired();
        }
    };

    private static RingBufferPool? s_shared;

    private readonly object _gate = new();
    private readonly List<PooledMapping> _idle = [];               // in IdleSince order: the longest idle first
    private readonly long _idleTimeoutTicks;                        // Stopwatch ticks; 0 = no pooling; long.MaxValue = never expires
    private readonly bool _isShared;
    private long _idleBytes;
    private Timer? _timer;
    private long _timerDue = long.MaxValue;
    private bool _disposed;

    private long _reused;
    private long _misses;
    private long _busy;
    private long _revived;
    private long _released;

    /// <summary>Creates a pool.</summary>
    /// <param name="options">Pool options (<see langword="null"/> = defaults).</param>
    /// <exception cref="ArgumentOutOfRangeException">A negative <see cref="RingBufferPoolOptions.IdleTimeout"/> other than infinite, or a negative <see cref="RingBufferPoolOptions.MaxIdleBytes"/>.</exception>
    public RingBufferPool(RingBufferPoolOptions? options = null)
        : this(options ?? new RingBufferPoolOptions(), isShared: false)
    {
    }

    private RingBufferPool(RingBufferPoolOptions options, bool isShared)
    {
        if (options.IdleTimeout < TimeSpan.Zero && options.IdleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.IdleTimeout, "IdleTimeout must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        if (options.MaxIdleBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxIdleBytes, "MaxIdleBytes must be non-negative.");
        }

        IdleTimeout = options.IdleTimeout;
        MaxIdleBytes = options.MaxIdleBytes;
        ClearOnReuse = options.ClearOnReuse;
        _idleTimeoutTicks = options.IdleTimeout == Timeout.InfiniteTimeSpan ? long.MaxValue
            : options.IdleTimeout == TimeSpan.Zero ? 0
            : Math.Max(1, SpinClock.ToTicks(options.IdleTimeout));
        _isShared = isShared;
    }

    /// <summary>Releases the idle mappings of a pool that was never disposed (the event handles are otherwise held until the process exits).</summary>
    ~RingBufferPool()
    {
        List<PooledMapping> release;
        lock (_gate)
        {
            _disposed = true;
            release = TakeAll_NoLock();
        }

        foreach (PooledMapping e in release)
        {
            try
            {
                e.Release();
            }
            catch (Exception)
            {
                // never let a finalizer throw
            }
        }
    }

    /// <summary>A process-wide pool with the default options. <see cref="Dispose"/> on it only trims.</summary>
    public static RingBufferPool Shared
    {
        get
        {
            RingBufferPool? pool = Volatile.Read(ref s_shared);
            if (pool is null)
            {
                var created = new RingBufferPool(new RingBufferPoolOptions(), isShared: true);
                pool = Interlocked.CompareExchange(ref s_shared, created, null) ?? created;
                if (!ReferenceEquals(pool, created))
                {
                    GC.SuppressFinalize(created);
                }
            }

            return pool;
        }
    }

    /// <summary>See <see cref="RingBufferPoolOptions.IdleTimeout"/>.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>See <see cref="RingBufferPoolOptions.MaxIdleBytes"/>.</summary>
    public long MaxIdleBytes { get; }

    /// <summary>See <see cref="RingBufferPoolOptions.ClearOnReuse"/>.</summary>
    public bool ClearOnReuse { get; }

    /// <summary>Mappings currently held idle (creator sections and parked opener mappings).</summary>
    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>Sum of the data-region and tag-area sizes of the mappings currently held idle.</summary>
    public long IdleBytes
    {
        get
        {
            lock (_gate)
            {
                return _idleBytes;
            }
        }
    }

    /// <summary><c>Create</c> calls served by an idle section.</summary>
    internal long ReusedCount => Interlocked.Read(ref _reused);

    /// <summary><c>Create</c> calls that had to map a new section.</summary>
    internal long MissCount => Interlocked.Read(ref _misses);

    /// <summary>Idle sections of the right size skipped because another process still had them open.</summary>
    internal long BusyCount => Interlocked.Read(ref _busy);

    /// <summary><c>Open</c> calls served by a parked mapping.</summary>
    internal long RevivedCount => Interlocked.Read(ref _revived);

    /// <summary>Idle mappings released (expired, over the bound, trimmed, disposed, or returned to a pool that keeps nothing).</summary>
    internal long ReleasedCount => Interlocked.Read(ref _released);

    /// <summary>Releases every idle mapping now. Mappings in use are not affected and return to the pool as usual.</summary>
    public void Trim()
    {
        List<PooledMapping> release;
        lock (_gate)
        {
            release = TakeAll_NoLock();
        }

        Release(release);
    }

    /// <summary>
    /// Releases every idle mapping and stops pooling: buffers still using mappings from this pool release them normally when they are done.
    /// On <see cref="Shared"/> this only trims.
    /// </summary>
    public void Dispose()
    {
        if (_isShared)
        {
            Trim();
            return;
        }

        List<PooledMapping> release;
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            release = TakeAll_NoLock();
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        Release(release);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ used by RingBuffer<T>

    /// <summary>
    /// Takes the most recently returned idle creator section with a header view of <paramref name="headerBytes"/> and a data region of <paramref name="dataBytes"/>
    /// that nobody else holds open, with its <c>InitState</c> left at 0 for the caller to re-initialise; <see langword="null"/> when there is none (the caller
    /// maps a new section).
    /// </summary>
    internal PooledMapping? RentForCreate(long headerBytes, long dataBytes, bool global, ulong? preferredBase)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                int candidates = 0;
                for (int i = _idle.Count - 1; i >= 0 && candidates < MaxReuseCandidates; i--)        // newest first: its pages are the warmest
                {
                    PooledMapping e = _idle[i];
                    if (!e.IsCreator || e.DataBytes != dataBytes || e.HeaderBytes != headerBytes || e.Global != global
                        || (preferredBase is ulong b && b != 0 && e.Mapping.BaseAddress != b))
                    {
                        continue;
                    }

                    candidates++;
                    if (TryClaim(e))
                    {
                        _idle.RemoveAt(i);
                        _idleBytes -= e.PooledBytes;
                        _reused++;
                        e.MarkInUse();
                        return e;
                    }

                    _busy++;
                }
            }

            _misses++;
            return null;
        }
    }

    /// <summary>Takes a parked opener mapping of section <paramref name="sectionId"/>, if this pool holds one.</summary>
    internal PooledMapping? TakeParked(ulong sectionId)
    {
        lock (_gate)
        {
            for (int i = _idle.Count - 1; i >= 0; i--)
            {
                PooledMapping e = _idle[i];
                if (!e.IsCreator && e.SectionId == sectionId)
                {
                    _idle.RemoveAt(i);
                    _idleBytes -= e.PooledBytes;
                    e.MarkInUse();
                    return e;
                }
            }

            return null;
        }
    }

    internal void CountRevived() => Interlocked.Increment(ref _revived);

    /// <summary>
    /// Takes a mapping back from a released buffer. An opener mapping gives up its section handle first: a parked mapping must not count as a user of
    /// the section, or its creator could never reuse it (see <see cref="TryClaim"/>).
    /// </summary>
    internal void Return(PooledMapping entry)
    {
        entry.MarkIdle();                                           // a second return of the same mapping would give it to two buffers: fail loudly instead
        if (!entry.IsCreator)
        {
            entry.Mapping.DetachSection();
        }

        List<PooledMapping>? release = null;
        lock (_gate)
        {
            if (_disposed || _idleTimeoutTicks == 0 || entry.PooledBytes > MaxIdleBytes)
            {
                release = [entry];
                _released++;
            }
            else
            {
                entry.IdleSince = Stopwatch.GetTimestamp();
                _idle.Add(entry);
                _idleBytes += entry.PooledBytes;
                while (_idleBytes > MaxIdleBytes)
                {
                    (release ??= []).Add(RemoveOldest_NoLock());
                }

                ArmTimer_NoLock();
            }
        }

        Release(release);
    }

    // ------------------------------------------------------------------ reuse check (DESIGN §15.3)

    /// <summary>
    /// Stores <c>InitState = 0</c> with a full fence, then asks the kernel how many handles the section has. Every process that maps a buffer holds the
    /// section open for as long as it does, and opens it (a system call) before it loads <c>InitState</c> and the instance id; so at least one side sees
    /// the other. Either the count includes the late opener and the closed buffer is left exactly as it was, or the opener waits for initialisation and
    /// then finds another instance id, which it reports as a buffer that no longer exists.
    /// </summary>
    private static unsafe bool TryClaim(PooledMapping e)
    {
        ControlBlock* hdr = (ControlBlock*)e.Mapping.Header;
        Interlocked.Exchange(ref hdr->InitState, 0);
        if (Kernel.TryQueryHandleCount(e.Mapping.Section, out uint handles) && handles == 1)
        {
            return true;
        }

        Volatile.Write(ref hdr->InitState, 1);                      // still held elsewhere (or the query failed): not reusable yet
        return false;
    }

    // ------------------------------------------------------------------ expiry

    private void ArmTimer_NoLock()
    {
        if (_disposed || _idle.Count == 0 || _idleTimeoutTicks == long.MaxValue)
        {
            return;
        }

        long since = _idle[0].IdleSince;
        long due = _idleTimeoutTicks > long.MaxValue - since ? long.MaxValue : since + _idleTimeoutTicks;
        if (due == long.MaxValue || due >= _timerDue)
        {
            return;                                                 // never expires, or an early enough wake-up is already armed
        }

        _timerDue = due;
        long remaining = due - Stopwatch.GetTimestamp();
        long ms = remaining <= 0 ? 0 : (long)Math.Min(Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency), MaxTimerDueMs);
        if (_timer is null)
        {
            // the callback holds the pool weakly (an abandoned pool is still collected) and captures no execution context
            bool restoreFlow = !ExecutionContext.IsFlowSuppressed();
            if (restoreFlow)
            {
                ExecutionContext.SuppressFlow();
            }

            try
            {
                _timer = new Timer(s_onTimer, new WeakReference<RingBufferPool>(this), Timeout.Infinite, Timeout.Infinite);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }

        _timer.Change(ms, Timeout.Infinite);
    }

    private void ReleaseExpired()
    {
        List<PooledMapping>? release = null;
        lock (_gate)
        {
            _timerDue = long.MaxValue;
            long now = Stopwatch.GetTimestamp();
            while (_idle.Count > 0 && now - _idle[0].IdleSince >= _idleTimeoutTicks)
            {
                (release ??= []).Add(RemoveOldest_NoLock());
            }

            ArmTimer_NoLock();
        }

        Release(release);
    }

    private PooledMapping RemoveOldest_NoLock()
    {
        PooledMapping e = _idle[0];
        _idle.RemoveAt(0);
        _idleBytes -= e.PooledBytes;
        _released++;
        return e;
    }

    private List<PooledMapping> TakeAll_NoLock()
    {
        List<PooledMapping> all = [.. _idle];
        _released += all.Count;
        _idle.Clear();
        _idleBytes = 0;
        return all;
    }

    /// <summary>Unmaps outside the lock: a large mapping takes milliseconds to tear down.</summary>
    private static void Release(List<PooledMapping>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (PooledMapping e in entries)
        {
            e.Release();
        }
    }
}
