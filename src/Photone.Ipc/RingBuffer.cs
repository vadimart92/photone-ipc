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
    private const long ClearChunkBytes = 1L << 30;

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
    private readonly RingBufferPool? _pool;
    private readonly PooledMapping? _pooled;        // non-null: the mapping goes back to _pool on release (DESIGN §15)
    private readonly SafeSectionHandle? _alias;     // the name object of a pooled buffer (created, or opened by name)
    private readonly long _tagLogBytes;             // 0: the buffer carries no tags (DESIGN §16)
    private readonly int _tagStateBytes;
    private readonly TagWriter? _tagWriter;         // writer of a buffer with tags

    private int _disposed;
    private int _closed;
    private int _localRefs = 1;             // the buffer itself; +1 per live RingReader created by it

    private RingBuffer(
        MirroredSection mapping,
        SignalBackend backend,
        RingBufferOptions options,
        string? name,
        bool isWriter,
        long capacity,
        ulong instanceId,
        ulong creatorBase,
        TagArea tags,
        RingBufferPool? pool = null,
        PooledMapping? pooled = null,
        SafeSectionHandle? alias = null)
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
        _spaceSpin = new SpinPolicy(options.SpinTime, options.MaxSpinTime);
        _laggards = isWriter ? new LaggardEntry[Layout.MaxReaders] : [];
        _laggardHandles = isWriter ? new nint[Layout.MaxReaders] : [];
        _laggardSlot = isWriter ? new int[Layout.MaxReaders] : [];
        _laggardWord = isWriter ? new long[Layout.MaxReaders] : [];
        _pollLaggards = isWriter ? new int[Layout.MaxReaders] : [];
        _pool = pooled is null ? null : pool;
        _pooled = pooled;
        _alias = alias;
        _tagLogBytes = tags.LogBytes;                       // the creator's own sizes, or ReadTagArea's: never re-read from shared memory
        _tagStateBytes = tags.StateBytes;
        _tagWriter = isWriter && _tagLogBytes != 0 ? new TagWriter(_hdr, TagState, _tagStateBytes, TagLog, _tagLogBytes) : null;
    }

    /// <summary>
    /// An opener's tag area: each size loaded once from the control block, and accepted only if it is sane and matches the header view actually mapped,
    /// so the tag pointers stay inside the mapping whatever the shared header says later.
    /// </summary>
    /// <exception cref="RingBufferLayoutException">The sizes are invalid or do not match the mapping.</exception>
    private static TagArea ReadTagArea(MirroredSection mapping)
    {
        ControlBlock* hdr = (ControlBlock*)mapping.Header;
        long logBytes = Volatile.Read(ref hdr->TagLogBytes);
        int stateBytes = Volatile.Read(ref hdr->TagStateBytes);
        if (!TagFormat.IsValidArea(logBytes, stateBytes) || Layout.HeaderViewBytes + stateBytes + logBytes != (long)mapping.HeaderBytes)
        {
            throw new RingBufferLayoutException($"The tag area (log {logBytes} bytes, table {stateBytes} bytes) does not match the mapped header view of {mapping.HeaderBytes} bytes.");
        }

        return new TagArea(logBytes, stateBytes);
    }

    // ------------------------------------------------------------------ create / open

    /// <summary>
    /// Creates a new buffer; this process is THE writer. <paramref name="name"/> <see langword="null"/> ⇒ <c>Local\photone.{Guid:N}</c>;
    /// a name without a namespace prefix becomes <c>Local\photone.{name}</c>; <c>Local\...</c> / <c>Global\...</c> are used verbatim.
    /// With <see cref="RingBufferOptions.Pool"/> the shared memory comes from that pool and returns to it on release (see <see cref="RingBufferPool"/>).
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
        TagArea tags = TagArea.Choose(options);
        string sectionName = MirroredSection.NormalizeName(name);
        bool global = sectionName.StartsWith("Global\\", StringComparison.Ordinal);
        if (options.Pool is RingBufferPool pool)
        {
            return CreatePooled(pool, capacity, dataBytes, tags, sectionName, global, options);
        }

        ulong instanceId = NewInstanceId();
        ulong sectionId = NewInstanceId();
        (MirroredSection mapping, SignalBackend backend) = MapNewSection(sectionName, tags.HeaderBytes, dataBytes, sectionId, global, options);
        try
        {
            ControlBlock* hdr = (ControlBlock*)mapping.Header;
            WriteHeader(hdr, capacity, dataBytes, tags, instanceId, sectionId, mapping.BaseAddress, layoutFlags: 0);
            TestHooks.BeforeInitState?.Invoke();
            Interlocked.MemoryBarrier();
            Volatile.Write(ref hdr->InitState, 1);                      // release: everything above becomes visible

            return new RingBuffer<T>(mapping, backend, options, sectionName, isWriter: true, capacity, instanceId, mapping.BaseAddress, tags);
        }
        catch
        {
            backend.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <see cref="Create"/> with a pool (DESIGN §15.2). The name becomes an alias record that names the pool-owned section and this buffer's instance id;
    /// the section is an idle one of this size that nobody else holds open (re-initialised in place), or a new one.
    /// </summary>
    private static RingBuffer<T> CreatePooled(RingBufferPool pool, long capacity, long dataBytes, TagArea tags, string name, bool global, RingBufferOptions options)
    {
        SafeSectionHandle alias = MirroredSection.CreateAliasSection(name);            // claims the name before anything else is acquired
        byte* aliasView = null;
        PooledMapping? entry = null;
        try
        {
            aliasView = MirroredSection.MapHeaderPeek(alias);
            AliasBlock* a = (AliasBlock*)aliasView;
            WriteCreatorIdentity((ControlBlock*)a);                     // same offsets as in the control block: an opener that finds the name early can tell a dead creator

            ulong instanceId = NewInstanceId();
            entry = pool.RentForCreate(tags.HeaderBytes, dataBytes, global, options.PreferredBaseAddress);
            if (entry is null)
            {
                ulong sectionId = NewInstanceId();
                string sectionName = MirroredSection.NewPoolSectionName(global);
                (MirroredSection mapping, SignalBackend backend) = MapNewSection(sectionName, tags.HeaderBytes, dataBytes, sectionId, global, options);
                entry = new PooledMapping(mapping, backend, dataBytes, sectionId, sectionName, global, isCreator: true);
            }
            else
            {
                PrepareReuse((ControlBlock*)entry.Mapping.Header, entry.Mapping.Data, dataBytes, tags, instanceId, pool.ClearOnReuse, options.PreFault);
            }

            ControlBlock* hdr = (ControlBlock*)entry.Mapping.Header;
            WriteHeader(hdr, capacity, dataBytes, tags, instanceId, entry.SectionId, entry.Mapping.BaseAddress, LayoutFlag.Pooled);

            string target = entry.SectionName!;
            a->Magic = AliasBlock.MagicValue;
            a->Version = AliasBlock.CurrentVersion;
            a->SectionId = entry.SectionId;
            a->DataBytes = dataBytes;
            a->InstanceId = instanceId;
            target.AsSpan().CopyTo(new Span<char>(a->TargetName, AliasBlock.MaxTargetNameChars));
            a->TargetNameLength = target.Length;

            TestHooks.BeforeInitState?.Invoke();
            Interlocked.MemoryBarrier();
            Volatile.Write(ref hdr->InitState, 1);                      // the ring first: an opener that got through the alias never waits on the ring
            Volatile.Write(ref a->InitState, 1);
            MirroredSection.UnmapPeek(aliasView);
            aliasView = null;

            return new RingBuffer<T>(entry.Mapping, entry.Backend, options, name, isWriter: true, capacity, instanceId, entry.Mapping.BaseAddress, tags, pool, entry, alias);
        }
        catch
        {
            if (aliasView != null)
            {
                try
                {
                    MirroredSection.UnmapPeek(aliasView);
                }
                catch (PhotoneIpcException)
                {
                    // keep the original exception
                }
            }

            entry?.Release();                                           // a half-initialised section never goes back to the pool
            alias.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates and maps a new section and its signaling objects: creator identity first, then the optional pre-fault. The rest of the header is
    /// <see cref="WriteHeader"/>'s.
    /// </summary>
    private static (MirroredSection Mapping, SignalBackend Backend) MapNewSection(string sectionName, long headerBytes, long dataBytes, ulong sectionId, bool global, RingBufferOptions options)
    {
        ulong total = (ulong)headerBytes + 2 * (ulong)dataBytes;
        Span<ulong> candidates = stackalloc ulong[AddressHint.MaxProbes];
        int candidateCount = AddressHint.Candidates(sectionId, total, options.PreferredBaseAddress, candidates);

        SafeSectionHandle section = MirroredSection.CreateSection(headerBytes, dataBytes, sectionName);
        MirroredSection mapping = MirroredSection.Create(section, headerBytes, dataBytes, candidates[..candidateCount]);   // owns the section; self-tests the mirror
        try
        {
            ControlBlock* hdr = (ControlBlock*)mapping.Header;
            if (mapping.Mirror != mapping.Data + dataBytes)
            {
                throw new RingBufferLayoutException("The mirror view is not adjacent to the data view.");
            }

            WriteCreatorIdentity(hdr);                                  // before anything slow (pre-faulting a large buffer takes a while)
            if (options.PreFault)
            {
                PreFault(mapping.Data, dataBytes, write: true);         // populate this process's PTEs for both views (the mirror is the same physical pages)
            }

            return (mapping, SignalBackend.Create(SignalBackendId.NamedEvent, sectionId, hdr, isCreator: true, globalNamespace: global));
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }

    /// <summary>Start time FIRST, pid SECOND: openers use <c>Pid != 0 &amp;&amp; StartTime != 0</c> to detect a creator dying mid-init.</summary>
    private static void WriteCreatorIdentity(ControlBlock* hdr)
    {
        hdr->CreatorStartTime = ProcessLiveness.OwnStartTime;
        Volatile.Write(ref hdr->CreatorPid, Environment.ProcessId);
    }

    /// <summary>Every control-block field except the creator identity, <c>InitState</c> and the backend area (DESIGN §3.3 step 5).</summary>
    private static void WriteHeader(ControlBlock* hdr, long capacity, long dataBytes, TagArea tags, ulong instanceId, ulong sectionId, ulong baseAddress, uint layoutFlags)
    {
        hdr->Magic = Layout.Magic;
        hdr->Version = Layout.Version;
        hdr->ControlBytes = Layout.ControlBytes;
        hdr->ElementSize = (uint)sizeof(T);
        hdr->MaxReaders = Layout.MaxReaders;
        hdr->Capacity = capacity;
        hdr->DataBytes = dataBytes;
        hdr->DataOffset = tags.HeaderBytes;
        hdr->TypeHash = Internal.Capacity.TypeHash(typeof(T));
        hdr->SignalBackendId = SignalBackendId.NamedEvent;
        hdr->LayoutFlags = layoutFlags;
        hdr->CreatorBase = baseAddress;
        hdr->InstanceId = instanceId;
        hdr->ReservationBytes = (ulong)tags.HeaderBytes + 2 * (ulong)dataBytes;
        hdr->SectionId = sectionId;
        hdr->TagLogBytes = tags.LogBytes;
        hdr->TagStateBytes = tags.StateBytes;
        hdr->TagEnd = 0;
        hdr->TagVersion = 0;
        hdr->TagSnapshotEnd = 0;
        hdr->TagSnapshotW = 0;
        hdr->TagStateUsed = 0;
        hdr->TagStateCount = 0;
        hdr->WriteCursor = 0;
        hdr->ReserveEnd = 0;
        hdr->WriterState = WriterStateActive;
        hdr->WriterPid = Environment.ProcessId;
        hdr->WriterStartTime = ProcessLiveness.OwnStartTime;
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
    }

    /// <summary>
    /// A section taken from the pool (its <c>InitState</c> is already 0, DESIGN §15.3): clears the control block except the backend's namespace byte,
    /// stores the new instance id and the creator identity, zeroes the data if the pool says so, and re-touches the pages (an idle mapping may have been
    /// trimmed from the working set; the pages themselves are still there, so this costs a pass over memory, not a fault per page).
    /// The instance id goes in before the slow parts, so an opener still waiting for the previous buffer gives up at once instead of waiting them out.
    /// </summary>
    private static void PrepareReuse(ControlBlock* hdr, byte* data, long dataBytes, TagArea tags, ulong instanceId, bool clearData, bool preFault)
    {
        byte backendNamespace = hdr->BackendArea[0];
        new Span<byte>(hdr, (int)Layout.ControlBytes).Clear();         // also TagEnd and the tag snapshot: the previous buffer's tags are unreachable
        hdr->BackendArea[0] = backendNamespace;
        hdr->InstanceId = instanceId;
        WriteCreatorIdentity(hdr);
        if (clearData)
        {
            ClearRegion((byte*)hdr + Layout.HeaderViewBytes, tags.StateBytes + tags.LogBytes);
            ClearRegion(data, dataBytes);
        }

        if (preFault)
        {
            PreFault(data, dataBytes, write: true);
        }
    }

    private static void ClearRegion(byte* start, long bytes)
    {
        for (long offset = 0; offset < bytes; offset += ClearChunkBytes)
        {
            new Span<byte>(start + offset, (int)Math.Min(ClearChunkBytes, bytes - offset)).Clear();
        }
    }

    /// <summary>Opens an existing buffer by name (reader role: <see cref="CreateReader"/> works, <see cref="GetBucket"/> throws).</summary>
    /// <param name="name">The name given to <see cref="Create"/> (normalised the same way).</param>
    /// <param name="options">
    /// Opener options (<see cref="RingBufferOptions.InitializationTimeout"/>, <see cref="RingBufferOptions.LivenessCheckInterval"/>, <see cref="RingBufferOptions.PreFault"/>,
    /// <see cref="RingBufferOptions.Pool"/>).
    /// </param>
    /// <exception cref="RingBufferNotFoundException">No section with that name exists, or the pooled buffer it named is gone.</exception>
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
        SafeSectionHandle? alias = null;
        long headerBytes;
        long dataBytes;
        long capacity;
        ulong creatorBase;
        ulong instanceId;
        ulong sectionId;
        uint backendId;
        bool pooled;
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
                WaitForInit(h, options.InitializationTimeout);          // an alias and a ring share the InitState / creator identity offsets
                if (h->Magic == AliasBlock.MagicValue)
                {
                    // A pooled buffer's name (DESIGN §15.4). The alias stays open for the buffer's lifetime, as a direct section handle would (the name
                    // lives while anyone holds it); the section it names must still serve the instance the name was given to.
                    (string target, ulong expectedInstance, ulong aliasSectionId) = ReadAlias((AliasBlock*)peek);
                    MirroredSection.UnmapPeek(peek);
                    peek = null;
                    alias = section;
                    TestHooks.AfterAliasResolved?.Invoke();
                    section = OpenAliasTarget(target, name);
                    if (options.Pool is RingBufferPool revivePool
                        && TryRevive(revivePool, section, aliasSectionId, expectedInstance, name, alias, options) is RingBuffer<T> revived)
                    {
                        return revived;
                    }

                    peek = MirroredSection.MapHeaderPeek(section);
                    h = (ControlBlock*)peek;
                    if (!WaitForInit(h, options.InitializationTimeout, expectedInstance) || (h->Magic == Layout.Magic && h->InstanceId != expectedInstance))
                    {
                        throw BufferGone(name);
                    }
                }

                Validate(h);
                pooled = (h->LayoutFlags & LayoutFlag.Pooled) != 0;
                if (pooled && alias is null && name is not null)
                {
                    throw new RingBufferNotFoundException(
                        $"'{name}' is a section kept by a RingBufferPool, not a buffer name; open the buffer by the name it was created with.", Kernel.ERROR_FILE_NOT_FOUND);
                }

                headerBytes = h->DataOffset;
                dataBytes = h->DataBytes;
                capacity = h->Capacity;
                creatorBase = h->CreatorBase;
                instanceId = h->InstanceId;
                sectionId = h->SectionId;
                backendId = h->SignalBackendId;
            }
            finally
            {
                if (peek != null)
                {
                    MirroredSection.UnmapPeek(peek);
                }
            }

            if (pooled && alias is null && options.Pool is RingBufferPool handlePool
                && TryRevive(handlePool, section, sectionId, instanceId, name, alias: null, options) is RingBuffer<T> revivedByHandle)
            {
                return revivedByHandle;
            }
        }
        catch
        {
            section.Dispose();
            alias?.Dispose();
            throw;
        }

        Span<ulong> candidates = stackalloc ulong[1];
        candidates[0] = creatorBase;
        MirroredSection mapping;
        try
        {
            mapping = MirroredSection.Open(section, headerBytes, dataBytes, creatorBase != 0 && creatorBase % Kernel.ExpectedAllocationGranularity == 0 ? candidates : []);
        }
        catch
        {
            alias?.Dispose();                                           // Open has already disposed the section
            throw;
        }

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

            backend = SignalBackend.Create(backendId, sectionId, (ControlBlock*)mapping.Header, isCreator: false, globalNamespace: false);
            PooledMapping? parkable = pooled && options.Pool is not null
                ? new PooledMapping(mapping, backend, dataBytes, sectionId, sectionName: null, global: false, isCreator: false)
                : null;
            return new RingBuffer<T>(mapping, backend, options, name, isWriter: false, capacity, instanceId, creatorBase, ReadTagArea(mapping), options.Pool, parkable, alias);
        }
        catch
        {
            backend?.Dispose();
            mapping.Dispose();
            alias?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opener side of the pool (DESIGN §15.5). If <paramref name="pool"/> holds a parked mapping of section <paramref name="sectionId"/>, checks through that
    /// mapping that the section serves <paramref name="expectedInstance"/> (loads made after <paramref name="section"/> was opened, so the creator cannot
    /// reuse the section behind them) and gives it the handle. <see langword="null"/> when nothing is parked, or when the parked mapping shows another
    /// state of the section: it goes back to the pool and the caller maps afresh.
    /// </summary>
    private static RingBuffer<T>? TryRevive(RingBufferPool pool, SafeSectionHandle section, ulong sectionId, ulong expectedInstance, string? name, SafeSectionHandle? alias, RingBufferOptions options)
    {
        PooledMapping? parked = pool.TakeParked(sectionId);
        if (parked is null)
        {
            return null;
        }

        ControlBlock* h = (ControlBlock*)parked.Mapping.Header;
        try
        {
            if (!WaitForInit(h, options.InitializationTimeout, expectedInstance)
                || h->Magic != Layout.Magic || h->SectionId != sectionId || h->InstanceId != expectedInstance || h->DataBytes != parked.DataBytes
                || h->DataOffset != parked.HeaderBytes)
            {
                pool.Return(parked);
                return null;
            }

            Validate(h);
        }
        catch
        {
            pool.Return(parked);                                        // still a good view of its section (e.g. the caller asked for the wrong T)
            throw;
        }

        try
        {
            if (options.PreFault)
            {
                PreFault(parked.Mapping.Data, parked.DataBytes, write: false);   // an idle mapping may have been trimmed from the working set
            }

            parked.Mapping.AttachSection(section);
            var buffer = new RingBuffer<T>(parked.Mapping, parked.Backend, options, name, isWriter: false, h->Capacity, h->InstanceId, h->CreatorBase, ReadTagArea(parked.Mapping), pool, parked, alias);
            pool.CountRevived();
            return buffer;
        }
        catch
        {
            parked.Release();
            throw;
        }
    }

    private static (string Target, ulong InstanceId, ulong SectionId) ReadAlias(AliasBlock* a)
    {
        if (a->Version != AliasBlock.CurrentVersion)
        {
            throw new RingBufferLayoutException($"Unsupported alias version {a->Version} (expected {AliasBlock.CurrentVersion}).");
        }

        int length = a->TargetNameLength;
        if (length <= 0 || length > AliasBlock.MaxTargetNameChars || a->InstanceId == 0 || a->SectionId == 0)
        {
            throw new RingBufferLayoutException("The alias record of the pooled buffer is malformed.");
        }

        return (new string(a->TargetName, 0, length), a->InstanceId, a->SectionId);
    }

    private static SafeSectionHandle OpenAliasTarget(string target, string? name)
    {
        try
        {
            return MirroredSection.OpenSection(target);
        }
        catch (RingBufferNotFoundException ex)
        {
            throw BufferGone(name, ex);
        }
    }

    private static RingBufferNotFoundException BufferGone(string? name, Exception? inner = null)
    {
        string message = name is null
            ? "The buffer no longer exists: its pooled section has been released or reused."
            : $"The buffer '{name}' no longer exists: its pooled section has been released or reused.";
        return inner is null ? new RingBufferNotFoundException(message, Kernel.ERROR_FILE_NOT_FOUND) : new RingBufferNotFoundException(message, inner);
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

    /// <summary>Waits for <c>InitState == 1</c>, checking the creator's liveness meanwhile.</summary>
    /// <param name="h">A control block, or an <see cref="AliasBlock"/> (same offsets for everything read here).</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="expectedInstance">
    /// Non-zero when the caller wants one particular buffer in a pooled section: the wait ends early, with <see langword="false"/>, once the header shows
    /// another instance id (a re-initialisation stores the new id before its slow parts, DESIGN §15.3).
    /// </param>
    /// <returns><see langword="true"/> once initialised; <see langword="false"/> when the expected instance has been superseded.</returns>
    private static bool WaitForInit(ControlBlock* h, TimeSpan timeout, ulong expectedInstance = 0)
    {
        long start = Stopwatch.GetTimestamp();
        long deadline = SpinClock.ToDeadline(timeout);
        long spinUntil = start + SpinClock.ToTicks(TimeSpan.FromMilliseconds(1));
        long yieldUntil = start + SpinClock.ToTicks(TimeSpan.FromMilliseconds(10));
        while (true)
        {
            if (Volatile.Read(ref h->InitState) == 1)
            {
                return true;
            }

            if (expectedInstance != 0)
            {
                ulong current = Volatile.Read(ref h->InstanceId);
                if (current != 0 && current != expectedInstance)
                {
                    return false;
                }
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

        if (!TagFormat.IsValidArea(h->TagLogBytes, h->TagStateBytes) || h->DataOffset != Layout.HeaderViewBytes + h->TagStateBytes + h->TagLogBytes)
        {
            throw new RingBufferLayoutException(
                $"The tag area (log {h->TagLogBytes} bytes, table {h->TagStateBytes} bytes) does not match DataOffset {h->DataOffset}.");
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
        // Holding a local reference keeps a concurrent Dispose from releasing the section, or from returning it to a pool that could reuse it for
        // another buffer before the duplicate exists (a duplicate made afterwards would silently reach that other buffer; DESIGN §15.3).
        if (!TryAddLocalRef())
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        try
        {
            ThrowIfDisposed();
            return DuplicateSectionHandleCore(targetProcessId);
        }
        finally
        {
            ReleaseLocalRef();
        }
    }

    private nint DuplicateSectionHandleCore(int targetProcessId)
    {
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

    /// <summary>Size of the tag log in bytes (<see cref="RingBufferOptions.TagCapacity"/> rounded up); 0 when the buffer carries no tags.</summary>
    public long TagCapacity => _tagLogBytes;

    /// <summary>Size of the persistent-tag table in bytes (<see cref="RingBufferOptions.PersistentTagCapacity"/> plus the rounding slack); 0 without tags.</summary>
    public int PersistentTagCapacity => _tagStateBytes;

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

    internal RingBufferOptions Options => _options;

    /// <summary>The persistent-tag table (the start of the tag area).</summary>
    internal byte* TagState => (byte*)_hdr + Layout.HeaderViewBytes;

    /// <summary>The tag log (after the persistent-tag table).</summary>
    internal byte* TagLog => (byte*)_hdr + Layout.HeaderViewBytes + _tagStateBytes;

    /// <summary>The pooled mapping this buffer returns on release, or <see langword="null"/> when it is not pooled.</summary>
    internal PooledMapping? Pooled => _pooled;

    private ref ControlBlock Hdr => ref Unsafe.AsRef<ControlBlock>(_hdr);

    private ref ReaderSlot Slot(int i) => ref ControlBlock.SlotRef(_hdr, i);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // ------------------------------------------------------------------ dispose

    /// <summary>
    /// Writer: drops an outstanding bucket (<c>Commit(0)</c>), publishes <c>Closed</c> and wakes every reader; readers keep draining.
    /// All roles: the mapping and signaling objects are released (or returned to <see cref="RingBufferOptions.Pool"/>) once the last
    /// <see cref="RingReader{T}"/> created from this buffer is disposed.
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
            ReleaseLocalRef(finalizing: true);
        }
    }

    /// <summary>A reader created from this buffer is gone (<paramref name="finalizing"/>: released by its finalizer).</summary>
    internal void ReaderReleased(bool finalizing = false) => ReleaseLocalRef(finalizing);

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

    private void ReleaseLocalRef(bool finalizing = false)
    {
        if (Interlocked.Decrement(ref _localRefs) == 0)
        {
            ReleaseNative(finalizing);
        }
    }

    /// <summary>
    /// Closes the laggard handles and the alias, then returns a pooled mapping to its pool, or releases the mapping and the events. A pooled mapping is
    /// released too when the writer was closed with a bucket outstanding: another thread may still be filling that bucket's span (unsupported, but it
    /// must fault on unmapped memory, not write into whatever buffer the pool hands the section to next).
    /// </summary>
    /// <param name="finalizing">
    /// Called from a finalizer: the mapping's own SafeHandles may be queued for finalization in the same collection, so a pooled mapping is released
    /// instead of going back to the pool (which would keep a handle whose release is already scheduled).
    /// </param>
    private void ReleaseNative(bool finalizing)
    {
        if (_isWriter)
        {
            for (int i = 0; i < _laggards.Length; i++)
            {
                CloseLaggard(ref _laggards[i]);
            }
        }

        _alias?.Dispose();                                          // a pooled buffer's name goes away with its last holder, as a direct section's name does
        if (_pooled is not null && !finalizing && !_closedWithBucket)
        {
            _pool!.Return(_pooled);                                 // creator: idle until reused; opener: parked (views and events kept, handle closed)
            return;
        }

        _backend.Dispose();
        _mapping.Dispose();
    }
}
