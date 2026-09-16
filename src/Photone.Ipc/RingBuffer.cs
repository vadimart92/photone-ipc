using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Photone.Ipc.Internal;
using Photone.Ipc.Signaling;

namespace Photone.Ipc;

/// <summary>
/// A single-writer, multi-reader (broadcast) ring buffer of unmanaged elements living in shared memory, double-mapped so every
/// reservation and every readable window is one contiguous span even when it wraps around the end of the ring.
/// The creating process is the writer; any process that <see cref="Open(string, RingBufferOptions?)"/>s the buffer (or the creator itself)
/// can create independent readers with <see cref="CreateReader"/>. See <c>docs/DESIGN.md</c>.
/// </summary>
/// <typeparam name="T">Unmanaged element type; the element size and type name are validated on <c>Open</c>.</typeparam>
public sealed unsafe partial class RingBuffer<T> : IDisposable where T : unmanaged
{
    private const int WriterStateActive = 1;
    private const int WriterStateClosed = 2;

    private readonly MirroredSection _mapping;
    private readonly ControlBlock* _hdr;
    private readonly byte* _data;
    private readonly SignalBackend _backend;
    private readonly RingBufferOptions _options;
    private readonly string? _name;
    private readonly ulong _instanceId;
    private readonly long _capacity;
    private readonly long _mask;
    private readonly long _dataBytes;
    private readonly ulong _creatorBase;
    private readonly bool _isWriter;
    private readonly bool _atCreatorAddress;
    private readonly uint _livenessMs;

    private int _disposed;
    private int _closed;
    private int _localRefs = 1;             // the buffer itself; +1 per live RingReader created by it

    private RingBuffer(MirroredSection mapping, SignalBackend backend, RingBufferOptions options, string? name, bool isWriter, long capacity, ulong instanceId, ulong creatorBase)
    {
        _mapping = mapping;
        _hdr = (ControlBlock*)mapping.Header;
        _data = mapping.Data;
        _backend = backend;
        _options = options;
        _name = name;
        _isWriter = isWriter;
        _capacity = capacity;
        _mask = capacity - 1;
        _dataBytes = (long)mapping.DataBytes;
        _instanceId = instanceId;
        _creatorBase = creatorBase;
        _atCreatorAddress = mapping.BaseAddress == creatorBase;
        double ms = options.LivenessCheckInterval.TotalMilliseconds;
        _livenessMs = ms < 1 ? 1u : ms >= int.MaxValue ? (uint)int.MaxValue : (uint)ms;
        _laggards = isWriter ? new LaggardEntry[Layout.MaxReaders] : [];
        _laggardHandles = isWriter ? new nint[Layout.MaxReaders] : [];
        _laggardSlot = isWriter ? new int[Layout.MaxReaders] : [];
        _laggardWord = isWriter ? new long[Layout.MaxReaders] : [];
        _pollLaggards = isWriter ? new int[Layout.MaxReaders] : [];
    }

    // ------------------------------------------------------------------ create / open

    /// <summary>
    /// Creates a new buffer; this process is THE writer. <paramref name="name"/> <see langword="null"/> ⇒ <c>Local\photone.{Guid:N}</c>;
    /// a name without a namespace prefix becomes <c>Local\photone.{name}</c>; <c>Local\...</c> / <c>Global\...</c> are used verbatim.
    /// </summary>
    /// <param name="minCapacity">Minimum element capacity; rounded up to a power of two such that the data region is a multiple of 64 KiB.</param>
    /// <param name="name">Section name (see above).</param>
    /// <param name="options">Creator options.</param>
    /// <exception cref="RingBufferAlreadyExistsException">A section with that name already exists.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity would exceed 2^30 elements or 1 TiB.</exception>
    public static RingBuffer<T> Create(long minCapacity, string? name = null, RingBufferOptions? options = null)
    {
        options ??= RingBufferOptions.Default;
        Kernel.EnsurePlatform();
        Layout.EnsureInitialized();
        (long capacity, long dataBytes) = Internal.Capacity.Choose(minCapacity, sizeof(T));
        string sectionName = MirroredSection.NormalizeName(name);
        ulong instanceId = NewInstanceId();
        ulong total = Layout.HeaderViewBytes + 2 * (ulong)dataBytes;

        Span<ulong> candidates = stackalloc ulong[AddressHint.MaxProbes];
        int candidateCount = AddressHint.Candidates(instanceId, total, options.PreferredBaseAddress, candidates);

        SafeSectionHandle section = MirroredSection.CreateSection(dataBytes, sectionName);
        MirroredSection mapping = MirroredSection.Create(section, dataBytes, candidates[..candidateCount]);   // owns the section; self-tests the mirror
        SignalBackend? backend = null;
        try
        {
            ControlBlock* hdr = (ControlBlock*)mapping.Header;
            byte* data = mapping.Data;
            if (mapping.Mirror != data + dataBytes)
            {
                throw new RingBufferLayoutException("The mirror view is not adjacent to the data view.");
            }

            long start = ProcessLiveness.OwnStartTime;
            int pid = Environment.ProcessId;
            hdr->CreatorStartTime = start;                              // FIRST, before anything slow (pre-faulting a large buffer takes a while):
            Volatile.Write(ref hdr->CreatorPid, pid);                   // SECOND; openers use Pid != 0 && StartTime != 0 to detect a creator dying mid-init

            if (options.PreFault)
            {
                PreFault(data, dataBytes, write: true);                 // populate this process's PTEs for both views (the mirror is the same physical pages)
            }

            hdr->Magic = Layout.Magic;
            hdr->Version = Layout.Version;
            hdr->ControlBytes = Layout.ControlBytes;
            hdr->ElementSize = (uint)sizeof(T);
            hdr->MaxReaders = Layout.MaxReaders;
            hdr->Capacity = capacity;
            hdr->DataBytes = dataBytes;
            hdr->DataOffset = Layout.DataOffset;
            hdr->TypeHash = Internal.Capacity.TypeHash(typeof(T));
            hdr->SignalBackendId = SignalBackendId.NamedEvent;
            hdr->LayoutFlags = 0;
            hdr->CreatorBase = mapping.BaseAddress;
            hdr->InstanceId = instanceId;
            hdr->ReservationBytes = total;
            hdr->WriteCursor = 0;
            hdr->ReserveEnd = 0;
            hdr->WriterState = WriterStateActive;
            hdr->WriterPid = pid;
            hdr->WriterStartTime = start;
            hdr->WriterEpoch = 1;
            hdr->WriterWaiting = 0;
            hdr->WriterWaitFor = 0;
            hdr->WaitersMask = 0;
            hdr->ActiveMask = 0;
            hdr->ReaderGeneration = 0;
            hdr->EvictedReaders = 0;
            for (int i = 0; i < Layout.MaxReaders; i++)
            {
                ControlBlock.SlotRef(hdr, i) = default;
            }

            bool global = sectionName.StartsWith("Global\\", StringComparison.Ordinal);
            backend = SignalBackend.Create(SignalBackendId.NamedEvent, instanceId, hdr, isCreator: true, globalNamespace: global);

            TestHooks.BeforeInitState?.Invoke();
            Interlocked.MemoryBarrier();
            Volatile.Write(ref hdr->InitState, 1);                      // release: everything above becomes visible

            return new RingBuffer<T>(mapping, backend, options, sectionName, isWriter: true, capacity, instanceId, mapping.BaseAddress);
        }
        catch
        {
            backend?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing buffer by name (reader role: <see cref="CreateReader"/> works, <see cref="GetBucket"/> throws).</summary>
    /// <param name="name">The name given to <see cref="Create"/> (normalised the same way).</param>
    /// <param name="options">Opener options (<see cref="RingBufferOptions.InitializationTimeout"/>, <see cref="RingBufferOptions.LivenessCheckInterval"/>).</param>
    /// <exception cref="RingBufferNotFoundException">No section with that name exists.</exception>
    /// <exception cref="RingBufferLayoutException">The section was not created by this library for this <typeparamref name="T"/>.</exception>
    /// <exception cref="RingBufferInitializationException">The creator did not finish initialising in time or died meanwhile.</exception>
    public static RingBuffer<T> Open(string name, RingBufferOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Kernel.EnsurePlatform();
        string sectionName = MirroredSection.NormalizeName(name);
        SafeSectionHandle section = MirroredSection.OpenSection(sectionName);
        return OpenCore(section, sectionName, options ?? RingBufferOptions.Default);
    }

    /// <summary>
    /// Opens from a section handle obtained via <see cref="DuplicateSectionHandleTo"/> or inheritance. Ownership of the handle transfers
    /// to the buffer (also on failure). <see cref="Name"/> is <see langword="null"/>.
    /// </summary>
    public static RingBuffer<T> Open(SafeSectionHandle section, RingBufferOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        Kernel.EnsurePlatform();
        return OpenCore(section, null, options ?? RingBufferOptions.Default);
    }

    private static RingBuffer<T> OpenCore(SafeSectionHandle section, string? name, RingBufferOptions options)
    {
        Layout.EnsureInitialized();
        long dataBytes;
        long capacity;
        ulong creatorBase;
        ulong instanceId;
        uint backendId;
        try
        {
            if (section.IsInvalid)
            {
                throw new ArgumentException("The section handle is invalid.", nameof(section));
            }

            byte* peek = MirroredSection.MapHeaderPeek(section);
            try
            {
                ControlBlock* h = (ControlBlock*)peek;
                WaitForInit(h, options.InitializationTimeout);
                Validate(h);
                dataBytes = h->DataBytes;
                capacity = h->Capacity;
                creatorBase = h->CreatorBase;
                instanceId = h->InstanceId;
                backendId = h->SignalBackendId;
            }
            finally
            {
                MirroredSection.UnmapPeek(peek);
            }
        }
        catch
        {
            section.Dispose();
            throw;
        }

        Span<ulong> candidates = stackalloc ulong[1];
        candidates[0] = creatorBase;
        MirroredSection mapping = MirroredSection.Open(section, dataBytes, creatorBase != 0 && creatorBase % Kernel.ExpectedAllocationGranularity == 0 ? candidates : []);
        SignalBackend? backend = null;
        try
        {
            if (mapping.Mirror != mapping.Data + dataBytes)
            {
                throw new RingBufferLayoutException("The mirror view is not adjacent to the data view.");
            }

            if (options.PreFault)
            {
                PreFault(mapping.Data, dataBytes, write: false);        // an opener's first touch of every page is a soft fault too: take them here, not on the hot path
            }

            backend = SignalBackend.Create(backendId, instanceId, (ControlBlock*)mapping.Header, isCreator: false, globalNamespace: false);
            return new RingBuffer<T>(mapping, backend, options, name, isWriter: false, capacity, instanceId, creatorBase);
        }
        catch
        {
            backend?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    /// <summary>Touches every page of the data view and of the mirror view once (writes for the creator, reads for openers).</summary>
    private static void PreFault(byte* data, long dataBytes, bool write)
    {
        long page = Kernel.PageSize;
        byte* mirror = data + dataBytes;
        byte sink = 0;
        for (long p = 0; p < dataBytes; p += page)
        {
            if (write)
            {
                Volatile.Write(ref data[p], 0);
            }
            else
            {
                sink |= Volatile.Read(ref data[p]);
            }

            sink |= Volatile.Read(ref mirror[p]);
        }

        GC.KeepAlive(sink);
    }

    private static void WaitForInit(ControlBlock* h, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        long deadline = SpinClock.ToDeadline(timeout);
        long spinUntil = start + SpinClock.ToTicks(TimeSpan.FromMilliseconds(1));
        long yieldUntil = start + SpinClock.ToTicks(TimeSpan.FromMilliseconds(10));
        while (true)
        {
            if (Volatile.Read(ref h->InitState) == 1)
            {
                return;
            }

            int pid = Volatile.Read(ref h->CreatorPid);
            long st = Volatile.Read(ref h->CreatorStartTime);
            if (pid != 0 && st != 0 && !ProcessLiveness.IsAlive(pid, st))
            {
                throw new RingBufferInitializationException("The creator process died during initialization.");
            }

            long now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                throw new RingBufferInitializationException($"The buffer was not initialized within {timeout} (InitState != 1).");
            }

            if (now < spinUntil)
            {
                Thread.SpinWait(20);
            }
            else if (now < yieldUntil)
            {
                Thread.Yield();
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    private static void Validate(ControlBlock* h)
    {
        if (h->Magic != Layout.Magic)
        {
            throw new RingBufferLayoutException($"Bad magic 0x{h->Magic:X16}: the section is not a photone-ipc ring buffer.");
        }

        if (h->Version != Layout.Version)
        {
            throw new RingBufferLayoutException($"Unsupported layout version {h->Version} (expected {Layout.Version}).");
        }

        if (h->ControlBytes != Layout.ControlBytes)
        {
            throw new RingBufferLayoutException($"ControlBytes is {h->ControlBytes}, expected {Layout.ControlBytes}.");
        }

        if (h->MaxReaders != Layout.MaxReaders)
        {
            throw new RingBufferLayoutException($"MaxReaders is {h->MaxReaders}, expected {Layout.MaxReaders}.");
        }

        if (h->DataOffset != Layout.DataOffset)
        {
            throw new RingBufferLayoutException($"DataOffset is {h->DataOffset}, expected {Layout.DataOffset}.");
        }

        if (h->ElementSize != (uint)sizeof(T))
        {
            throw new RingBufferLayoutException($"Element size mismatch: the buffer holds {h->ElementSize}-byte elements, {typeof(T)} is {sizeof(T)} bytes.");
        }

        long c = h->Capacity;
        if (c <= 0 || (c & (c - 1)) != 0 || c > (1L << Internal.Capacity.MaxCapacityLog2))
        {
            throw new RingBufferLayoutException($"Capacity {c} is not a power of two in range.");
        }

        if (h->DataBytes != c * h->ElementSize || h->DataBytes % Layout.HeaderViewBytes != 0)
        {
            throw new RingBufferLayoutException($"DataBytes {h->DataBytes} does not match Capacity * ElementSize or is not a multiple of 64 KiB.");
        }

        uint hash = Internal.Capacity.TypeHash(typeof(T));
        if (h->TypeHash != 0 && h->TypeHash != hash)
        {
            throw new RingBufferLayoutException($"Element type mismatch: the buffer was created for a different type than {typeof(T)} (same size, different type hash).");
        }

        if (h->SignalBackendId != SignalBackendId.NamedEvent)
        {
            throw new RingBufferLayoutException($"Unknown signal backend id {h->SignalBackendId}.");
        }

        if (h->InstanceId == 0)
        {
            throw new RingBufferLayoutException("InstanceId is zero.");
        }
    }

    private static ulong NewInstanceId()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = BitConverter.ToUInt64(bytes);
        }
        while (id == 0);
        return id;
    }

    // ------------------------------------------------------------------ handle sharing

    /// <summary>
    /// Duplicates the section handle into process <paramref name="targetProcessId"/>; the returned value is meaningful only there
    /// (wrap it in <see cref="SafeSectionHandle"/> and pass it to <see cref="Open(SafeSectionHandle, RingBufferOptions?)"/>).
    /// Requires <c>PROCESS_DUP_HANDLE</c> on the target.
    /// </summary>
    public nint DuplicateSectionHandleTo(int targetProcessId)
    {
        ThrowIfDisposed();
        SafeProcessHandle target = Kernel.OpenProcess(Kernel.PROCESS_DUP_HANDLE, false, (uint)targetProcessId);
        int err = Kernel.LastError();
        if (target.IsInvalid)
        {
            target.Dispose();
            throw Kernel.Fail("OpenProcess", err, $"PROCESS_DUP_HANDLE on pid {targetProcessId}");
        }

        using (target)
        {
            SafeSectionHandle section = _mapping.Section;
            bool added = false;
            try
            {
                section.DangerousAddRef(ref added);
                if (!Kernel.DuplicateHandle(Kernel.GetCurrentProcess(), section.DangerousGetHandle(), target.DangerousGetHandle(), out nint dup, 0, false, Kernel.DUPLICATE_SAME_ACCESS))
                {
                    throw Kernel.Fail("DuplicateHandle", Kernel.LastError(), "section");
                }

                return dup;
            }
            finally
            {
                if (added)
                {
                    section.DangerousRelease();
                }
            }
        }
    }

    // ------------------------------------------------------------------ properties

    /// <summary>The normalised section name, or <see langword="null"/> when opened by handle.</summary>
    public string? Name => _name;

    /// <summary>Random per-buffer id (names the kernel signaling objects).</summary>
    public ulong InstanceId => _instanceId;

    /// <summary>Capacity in elements (a power of two).</summary>
    public long Capacity => _capacity;

    /// <summary><c>sizeof(T)</c>.</summary>
    public int ElementSize => sizeof(T);

    /// <summary>Size of the data region in bytes (<c>Capacity * ElementSize</c>).</summary>
    public long DataBytes => _dataBytes;

    /// <summary><see langword="true"/> in the creating process.</summary>
    public bool IsWriter => _isWriter;

    /// <summary><see langword="true"/> once the writer has disposed its buffer.</summary>
    public bool IsWriterClosed
    {
        get
        {
            if (_isWriter)
            {
                return Volatile.Read(ref _closed) != 0;             // answerable without the mapping (also after Dispose)
            }

            ThrowIfDisposed();
            return Volatile.Read(ref _hdr->WriterState) == WriterStateClosed;
        }
    }

    /// <summary><see langword="true"/> when this process mapped the buffer at the creator's virtual address (informational).</summary>
    public bool IsMappedAtCreatorAddress => _atCreatorAddress;

    /// <summary>Placeholder base address in this process.</summary>
    public ulong BaseAddress => _mapping.BaseAddress;

    /// <summary>Placeholder base address in the creating process.</summary>
    public ulong CreatorBaseAddress => _creatorBase;

    /// <summary>The published write cursor (absolute element count).</summary>
    public long WriteCursor
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _hdr->WriteCursor);
        }
    }

    /// <summary>Number of readers currently marked active.</summary>
    public int ActiveReaderCount
    {
        get
        {
            ThrowIfDisposed();
            return BitOperations.PopCount(Volatile.Read(ref _hdr->ActiveMask));
        }
    }

    /// <summary>Number of readers evicted so far (all processes).</summary>
    public long EvictedReaders
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _hdr->EvictedReaders);
        }
    }

    /// <summary>Internal diagnostics counters (writer side).</summary>
    internal Counters Counters => _counters;

    internal ControlBlock* Header => _hdr;

    internal byte* Data => _data;

    internal SignalBackend Backend => _backend;

    internal long Mask => _mask;

    internal uint LivenessCheckIntervalMs => _livenessMs;

    private ref ControlBlock Hdr => ref Unsafe.AsRef<ControlBlock>(_hdr);

    private ref ReaderSlot Slot(int i) => ref ControlBlock.SlotRef(_hdr, i);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // ------------------------------------------------------------------ dispose

    /// <summary>
    /// Writer: drops an outstanding bucket (<c>Commit(0)</c>), publishes <c>Closed</c> and wakes every reader; readers keep draining.
    /// All roles: the mapping and signaling objects are released once the last <see cref="RingReader{T}"/> created from this buffer is disposed.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
        if (_isWriter)
        {
            CloseWriter();
        }

        ReleaseLocalRef();
    }

    /// <summary>
    /// Safety net for a buffer that was never disposed: the writer still publishes <c>Closed</c> (so readers in other processes stop
    /// waiting) and the native resources are released once every reader created from it is gone. Always prefer <see cref="Dispose"/>.
    /// </summary>
    ~RingBuffer()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_isWriter)
            {
                CloseWriter();
            }
        }
        catch (Exception)
        {
            // never let a finalizer throw
        }
        finally
        {
            ReleaseLocalRef();
        }
    }

    internal void ReaderReleased() => ReleaseLocalRef();

    /// <summary>Adds a local reference unless the native resources are already released; blocking paths use it to outlive a concurrent <see cref="Dispose"/>.</summary>
    private bool TryAddLocalRef()
    {
        int v = Volatile.Read(ref _localRefs);
        while (v > 0)
        {
            int seen = Interlocked.CompareExchange(ref _localRefs, v + 1, v);
            if (seen == v)
            {
                return true;
            }

            v = seen;
        }

        return false;
    }

    private void ReleaseLocalRef()
    {
        if (Interlocked.Decrement(ref _localRefs) == 0)
        {
            ReleaseNative();
        }
    }

    private void ReleaseNative()
    {
        if (_isWriter)
        {
            for (int i = 0; i < _laggards.Length; i++)
            {
                CloseLaggard(ref _laggards[i]);
            }
        }

        _backend.Dispose();
        _mapping.Dispose();
    }
}
