using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Photone.Ipc.Internal;

/// <summary>Shared-memory layout constants (DESIGN §4).</summary>
internal static class Layout
{
    /// <summary>"PHOTONE1" as little-endian u64.</summary>
    public const ulong Magic = 0x31454E4F544F4850;

    /// <summary>
    /// 4: stream tags (<see cref="ControlBlock.TagMode"/>, the tag reserve after the data, <see cref="ControlBlock.TagEnd"/>, the tag snapshot and the
    /// committed sizes of the tag memory; DESIGN §16). 3 was an unreleased first revision of the tags (a fixed tag area before the data).
    /// 2: <see cref="ControlBlock.SectionId"/>, <see cref="LayoutFlag.Pooled"/>, events named from the section id, name aliases (DESIGN §15).
    /// </summary>
    public const uint Version = 4;
    public const uint ControlBytes = 4096;

    /// <summary>Size of the control view (the control block and reserved space); the data follows it, then the tag reserve, if any.</summary>
    public const uint HeaderViewBytes = 65536;

    /// <summary>Section offset of the data region.</summary>
    public const long DataOffset = 65536;
    public const int MaxReaders = 32;
    public const int SlotBase = 512;
    public const int SlotBytes = 64;
    public const int BackendAreaOffset = 2560;
    public const int BackendAreaBytes = 128;
    public const int TagSnapshotOffset = 2688;
    public const int TagRingCommittedOffset = 2752;

    static Layout()
    {
        Verify();
    }

    /// <summary>Touches the static constructor (call once from any factory so a layout bug surfaces early).</summary>
    public static void EnsureInitialized()
    {
    }

    /// <summary>Verifies sizes and every synchronisation-relevant field offset; throws <see cref="InvalidOperationException"/> on mismatch.</summary>
    public static unsafe void Verify()
    {
        if (Unsafe.SizeOf<ControlBlock>() != ControlBytes)
        {
            throw new InvalidOperationException($"ControlBlock size is {Unsafe.SizeOf<ControlBlock>()}, expected {ControlBytes}.");
        }

        if (Unsafe.SizeOf<ReaderSlot>() != SlotBytes)
        {
            throw new InvalidOperationException($"ReaderSlot size is {Unsafe.SizeOf<ReaderSlot>()}, expected {SlotBytes}.");
        }

        ControlBlock cb = default;
        byte* p = (byte*)&cb;
        Check((byte*)&cb.Magic - p, 0, nameof(ControlBlock.Magic));
        Check((byte*)&cb.Version - p, 8, nameof(ControlBlock.Version));
        Check((byte*)&cb.ControlBytes - p, 12, nameof(ControlBlock.ControlBytes));
        Check((byte*)&cb.ElementSize - p, 16, nameof(ControlBlock.ElementSize));
        Check((byte*)&cb.MaxReaders - p, 20, nameof(ControlBlock.MaxReaders));
        Check((byte*)&cb.Capacity - p, 24, nameof(ControlBlock.Capacity));
        Check((byte*)&cb.DataBytes - p, 32, nameof(ControlBlock.DataBytes));
        Check((byte*)&cb.DataOffset - p, 40, nameof(ControlBlock.DataOffset));
        Check((byte*)&cb.TypeHash - p, 48, nameof(ControlBlock.TypeHash));
        Check((byte*)&cb.InitState - p, 52, nameof(ControlBlock.InitState));
        Check((byte*)&cb.SignalBackendId - p, 56, nameof(ControlBlock.SignalBackendId));
        Check((byte*)&cb.LayoutFlags - p, 60, nameof(ControlBlock.LayoutFlags));
        Check((byte*)&cb.CreatorBase - p, 64, nameof(ControlBlock.CreatorBase));
        Check((byte*)&cb.CreatorPid - p, 72, nameof(ControlBlock.CreatorPid));
        Check((byte*)&cb.CreatorStartTime - p, 80, nameof(ControlBlock.CreatorStartTime));
        Check((byte*)&cb.InstanceId - p, 88, nameof(ControlBlock.InstanceId));
        Check((byte*)&cb.ReservationBytes - p, 96, nameof(ControlBlock.ReservationBytes));
        Check((byte*)&cb.SectionId - p, 104, nameof(ControlBlock.SectionId));
        Check((byte*)&cb.TagMode - p, 112, nameof(ControlBlock.TagMode));
        Check((byte*)&cb.TagReserveBytes - p, 120, nameof(ControlBlock.TagReserveBytes));
        Check((byte*)&cb.WriteCursor - p, 128, nameof(ControlBlock.WriteCursor));
        Check((byte*)&cb.TagEnd - p, 136, nameof(ControlBlock.TagEnd));
        Check((byte*)&cb.ReserveEnd - p, 256, nameof(ControlBlock.ReserveEnd));
        Check((byte*)&cb.WriterState - p, 264, nameof(ControlBlock.WriterState));
        Check((byte*)&cb.WriterPid - p, 268, nameof(ControlBlock.WriterPid));
        Check((byte*)&cb.WriterStartTime - p, 272, nameof(ControlBlock.WriterStartTime));
        Check((byte*)&cb.WriterEpoch - p, 280, nameof(ControlBlock.WriterEpoch));
        Check((byte*)&cb.WriterWaiting - p, 384, nameof(ControlBlock.WriterWaiting));
        Check((byte*)&cb.WriterWaiterThreadId - p, 388, nameof(ControlBlock.WriterWaiterThreadId));
        Check((byte*)&cb.WriterWaitFor - p, 392, nameof(ControlBlock.WriterWaitFor));
        Check((byte*)&cb.WriterWaitSinceTick - p, 400, nameof(ControlBlock.WriterWaitSinceTick));
        Check((byte*)&cb.WriterBackendWord - p, 408, nameof(ControlBlock.WriterBackendWord));
        Check((byte*)&cb.WaitersMask - p, 448, nameof(ControlBlock.WaitersMask));
        Check((byte*)&cb.ActiveMask - p, 456, nameof(ControlBlock.ActiveMask));
        Check((byte*)&cb.ReaderGeneration - p, 464, nameof(ControlBlock.ReaderGeneration));
        Check((byte*)&cb.EvictedReaders - p, 468, nameof(ControlBlock.EvictedReaders));
        Check((byte*)&cb.LastEvictedPid - p, 472, nameof(ControlBlock.LastEvictedPid));
        Check((byte*)&cb.Slots - p, SlotBase, nameof(ControlBlock.Slots));
        Check(cb.BackendArea - p, BackendAreaOffset, nameof(ControlBlock.BackendArea));   // fixed buffer: the expression is already the element pointer
        Check((byte*)&cb.TagVersion - p, TagSnapshotOffset, nameof(ControlBlock.TagVersion));
        Check((byte*)&cb.TagSnapshotEnd - p, TagSnapshotOffset + 8, nameof(ControlBlock.TagSnapshotEnd));
        Check((byte*)&cb.TagSnapshotW - p, TagSnapshotOffset + 16, nameof(ControlBlock.TagSnapshotW));
        Check((byte*)&cb.TagStateUsed - p, TagSnapshotOffset + 24, nameof(ControlBlock.TagStateUsed));
        Check((byte*)&cb.TagStateCount - p, TagSnapshotOffset + 28, nameof(ControlBlock.TagStateCount));
        Check((byte*)&cb.TagSnapshotRing - p, TagSnapshotOffset + 32, nameof(ControlBlock.TagSnapshotRing));
        Check((byte*)&cb.TagSnapshotRingStart - p, TagSnapshotOffset + 40, nameof(ControlBlock.TagSnapshotRingStart));
        Check((byte*)&cb.TagTableCommitted - p, TagSnapshotOffset + 48, nameof(ControlBlock.TagTableCommitted));
        Check((byte*)cb.TagRingCommitted - p, TagRingCommittedOffset, nameof(ControlBlock.TagRingCommitted));   // fixed buffer: the expression is already the element pointer

        ReaderSlot rs = default;
        byte* q = (byte*)&rs;
        Check((byte*)&rs.Word - q, 0, nameof(ReaderSlot.Word));
        Check((byte*)&rs.ReadCursor - q, 8, nameof(ReaderSlot.ReadCursor));
        Check((byte*)&rs.WaitFor - q, 16, nameof(ReaderSlot.WaitFor));
        Check((byte*)&rs.ProcessStartTime - q, 24, nameof(ReaderSlot.ProcessStartTime));
        Check((byte*)&rs.ClaimTick - q, 32, nameof(ReaderSlot.ClaimTick));
        Check((byte*)&rs.Flags - q, 40, nameof(ReaderSlot.Flags));
        Check((byte*)&rs.EvictReason - q, 44, nameof(ReaderSlot.EvictReason));
        Check((byte*)&rs.WaiterThreadId - q, 48, nameof(ReaderSlot.WaiterThreadId));
        Check((byte*)&rs.BackendWord - q, 56, nameof(ReaderSlot.BackendWord));

        // An alias shares the fields an opener reads before it knows which kind of section it mapped (WaitForInit, then Magic).
        AliasBlock ab = default;
        byte* a = (byte*)&ab;
        Check((byte*)&ab.Magic - a, 0, nameof(AliasBlock.Magic));
        Check((byte*)&ab.InitState - a, 52, nameof(AliasBlock.InitState));
        Check((byte*)&ab.CreatorPid - a, 72, nameof(AliasBlock.CreatorPid));
        Check((byte*)&ab.CreatorStartTime - a, 80, nameof(AliasBlock.CreatorStartTime));
        Check((byte*)&ab.InstanceId - a, 88, nameof(AliasBlock.InstanceId));
        Check((byte*)ab.TargetName - a, AliasBlock.TargetNameOffset, nameof(AliasBlock.TargetName));
        if (AliasBlock.TargetNameOffset + (2 * AliasBlock.MaxTargetNameChars) > Unsafe.SizeOf<AliasBlock>())
        {
            throw new InvalidOperationException("AliasBlock.TargetName does not fit in the block.");
        }

        static void Check(long actual, long expected, string field)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException($"Layout field {field} is at offset {actual}, expected {expected}.");
            }

            // Every Interlocked/Volatile 64-bit field must be naturally aligned; 128 B for WriteCursor is checked by its literal offset.
            if (expected % 4 != 0)
            {
                throw new InvalidOperationException($"Layout field {field} is not 4-byte aligned.");
            }
        }
    }
}

/// <summary>Bits of <see cref="ControlBlock.LayoutFlags"/>.</summary>
internal static class LayoutFlag
{
    /// <summary>
    /// The section belongs to a <see cref="RingBufferPool"/>: it outlives the buffer and is reused by later buffers of its creator, so it is reached
    /// through an <see cref="AliasBlock"/> (by name) or a duplicated handle, never by its own kernel name (DESIGN §15).
    /// </summary>
    public const uint Pooled = 1;
}

/// <summary>Slot state values held in the low two bits of <see cref="ReaderSlot.Word"/>.</summary>
internal static class SlotState
{
    public const int Free = 0;
    public const int Claimed = 1;
    public const int Active = 2;
    public const int Dead = 3;
}

/// <summary>Packed slot word: <c>pid:32 | seq:30 | state:2</c>, CAS'd as one 64-bit word.</summary>
internal static class SlotWord
{
    public static long Make(int state, uint seq, int pid) => ((long)pid << 32) | ((long)(seq & 0x3FFF_FFFF) << 2) | (uint)state;

    public static int State(long w) => (int)(w & 3);

    public static uint Seq(long w) => (uint)((w >> 2) & 0x3FFF_FFFF);

    public static int Pid(long w) => (int)(w >> 32);
}

/// <summary>Per-reader slot (64 bytes = one cache line). See DESIGN §4.2.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct ReaderSlot
{
    /// <summary><c>pid:32 | seq:30 | state:2</c>; CAS'd as one word by the owner (claim/activate/free) or an evictor.</summary>
    [FieldOffset(0)] public long Word;

    /// <summary>Absolute read cursor of this reader; owner <c>Interlocked.Exchange</c> per Advance.</summary>
    [FieldOffset(8)] public long ReadCursor;

    /// <summary>Absolute write cursor the reader waits for; <see cref="long.MaxValue"/> when idle.</summary>
    [FieldOffset(16)] public long WaitFor;

    /// <summary>Owner process creation time (FILETIME); PID-reuse guard.</summary>
    [FieldOffset(24)] public long ProcessStartTime;

    /// <summary><c>GetTickCount64</c> at claim; stuck-claim reclaim.</summary>
    [FieldOffset(32)] public ulong ClaimTick;

    /// <summary>bit0 = MappedAtCreatorAddress; bit1 reserved (RewindRequested).</summary>
    [FieldOffset(40)] public int Flags;

    /// <summary>0 none, 1 Dead, 2 StuckClaim, 3 Lag (reserved).</summary>
    [FieldOffset(44)] public int EvictReason;

    /// <summary>Backend word (0 for the named-event backend).</summary>
    [FieldOffset(48)] public int WaiterThreadId;

    /// <summary>Backend word (wake token / futex word).</summary>
    [FieldOffset(56)] public long BackendWord;
}

/// <summary>Control block at section offset 0 (4096 bytes). See DESIGN §4.1 for line ownership.</summary>
[StructLayout(LayoutKind.Explicit, Size = 4096)]
internal unsafe struct ControlBlock
{
    // ---- line 0: identity (creator once) ----
    [FieldOffset(0)] public ulong Magic;
    [FieldOffset(8)] public uint Version;
    [FieldOffset(12)] public uint ControlBytes;
    [FieldOffset(16)] public uint ElementSize;
    [FieldOffset(20)] public uint MaxReaders;
    [FieldOffset(24)] public long Capacity;
    [FieldOffset(32)] public long DataBytes;
    [FieldOffset(40)] public long DataOffset;
    [FieldOffset(48)] public uint TypeHash;
    /// <summary>0 initialising, 1 ready; <c>Volatile.Write</c> last by the creator.</summary>
    [FieldOffset(52)] public int InitState;
    [FieldOffset(56)] public uint SignalBackendId;
    /// <summary><see cref="LayoutFlag"/> bits.</summary>
    [FieldOffset(60)] public uint LayoutFlags;

    // ---- line 1: creator identity ----
    [FieldOffset(64)] public ulong CreatorBase;
    /// <summary>Written second (after <see cref="CreatorStartTime"/>), with <c>Volatile.Write</c>.</summary>
    [FieldOffset(72)] public int CreatorPid;
    /// <summary>Written first.</summary>
    [FieldOffset(80)] public long CreatorStartTime;
    /// <summary>Random per buffer: a pooled section gets a new one every time it is reused.</summary>
    [FieldOffset(88)] public ulong InstanceId;
    [FieldOffset(96)] public ulong ReservationBytes;
    /// <summary>Random per section, constant for its whole life (also across pooled reuse); names the signaling objects.</summary>
    [FieldOffset(104)] public ulong SectionId;
    /// <summary><see cref="Photone.Ipc.TagMode"/> of the buffer (DESIGN §16).</summary>
    [FieldOffset(112)] public int TagMode;
    /// <summary>Size of the tag reserve at section offset <c>64 KiB + DataBytes</c>: <see cref="TagFormat.ReserveBytes"/> with cross-process tags, otherwise 0.</summary>
    [FieldOffset(120)] public long TagReserveBytes;

    // ---- line 2: the message (writer Interlocked.Exchange per Commit; readers poll). Line 3 (192..255) is deliberately empty. ----
    [FieldOffset(128)] public long WriteCursor;
    /// <summary>Absolute log position (bytes) after the last published tag record; stored before <see cref="WriteCursor"/> by a commit that carries tags (cross-process tags).</summary>
    [FieldOffset(136)] public long TagEnd;

    // ---- line 4: writer state (line 5 (320..383) is deliberately empty) ----
    [FieldOffset(256)] public long ReserveEnd;
    /// <summary>0 None, 1 Active, 2 Closed.</summary>
    [FieldOffset(264)] public int WriterState;
    [FieldOffset(268)] public int WriterPid;
    [FieldOffset(272)] public long WriterStartTime;
    [FieldOffset(280)] public uint WriterEpoch;

    // ---- line 6: writer blocking handshake ----
    [FieldOffset(384)] public int WriterWaiting;
    [FieldOffset(388)] public int WriterWaiterThreadId;
    [FieldOffset(392)] public long WriterWaitFor;
    [FieldOffset(400)] public ulong WriterWaitSinceTick;
    [FieldOffset(408)] public long WriterBackendWord;

    // ---- line 7: reader masks ----
    [FieldOffset(448)] public ulong WaitersMask;
    [FieldOffset(456)] public ulong ActiveMask;
    [FieldOffset(464)] public uint ReaderGeneration;
    [FieldOffset(468)] public uint EvictedReaders;
    [FieldOffset(472)] public int LastEvictedPid;

    // ---- lines 8..39: reader slots ----
    [FieldOffset(512)] public ReaderSlotArray Slots;

    // ---- lines 40..41: backend-private area ----
    [FieldOffset(2560)] public fixed byte BackendArea[128];

    // ---- line 42: tag snapshot for joining readers (seqlock; stored after WriteCursor by a commit that carries tags, DESIGN §16.6) ----
    /// <summary>Even when stable; the writer makes it odd before it changes the snapshot or the persistent-tag table and even again afterwards.</summary>
    [FieldOffset(2688)] public ulong TagVersion;
    /// <summary>Log position up to which the persistent-tag table includes every persistent record.</summary>
    [FieldOffset(2696)] public long TagSnapshotEnd;
    /// <summary>The write cursor published together with <see cref="TagSnapshotEnd"/>: every record before it has an offset below this.</summary>
    [FieldOffset(2704)] public long TagSnapshotW;
    /// <summary>Bytes of the persistent-tag table in use.</summary>
    [FieldOffset(2712)] public int TagStateUsed;
    /// <summary>Records in the persistent-tag table (one per key).</summary>
    [FieldOffset(2716)] public int TagStateCount;
    /// <summary>Size class of the ring that holds log position <see cref="TagSnapshotEnd"/>.</summary>
    [FieldOffset(2720)] public int TagSnapshotRing;
    /// <summary>Log position at which that ring's generation starts (its physical offset 0).</summary>
    [FieldOffset(2728)] public long TagSnapshotRingStart;
    /// <summary>Committed bytes of the persistent-tag table region (only grows; stored after the commit, before the table uses them).</summary>
    [FieldOffset(2736)] public long TagTableCommitted;

    // ---- lines 43..45: committed bytes of every ring size class (only grow; stored after the commit, before any record lies there) ----
    [FieldOffset(2752)] public fixed long TagRingCommitted[TagFormat.RingClasses];

    /// <summary>Returns a reference to slot <paramref name="i"/> (0..31) of the control block at <paramref name="hdr"/>.</summary>
    public static ref ReaderSlot SlotRef(ControlBlock* hdr, int i)
        => ref Unsafe.AsRef<ReaderSlot>((byte*)hdr + Layout.SlotBase + Layout.SlotBytes * i);
}

/// <summary>32 consecutive <see cref="ReaderSlot"/>s (2048 bytes).</summary>
[InlineArray(Layout.MaxReaders)]
internal struct ReaderSlotArray
{
    private ReaderSlot _element0;
}

/// <summary>
/// Offset 0 of the 64 KiB named section that carries the name of a pooled buffer (DESIGN §15). The ring itself lives in a pool-owned section that
/// keeps its kernel name across reuse; the alias ties the user's name to one incarnation (<see cref="InstanceId"/>) of that section.
/// <see cref="InitState"/>, <see cref="CreatorPid"/>, <see cref="CreatorStartTime"/> and <see cref="InstanceId"/> sit at the same offsets as in
/// <see cref="ControlBlock"/>, so an opener waits for either kind of section the same way before looking at <see cref="Magic"/>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 1024)]
internal unsafe struct AliasBlock
{
    /// <summary>"PHOTLINK" as little-endian u64.</summary>
    public const ulong MagicValue = 0x4B4E494C544F4850;
    public const uint CurrentVersion = 1;
    public const int TargetNameOffset = 128;
    public const int MaxTargetNameChars = 256;

    [FieldOffset(0)] public ulong Magic;
    [FieldOffset(8)] public uint Version;
    /// <summary>UTF-16 code units in <see cref="TargetName"/>.</summary>
    [FieldOffset(12)] public int TargetNameLength;
    /// <summary><see cref="ControlBlock.SectionId"/> of the target.</summary>
    [FieldOffset(16)] public ulong SectionId;
    /// <summary><see cref="ControlBlock.DataBytes"/> of the target.</summary>
    [FieldOffset(24)] public long DataBytes;
    /// <summary>0 while the creator is writing, 1 once the record and the target are complete.</summary>
    [FieldOffset(52)] public int InitState;
    [FieldOffset(72)] public int CreatorPid;
    [FieldOffset(80)] public long CreatorStartTime;
    /// <summary>The <see cref="ControlBlock.InstanceId"/> the name refers to; any other value in the target means the buffer is gone.</summary>
    [FieldOffset(88)] public ulong InstanceId;
    /// <summary>Kernel name of the pooled section (<c>Local\photone.pool.{guid}</c> or <c>Global\...</c>).</summary>
    [FieldOffset(TargetNameOffset)] public fixed char TargetName[MaxTargetNameChars];
}
