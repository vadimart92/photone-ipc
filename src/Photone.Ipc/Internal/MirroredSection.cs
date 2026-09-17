using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace Photone.Ipc.Internal;

/// <summary>
/// One placeholder of <c>G + 2D</c> bytes carrying three views of one pagefile-backed section:
/// <c>[Header (section 0..G)][Data (section G..G+D)][Mirror (section G..G+D again)]</c>, so <c>Mirror == Data + D</c>
/// and a span starting anywhere in <c>Data</c> may run up to <c>D</c> bytes past its end without copying.
/// A buffer with cross-process tags has a tag reserve after the data (section <c>G + D..</c>, DESIGN §16.2), which this class does not map:
/// <see cref="SectionView"/> maps parts of it on demand. This file is the ONLY code in the library that maps or unmaps memory (DESIGN §3.2–§3.5).
/// The handle is the placeholder base; releasing it unmaps the three views (which frees the VA outright, verified) and closes the section.
/// A pooled opener mapping may keep its views without a section handle while it is parked (<see cref="DetachSection"/>, DESIGN §15).
/// </summary>
internal sealed unsafe class MirroredSection : SafeHandle
{
    private const string LocalPrefix = "Local\\";
    private const string GlobalPrefix = "Global\\";
    private const string DefaultNamePrefix = "Local\\photone.";
    private const string PoolNamePart = "photone.pool.";

    private SafeSectionHandle? _section;
    private readonly nuint _dataBytes;
    private readonly byte* _header;
    private readonly byte* _data;
    private readonly bool _atRequestedAddress;

    private MirroredSection(SafeSectionHandle section, nuint dataBytes, byte* basePtr, bool atRequestedAddress)
        : base(invalidHandleValue: 0, ownsHandle: true)
    {
        _section = section;
        _dataBytes = dataBytes;
        _header = basePtr;
        _data = basePtr + Layout.HeaderViewBytes;
        _atRequestedAddress = atRequestedAddress;
        SetHandle((nint)basePtr);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == 0;

    /// <summary>The section handle; owned by this object and closed on release (or by <see cref="DetachSection"/>).</summary>
    /// <exception cref="InvalidOperationException">The handle is detached.</exception>
    public SafeSectionHandle Section => _section ?? throw new InvalidOperationException("The section handle is detached from this mapping.");

    /// <summary>
    /// Closes the section handle and keeps the three views. The views keep the memory alive but are invisible to the system-wide handle count, which is
    /// what lets the creator's pool reuse the section while this mapping is parked. Idempotent.
    /// </summary>
    public void DetachSection() => Interlocked.Exchange(ref _section, null)?.Dispose();

    /// <summary>Gives a detached mapping the handle of the section its views show (the caller has proved it is the same section); ownership transfers.</summary>
    /// <exception cref="InvalidOperationException">A handle is already attached.</exception>
    public void AttachSection(SafeSectionHandle section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (Interlocked.CompareExchange(ref _section, section, null) is not null)
        {
            throw new InvalidOperationException("A section handle is already attached to this mapping.");
        }
    }

    /// <summary>Size of the data region in bytes (a multiple of 64 KiB).</summary>
    public nuint DataBytes => _dataBytes;

    /// <summary>Size of the whole placeholder: <c>G + 2D</c>.</summary>
    public nuint ReservationBytes => Layout.HeaderViewBytes + 2 * _dataBytes;

    /// <summary>Placeholder base (== <see cref="Header"/>).</summary>
    public byte* Base => _header;

    /// <summary>Placeholder base as an integer (what peers receive as <c>CreatorBase</c>).</summary>
    public ulong BaseAddress => (ulong)_header;

    /// <summary>View of section <c>[0, G)</c>.</summary>
    public byte* Header => _header;

    /// <summary>View of section <c>[G, G + D)</c>.</summary>
    public byte* Data => _data;

    /// <summary>Second view of section <c>[G, G + D)</c>, placed at <c>Data + D</c>.</summary>
    public byte* Mirror => _data + _dataBytes;

    /// <summary><see langword="true"/> when the placeholder landed on one of the caller's requested addresses (not the system fallback).</summary>
    public bool AtRequestedAddress => _atRequestedAddress;

    // ------------------------------------------------------------------ naming

    /// <summary>
    /// <see langword="null"/> ⇒ <c>Local\photone.{Guid:N}</c>; a name starting with <c>Local\</c> or <c>Global\</c> is used verbatim;
    /// anything else becomes <c>Local\photone.{name}</c>.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (name is null)
        {
            return DefaultNamePrefix + Guid.NewGuid().ToString("N");
        }

        if (name.Length == 0)
        {
            throw new ArgumentException("Name must not be empty.", nameof(name));
        }

        if (name.StartsWith(LocalPrefix, StringComparison.Ordinal) || name.StartsWith(GlobalPrefix, StringComparison.Ordinal))
        {
            return name;
        }

        if (name.Contains('\\'))
        {
            throw new ArgumentException("Name must not contain a backslash unless it starts with Local\\ or Global\\.", nameof(name));
        }

        return DefaultNamePrefix + name;
    }

    /// <summary><c>Local\photone.pool.{Guid:N}</c> (or under <c>Global\</c>): the kernel name of a pool-owned section (DESIGN §15).</summary>
    public static string NewPoolSectionName(bool global) => (global ? GlobalPrefix : LocalPrefix) + PoolNamePart + Guid.NewGuid().ToString("N");

    // ------------------------------------------------------------------ sections

    /// <summary>
    /// Creates a new pagefile-backed section of <c>G + dataBytes + tagReserveBytes</c> bytes. Without a tag reserve the whole section is committed
    /// (<c>PAGE_READWRITE | SEC_COMMIT</c>). With one it is only reserved (<c>SEC_RESERVE</c>, DESIGN §16.2), which costs neither memory nor commit charge:
    /// <see cref="Create"/> commits the control view and the data, and the writer commits tag memory as it needs it (<see cref="SectionView.Commit"/>).
    /// </summary>
    /// <param name="dataBytes">Data region size; a positive multiple of 64 KiB.</param>
    /// <param name="tagReserveBytes">Size of the tag reserve after the data (a multiple of 64 KiB), or 0.</param>
    /// <param name="sectionName">Already-normalised object name, or <see langword="null"/> for an anonymous section.</param>
    /// <exception cref="RingBufferAlreadyExistsException">An object with that name already exists.</exception>
    public static SafeSectionHandle CreateSection(long dataBytes, long tagReserveBytes, string? sectionName)
    {
        Kernel.EnsurePlatform();
        ValidateDataBytes(dataBytes);
        if (tagReserveBytes < 0 || tagReserveBytes % Layout.HeaderViewBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tagReserveBytes), tagReserveBytes, "tagReserveBytes must be a non-negative multiple of 65536.");
        }

        return CreateSectionCore(Layout.HeaderViewBytes + (ulong)dataBytes + (ulong)tagReserveBytes, sectionName, reserveOnly: tagReserveBytes != 0);
    }

    /// <summary>Creates a new, committed section of <c>G + dataBytes</c> bytes without a tag reserve.</summary>
    /// <exception cref="RingBufferAlreadyExistsException">An object with that name already exists.</exception>
    public static SafeSectionHandle CreateSection(long dataBytes, string? sectionName) => CreateSection(dataBytes, 0, sectionName);

    /// <summary>
    /// Creates the 64 KiB named section that holds a pooled buffer's <see cref="AliasBlock"/> (only its first page is ever touched; its size lets
    /// <see cref="MapHeaderPeek"/> map either kind of section).
    /// </summary>
    /// <exception cref="RingBufferAlreadyExistsException">An object with that name already exists.</exception>
    public static SafeSectionHandle CreateAliasSection(string sectionName)
    {
        Kernel.EnsurePlatform();
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        return CreateSectionCore(Layout.HeaderViewBytes, sectionName, reserveOnly: false);
    }

    private static SafeSectionHandle CreateSectionCore(ulong size, string? sectionName, bool reserveOnly)
    {
        PAGE_PROTECTION_FLAGS protect = reserveOnly ? PAGE_PROTECTION_FLAGS.PAGE_READWRITE | PAGE_PROTECTION_FLAGS.SEC_RESERVE : PAGE_PROTECTION_FLAGS.PAGE_READWRITE;
        SafeSectionHandle section = Kernel.CreateFileMapping(-1, null, protect, (uint)(size >> 32), (uint)size, sectionName);
        int err = Kernel.LastError();                                  // read even on success (183 = opened existing)
        if (section.IsInvalid)
        {
            section.Dispose();
            string? detail = err switch
            {
                (int)WIN32_ERROR.ERROR_ACCESS_DENIED when sectionName is not null && sectionName.StartsWith(GlobalPrefix, StringComparison.Ordinal)
                    => "creating a Global\\ section requires SeCreateGlobalPrivilege",
                (int)WIN32_ERROR.ERROR_COMMITMENT_LIMIT or (int)WIN32_ERROR.ERROR_NOT_ENOUGH_MEMORY
                    => $"the section ({size} bytes) exceeds the system commit limit",
                _ => null,
            };
            throw Kernel.Fail("CreateFileMappingW", err, detail);
        }

        if (err == (int)WIN32_ERROR.ERROR_ALREADY_EXISTS)
        {
            section.Dispose();
            throw new RingBufferAlreadyExistsException($"A section named '{sectionName}' already exists.", err);
        }

        return section;
    }

    /// <summary>Opens an existing section by (already-normalised) name with read/write access.</summary>
    /// <exception cref="RingBufferNotFoundException">No object with that name exists.</exception>
    public static SafeSectionHandle OpenSection(string sectionName)
    {
        Kernel.EnsurePlatform();
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        SafeSectionHandle section = Kernel.OpenFileMapping(FILE_MAP.FILE_MAP_READ | FILE_MAP.FILE_MAP_WRITE, false, sectionName);
        int err = Kernel.LastError();
        if (section.IsInvalid)
        {
            section.Dispose();
            if (err == (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                throw new RingBufferNotFoundException($"No section named '{sectionName}' exists.", err);
            }

            throw Kernel.Fail("OpenFileMappingW", err, sectionName);
        }

        return section;
    }

    // ------------------------------------------------------------------ header peek

    /// <summary>
    /// Maps only the first 64 KiB of the section (no placeholder) so an opener can read the control block before committing to a full mapping.
    /// Release with <see cref="UnmapPeek"/>.
    /// <para>
    /// The view is committed first. A section with a tag reserve is created reserved (DESIGN §16.2), and an opener that finds its name before the creator
    /// has committed the control view would otherwise fault on its first load. Committing is idempotent and a no-op on a committed section, and it
    /// preserves whatever the creator has stored.
    /// </para>
    /// </summary>
    /// <exception cref="RingBufferLayoutException">The section is smaller than 64 KiB.</exception>
    public static byte* MapHeaderPeek(SafeSectionHandle section)
    {
        ArgumentNullException.ThrowIfNull(section);
        void* p = Kernel.MapViewOfFile3(section, 0, null, 0, Layout.HeaderViewBytes, 0, PAGE_PROTECTION_FLAGS.PAGE_READWRITE, null, 0);
        int err = Kernel.LastError();
        if (p == null)
        {
            if (err == (int)WIN32_ERROR.ERROR_ACCESS_DENIED)
            {
                throw new RingBufferLayoutException("The section is smaller than the 64 KiB header (or the handle lacks write access).", err);
            }

            throw Kernel.Fail("MapViewOfFile3", err, "header peek");
        }

        if (Kernel.VirtualAlloc(p, Layout.HeaderViewBytes, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE) == null)
        {
            err = Kernel.LastError();
            Kernel.UnmapViewOfFile(p);
            throw Kernel.Fail("VirtualAlloc", err, "header peek commit");
        }

        return (byte*)p;
    }

    /// <summary>Unmaps a view returned by <see cref="MapHeaderPeek"/>.</summary>
    public static void UnmapPeek(byte* peek)
    {
        if (peek != null && !Kernel.UnmapViewOfFile(peek))
        {
            throw Kernel.Fail("UnmapViewOfFile", Kernel.LastError(), "header peek");
        }
    }

    // ------------------------------------------------------------------ create / open

    /// <summary>
    /// Creator side: reserves the placeholder (trying <paramref name="candidates"/> in order, then a system-chosen address), maps the three views
    /// and runs the write/verify/restore mirror self-test on <c>data[0]</c> and <c>data[D-1]</c>. Only the creator may call this
    /// (the section must be fresh: nobody else can observe the self-test stores).
    /// </summary>
    /// <param name="section">Section of at least <c>G + dataBytes</c> bytes; ownership transfers to the result (also on failure).</param>
    /// <param name="dataBytes">Data region size; a positive multiple of 64 KiB.</param>
    /// <param name="candidates">64 KiB-aligned base addresses to try first; may be empty.</param>
    /// <param name="commit">The section was created reserved (it has a tag reserve): commit the control view and the data before touching them.</param>
    /// <exception cref="RingBufferLayoutException">The mirror does not alias the data region.</exception>
    public static MirroredSection Create(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates, bool commit = false)
        => Map(section, dataBytes, candidates, selfTest: true, commit);

    /// <summary>
    /// Opener side: reserves the placeholder (trying <paramref name="candidates"/> — normally the creator's base — then a system-chosen address)
    /// and maps the three views. No content check: a live writer may be storing into <c>data[0]</c>. Placement (<c>Mirror == Data + D</c>) is asserted.
    /// </summary>
    /// <param name="section">Section of at least <c>G + dataBytes</c> bytes; ownership transfers to the result (also on failure).</param>
    /// <param name="dataBytes">Data region size; a positive multiple of 64 KiB.</param>
    /// <param name="candidates">64 KiB-aligned base addresses to try first; may be empty.</param>
    /// <exception cref="RingBufferLayoutException">The section is smaller than the header claims.</exception>
    public static MirroredSection Open(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates)
        => Map(section, dataBytes, candidates, selfTest: false, commit: false);

    private static MirroredSection Map(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates, bool selfTest, bool commit)
    {
        ArgumentNullException.ThrowIfNull(section);
        try
        {
            Kernel.EnsurePlatform();
            Layout.EnsureInitialized();
            ValidateDataBytes(dataBytes);
            if (section.IsInvalid)
            {
                throw new ArgumentException("The section handle is invalid.", nameof(section));
            }

            foreach (ulong c in candidates)
            {
                if (c % Kernel.ExpectedAllocationGranularity != 0)
                {
                    throw new ArgumentException($"Candidate base address 0x{c:X} is not a multiple of 64 KiB.", nameof(candidates));
                }
            }

            nuint g = Layout.HeaderViewBytes;
            nuint d = (nuint)dataBytes;
            nuint total = g + 2 * d;

            // 1. one placeholder for the whole range; a requested address either succeeds as a unit or fails (487 = occupied here)
            byte* basePtr = null;
            bool atRequested = false;
            foreach (ulong c in candidates)
            {
                basePtr = (byte*)Kernel.VirtualAlloc2(0, (void*)c, total, VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE_PLACEHOLDER, PAGE_PROTECTION_FLAGS.PAGE_NOACCESS, null, 0);
                if (basePtr != null)
                {
                    atRequested = true;
                    break;
                }
            }

            if (basePtr == null)
            {
                basePtr = (byte*)Kernel.VirtualAlloc2(0, null, total, VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE_PLACEHOLDER, PAGE_PROTECTION_FLAGS.PAGE_NOACCESS, null, 0);
                if (basePtr == null)
                {
                    throw Kernel.Fail("VirtualAlloc2", Kernel.LastError(), $"placeholder of {total} bytes");
                }
            }

            // 2. split into [base, base+G) [base+G, base+G+D) [base+G+D, base+G+2D)
            if (!Kernel.VirtualFree(basePtr, g, VIRTUAL_FREE_TYPE.MEM_RELEASE | (VIRTUAL_FREE_TYPE)UNMAP_VIEW_OF_FILE_FLAGS.MEM_PRESERVE_PLACEHOLDER))
            {
                int err = Kernel.LastError();
                Kernel.VirtualFree(basePtr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);                    // unsplit: one call frees everything
                throw Kernel.Fail("VirtualFree", err, "split 1 (MEM_PRESERVE_PLACEHOLDER)");
            }

            if (!Kernel.VirtualFree(basePtr + g, d, VIRTUAL_FREE_TYPE.MEM_RELEASE | (VIRTUAL_FREE_TYPE)UNMAP_VIEW_OF_FILE_FLAGS.MEM_PRESERVE_PLACEHOLDER))
            {
                int err = Kernel.LastError();
                Kernel.VirtualFree(basePtr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                Kernel.VirtualFree(basePtr + g, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                throw Kernel.Fail("VirtualFree", err, "split 2 (MEM_PRESERVE_PLACEHOLDER)");
            }

            // 3. replace each piece with a view; on failure unmap the views and release the remaining placeholders
            Span<bool> isView = stackalloc bool[3];
            isView.Clear();
            byte** pieces = stackalloc byte*[3];
            pieces[0] = basePtr;
            pieces[1] = basePtr + g;
            pieces[2] = basePtr + g + d;
            ulong* offsets = stackalloc ulong[3];
            offsets[0] = 0;
            offsets[1] = g;
            offsets[2] = g;
            nuint* sizes = stackalloc nuint[3];
            sizes[0] = g;
            sizes[1] = d;
            sizes[2] = d;

            for (int i = 0; i < 3; i++)
            {
                void* v = Kernel.MapViewOfFile3(section, 0, pieces[i], offsets[i], sizes[i], VIRTUAL_ALLOCATION_TYPE.MEM_REPLACE_PLACEHOLDER, PAGE_PROTECTION_FLAGS.PAGE_READWRITE, null, 0);
                int err = Kernel.LastError();
                if (v == pieces[i])
                {
                    isView[i] = true;
                    continue;
                }

                if (v != null)
                {
                    // Cannot happen with MEM_REPLACE_PLACEHOLDER (the view lands exactly on the piece); be defensive.
                    Kernel.UnmapViewOfFile(v);
                    err = (int)WIN32_ERROR.ERROR_INVALID_ADDRESS;
                }

                Unwind(pieces, isView);
                if (err == (int)WIN32_ERROR.ERROR_ACCESS_DENIED && i > 0)
                {
                    throw new RingBufferLayoutException($"The section is smaller than the header claims (data region of {dataBytes} bytes at offset {g}).", err);
                }

                throw Kernel.Fail("MapViewOfFile3", err, i switch { 0 => "header view", 1 => "data view", _ => "mirror view" });
            }

            // 4. a reserved section: commit the control view and the data (the mirror shows the same pages) before anything touches them
            if (commit)
            {
                if (Kernel.VirtualAlloc(basePtr, g, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE) == null
                    || Kernel.VirtualAlloc(basePtr + g, d, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE) == null)
                {
                    int err = Kernel.LastError();
                    Unwind(pieces, isView);                                 // all three are views by now
                    throw Kernel.Fail("VirtualAlloc", err, err is (int)WIN32_ERROR.ERROR_COMMITMENT_LIMIT or (int)WIN32_ERROR.ERROR_NOT_ENOUGH_MEMORY
                        ? $"committing the {dataBytes}-byte data region exceeds the system commit limit"
                        : "commit of the control view and the data");
                }
            }

            var result = new MirroredSection(section, d, basePtr, atRequested);
            if (selfTest)
            {
                try
                {
                    result.SelfTest();
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }

            return result;
        }
        catch
        {
            section.Dispose();
            throw;
        }
    }

    private static void Unwind(byte** pieces, Span<bool> isView)
    {
        for (int i = 0; i < 3; i++)
        {
            if (isView[i])
            {
                Kernel.UnmapViewOfFile(pieces[i]);
            }
            else
            {
                Kernel.VirtualFree(pieces[i], 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            }
        }
    }

    /// <summary>Creator-only: write/verify/restore through both aliases at both ends of the data region.</summary>
    private void SelfTest()
    {
        byte* data = _data;
        byte* mirror = Mirror;
        nuint last = _dataBytes - 1;

        Volatile.Write(ref data[0], 0xA5);
        Volatile.Write(ref data[last], 0x5A);
        bool ok = Volatile.Read(ref mirror[0]) == 0xA5 && Volatile.Read(ref mirror[last]) == 0x5A;
        if (ok)
        {
            Volatile.Write(ref mirror[0], 0x3C);
            Volatile.Write(ref mirror[last], 0xC3);
            ok = Volatile.Read(ref data[0]) == 0x3C && Volatile.Read(ref data[last]) == 0xC3;
        }

        Volatile.Write(ref data[0], 0);
        Volatile.Write(ref data[last], 0);
        if (!ok)
        {
            throw new RingBufferLayoutException("Mirror mismatch: the second data view does not alias the first.");
        }
    }

    private static void ValidateDataBytes(long dataBytes)
    {
        if (dataBytes <= 0 || dataBytes % Layout.HeaderViewBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBytes), dataBytes, "dataBytes must be a positive multiple of 65536.");
        }

        if (dataBytes > Capacity.MaxDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBytes), dataBytes, "dataBytes must not exceed 1 TiB.");
        }
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>
    /// Unmaps the three views (plain <c>UnmapViewOfFile</c> frees a placeholder-backed view's VA outright; verified) and closes the section.
    /// Runs from <see cref="SafeHandle.Dispose()"/> or the critical finalizer; touches no managed state beyond this object's fields.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        bool ok = Kernel.UnmapViewOfFile(_header);
        ok &= Kernel.UnmapViewOfFile(_data);
        ok &= Kernel.UnmapViewOfFile(_data + _dataBytes);
        Interlocked.Exchange(ref _section, null)?.Dispose();
        return ok;
    }
}

/// <summary>
/// A plain view of <c>[offset, offset + bytes)</c> of a section, outside any placeholder: a piece of the tag reserve (DESIGN §16.2), mapped on demand by the
/// writer (read/write) and by readers (read-only). A view of a reserved region may be larger than what is committed; only committed pages may be touched.
/// Releasing it unmaps the view (the section lives on through the buffer's own views and handles). Hold a reference with
/// <see cref="SafeHandle.DangerousAddRef"/> while reading from another thread than the one that may dispose it.
/// </summary>
internal sealed unsafe class SectionView : SafeHandle
{
    private SectionView(void* address, long offset, long bytes)
        : base(invalidHandleValue: 0, ownsHandle: true)
    {
        SetHandle((nint)address);
        Offset = offset;
        Bytes = bytes;
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == 0;

    /// <summary>First byte of the view.</summary>
    public byte* Address => (byte*)handle;

    /// <summary>Section offset of <see cref="Address"/>.</summary>
    public long Offset { get; }

    /// <summary>Size of the view.</summary>
    public long Bytes { get; }

    /// <summary>Maps <c>[offset, offset + bytes)</c> of <paramref name="section"/>.</summary>
    /// <exception cref="RingBufferLayoutException">The section is smaller than the view (a corrupt or foreign tag reserve).</exception>
    public static SectionView Map(SafeSectionHandle section, long offset, long bytes, bool writable)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (offset < 0 || offset % Layout.HeaderViewBytes != 0 || bytes <= 0 || bytes % Layout.HeaderViewBytes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"A section view needs a 64 KiB-aligned offset and size (offset {offset}).");
        }

        void* p = Kernel.MapViewOfFile3(section, 0, null, (ulong)offset, (nuint)bytes, 0, writable ? PAGE_PROTECTION_FLAGS.PAGE_READWRITE : PAGE_PROTECTION_FLAGS.PAGE_READONLY, null, 0);
        int err = Kernel.LastError();
        if (p == null)
        {
            if (err == (int)WIN32_ERROR.ERROR_ACCESS_DENIED)
            {
                throw new RingBufferLayoutException($"The section is smaller than its tag reserve claims (a view of {bytes} bytes at offset {offset}).", err);
            }

            throw Kernel.Fail("MapViewOfFile3", err, $"tag view of {bytes} bytes at offset {offset}");
        }

        return new SectionView(p, offset, bytes);
    }

    /// <summary>
    /// Commits <c>[start, start + bytes)</c> of the view (rounded out to pages). Idempotent. The commit belongs to the section: every view in every process
    /// sees the pages, and they stay committed for the section's life (a view of a section cannot decommit).
    /// </summary>
    /// <exception cref="PhotoneIpcException">The system commit limit is reached.</exception>
    public void Commit(long start, long bytes)
    {
        if (start < 0 || bytes <= 0 || start + bytes > Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"The commit [{start}, {start + bytes}) lies outside the view of {Bytes} bytes.");
        }

        if (Kernel.VirtualAlloc(Address + start, (nuint)bytes, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE) == null)
        {
            int err = Kernel.LastError();
            throw Kernel.Fail("VirtualAlloc", err, err is (int)WIN32_ERROR.ERROR_COMMITMENT_LIMIT or (int)WIN32_ERROR.ERROR_NOT_ENOUGH_MEMORY
                ? $"committing {bytes} bytes of tag memory exceeds the system commit limit"
                : $"commit of {bytes} bytes of tag memory");
        }
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => Kernel.UnmapViewOfFile((void*)handle);
}
