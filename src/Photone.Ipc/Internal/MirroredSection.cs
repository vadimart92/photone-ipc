using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>
/// One placeholder of <c>G + 2D</c> bytes carrying three views of one pagefile-backed section:
/// <c>[Header (section 0..G)][Data (section G..G+D)][Mirror (section G..G+D again)]</c>, so <c>Mirror == Data + D</c>
/// and a span starting anywhere in <c>Data</c> may run up to <c>D</c> bytes past its end without copying.
/// This is the ONLY code in the library that maps or unmaps memory (DESIGN §3.2–§3.5).
/// The handle is the placeholder base; releasing it unmaps the three views (which frees the VA outright, verified) and closes the section.
/// </summary>
internal sealed unsafe class MirroredSection : SafeHandle
{
    private const string LocalPrefix = "Local\\";
    private const string GlobalPrefix = "Global\\";
    private const string DefaultNamePrefix = "Local\\photone.";

    private readonly SafeSectionHandle _section;
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

    /// <summary>The section handle; owned by this object and closed on release.</summary>
    public SafeSectionHandle Section => _section;

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

    // ------------------------------------------------------------------ sections

    /// <summary>
    /// Creates a new pagefile-backed section of <c>G + dataBytes</c> bytes (<c>PAGE_READWRITE | SEC_COMMIT</c>).
    /// </summary>
    /// <param name="dataBytes">Data region size; a positive multiple of 64 KiB.</param>
    /// <param name="sectionName">Already-normalised object name, or <see langword="null"/> for an anonymous section.</param>
    /// <exception cref="RingBufferAlreadyExistsException">An object with that name already exists.</exception>
    public static SafeSectionHandle CreateSection(long dataBytes, string? sectionName)
    {
        Kernel.EnsurePlatform();
        ValidateDataBytes(dataBytes);
        ulong size = Layout.HeaderViewBytes + (ulong)dataBytes;
        SafeSectionHandle section = Kernel.CreateFileMapping(Kernel.INVALID_HANDLE_VALUE, null, Kernel.PAGE_READWRITE, (uint)(size >> 32), (uint)size, sectionName);
        int err = Kernel.LastError();                                  // read even on success (183 = opened existing)
        if (section.IsInvalid)
        {
            section.Dispose();
            string? detail = err switch
            {
                Kernel.ERROR_ACCESS_DENIED when sectionName is not null && sectionName.StartsWith(GlobalPrefix, StringComparison.Ordinal)
                    => "creating a Global\\ section requires SeCreateGlobalPrivilege",
                Kernel.ERROR_COMMITMENT_LIMIT or Kernel.ERROR_NOT_ENOUGH_MEMORY
                    => $"the section ({size} bytes) exceeds the system commit limit",
                _ => null,
            };
            throw Kernel.Fail("CreateFileMappingW", err, detail);
        }

        if (err == Kernel.ERROR_ALREADY_EXISTS)
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
        SafeSectionHandle section = Kernel.OpenFileMapping(Kernel.FILE_MAP_READ | Kernel.FILE_MAP_WRITE, false, sectionName);
        int err = Kernel.LastError();
        if (section.IsInvalid)
        {
            section.Dispose();
            if (err == Kernel.ERROR_FILE_NOT_FOUND)
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
    /// </summary>
    /// <exception cref="RingBufferLayoutException">The section is smaller than 64 KiB.</exception>
    public static byte* MapHeaderPeek(SafeSectionHandle section)
    {
        ArgumentNullException.ThrowIfNull(section);
        void* p = Kernel.MapViewOfFile3(section, 0, null, 0, Layout.HeaderViewBytes, 0, Kernel.PAGE_READWRITE, null, 0);
        int err = Kernel.LastError();
        if (p == null)
        {
            if (err == Kernel.ERROR_ACCESS_DENIED)
            {
                throw new RingBufferLayoutException("The section is smaller than the 64 KiB header (or the handle lacks write access).", err);
            }

            throw Kernel.Fail("MapViewOfFile3", err, "header peek");
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
    /// <exception cref="RingBufferLayoutException">The mirror does not alias the data region.</exception>
    public static MirroredSection Create(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates)
        => Map(section, dataBytes, candidates, selfTest: true);

    /// <summary>
    /// Opener side: reserves the placeholder (trying <paramref name="candidates"/> — normally the creator's base — then a system-chosen address)
    /// and maps the three views. No content check: a live writer may be storing into <c>data[0]</c>. Placement (<c>Mirror == Data + D</c>) is asserted.
    /// </summary>
    /// <param name="section">Section of at least <c>G + dataBytes</c> bytes; ownership transfers to the result (also on failure).</param>
    /// <param name="dataBytes">Data region size; a positive multiple of 64 KiB.</param>
    /// <param name="candidates">64 KiB-aligned base addresses to try first; may be empty.</param>
    /// <exception cref="RingBufferLayoutException">The section is smaller than the header claims.</exception>
    public static MirroredSection Open(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates)
        => Map(section, dataBytes, candidates, selfTest: false);

    private static MirroredSection Map(SafeSectionHandle section, long dataBytes, ReadOnlySpan<ulong> candidates, bool selfTest)
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
                basePtr = (byte*)Kernel.VirtualAlloc2(0, (void*)c, total, Kernel.MEM_RESERVE | Kernel.MEM_RESERVE_PLACEHOLDER, Kernel.PAGE_NOACCESS, null, 0);
                if (basePtr != null)
                {
                    atRequested = true;
                    break;
                }
            }

            if (basePtr == null)
            {
                basePtr = (byte*)Kernel.VirtualAlloc2(0, null, total, Kernel.MEM_RESERVE | Kernel.MEM_RESERVE_PLACEHOLDER, Kernel.PAGE_NOACCESS, null, 0);
                if (basePtr == null)
                {
                    throw Kernel.Fail("VirtualAlloc2", Kernel.LastError(), $"placeholder of {total} bytes");
                }
            }

            // 2. split into [base, base+G) [base+G, base+G+D) [base+G+D, base+G+2D)
            if (!Kernel.VirtualFree(basePtr, g, Kernel.MEM_RELEASE | Kernel.MEM_PRESERVE_PLACEHOLDER))
            {
                int err = Kernel.LastError();
                Kernel.VirtualFree(basePtr, 0, Kernel.MEM_RELEASE);                    // unsplit: one call frees everything
                throw Kernel.Fail("VirtualFree", err, "split 1 (MEM_PRESERVE_PLACEHOLDER)");
            }

            if (!Kernel.VirtualFree(basePtr + g, d, Kernel.MEM_RELEASE | Kernel.MEM_PRESERVE_PLACEHOLDER))
            {
                int err = Kernel.LastError();
                Kernel.VirtualFree(basePtr, 0, Kernel.MEM_RELEASE);
                Kernel.VirtualFree(basePtr + g, 0, Kernel.MEM_RELEASE);
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
                void* v = Kernel.MapViewOfFile3(section, 0, pieces[i], offsets[i], sizes[i], Kernel.MEM_REPLACE_PLACEHOLDER, Kernel.PAGE_READWRITE, null, 0);
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
                    err = Kernel.ERROR_INVALID_ADDRESS;
                }

                Unwind(pieces, isView);
                if (err == Kernel.ERROR_ACCESS_DENIED && i > 0)
                {
                    throw new RingBufferLayoutException($"The section is smaller than the header claims (data region of {dataBytes} bytes at offset {g}).", err);
                }

                throw Kernel.Fail("MapViewOfFile3", err, i switch { 0 => "header view", 1 => "data view", _ => "mirror view" });
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
                Kernel.VirtualFree(pieces[i], 0, Kernel.MEM_RELEASE);
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
        _section.Dispose();
        return ok;
    }
}
