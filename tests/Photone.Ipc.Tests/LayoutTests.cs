using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

public sealed class LayoutTests
{
    [Fact]
    public void Layout_SizesAndOffsets()
    {
        Assert.Equal(4096, Unsafe.SizeOf<ControlBlock>());
        Assert.Equal(64, Unsafe.SizeOf<ReaderSlot>());
        Assert.Equal(2048, Unsafe.SizeOf<ReaderSlotArray>());
        Layout.Verify();   // throws on any offset mismatch

        var expected = new Dictionary<string, int>
        {
            [nameof(ControlBlock.Magic)] = 0,
            [nameof(ControlBlock.Version)] = 8,
            [nameof(ControlBlock.ControlBytes)] = 12,
            [nameof(ControlBlock.ElementSize)] = 16,
            [nameof(ControlBlock.MaxReaders)] = 20,
            [nameof(ControlBlock.Capacity)] = 24,
            [nameof(ControlBlock.DataBytes)] = 32,
            [nameof(ControlBlock.DataOffset)] = 40,
            [nameof(ControlBlock.TypeHash)] = 48,
            [nameof(ControlBlock.InitState)] = 52,
            [nameof(ControlBlock.SignalBackendId)] = 56,
            [nameof(ControlBlock.LayoutFlags)] = 60,
            [nameof(ControlBlock.CreatorBase)] = 64,
            [nameof(ControlBlock.CreatorPid)] = 72,
            [nameof(ControlBlock.CreatorStartTime)] = 80,
            [nameof(ControlBlock.InstanceId)] = 88,
            [nameof(ControlBlock.ReservationBytes)] = 96,
            [nameof(ControlBlock.SectionId)] = 104,
            [nameof(ControlBlock.TagLogBytes)] = 112,
            [nameof(ControlBlock.TagStateBytes)] = 120,
            [nameof(ControlBlock.WriteCursor)] = 128,
            [nameof(ControlBlock.TagEnd)] = 136,
            [nameof(ControlBlock.ReserveEnd)] = 256,
            [nameof(ControlBlock.WriterState)] = 264,
            [nameof(ControlBlock.WriterPid)] = 268,
            [nameof(ControlBlock.WriterStartTime)] = 272,
            [nameof(ControlBlock.WriterEpoch)] = 280,
            [nameof(ControlBlock.WriterWaiting)] = 384,
            [nameof(ControlBlock.WriterWaiterThreadId)] = 388,
            [nameof(ControlBlock.WriterWaitFor)] = 392,
            [nameof(ControlBlock.WriterWaitSinceTick)] = 400,
            [nameof(ControlBlock.WriterBackendWord)] = 408,
            [nameof(ControlBlock.WaitersMask)] = 448,
            [nameof(ControlBlock.ActiveMask)] = 456,
            [nameof(ControlBlock.ReaderGeneration)] = 464,
            [nameof(ControlBlock.EvictedReaders)] = 468,
            [nameof(ControlBlock.LastEvictedPid)] = 472,
            [nameof(ControlBlock.Slots)] = 512,
            [nameof(ControlBlock.BackendArea)] = 2560,
            [nameof(ControlBlock.TagVersion)] = 2688,
            [nameof(ControlBlock.TagSnapshotEnd)] = 2696,
            [nameof(ControlBlock.TagSnapshotW)] = 2704,
            [nameof(ControlBlock.TagStateUsed)] = 2712,
            [nameof(ControlBlock.TagStateCount)] = 2716,
        };

        foreach ((string name, int offset) in expected)
        {
            FieldInfo f = typeof(ControlBlock).GetField(name, BindingFlags.Public | BindingFlags.Instance)!;
            Assert.NotNull(f);
            Assert.Equal(offset, f.GetCustomAttribute<FieldOffsetAttribute>()!.Value);
        }

        // Every 64-bit field used with Interlocked/Volatile is naturally aligned; the write cursor owns a 128 B-aligned line.
        Assert.Equal(0, expected[nameof(ControlBlock.WriteCursor)] % 128);
        foreach (FieldInfo f in typeof(ControlBlock).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            int off = f.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            int size = FieldSize(f);
            if (size >= 8)
            {
                Assert.Equal(0, off % 8);
            }
        }
    }

    [Fact]
    public void Layout_Lines3And5_AreEmpty()
    {
        // Lines 3 (192..255) and 5 (320..383) are the adjacent-line-prefetch partners of the writer-stored lines 2 and 4.
        foreach (FieldInfo f in typeof(ControlBlock).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            int start = f.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            int end = start + FieldSize(f);
            Assert.False(Overlaps(start, end, 192, 256), $"{f.Name} overlaps line 3");
            Assert.False(Overlaps(start, end, 320, 384), $"{f.Name} overlaps line 5");
            // and nothing else shares the write cursor's line, except TagEnd: stored by the same writer right before WriteCursor (on commits with tags)
            // and loaded by readers right after it, so it adds no traffic of its own to the line (DESIGN §16.3)
            if (f.Name is not nameof(ControlBlock.WriteCursor) and not nameof(ControlBlock.TagEnd))
            {
                Assert.False(Overlaps(start, end, 128, 192), $"{f.Name} shares line 2 with WriteCursor");
            }
        }
    }

    [Fact]
    public void ReaderSlot_Offsets()
    {
        Assert.Equal(0, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.Word)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.ReadCursor)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.WaitFor)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.ProcessStartTime)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.ClaimTick)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.Flags)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.EvictReason)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.WaiterThreadId)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<ReaderSlot>(nameof(ReaderSlot.BackendWord)).ToInt32());
    }

    [Fact]
    public unsafe void SlotRef_AddressesEachLine()
    {
        ControlBlock cb = default;
        ControlBlock* p = &cb;
        for (int i = 0; i < Layout.MaxReaders; i++)
        {
            ref ReaderSlot s = ref ControlBlock.SlotRef(p, i);
            long off = (byte*)Unsafe.AsPointer(ref s) - (byte*)p;
            Assert.Equal(Layout.SlotBase + Layout.SlotBytes * i, off);
        }
    }

    [Theory]
    [InlineData(SlotState.Free, 0u, 0)]
    [InlineData(SlotState.Claimed, 1u, 1234)]
    [InlineData(SlotState.Active, 0x3FFF_FFFFu, int.MaxValue)]
    [InlineData(SlotState.Dead, 0x2ABC_DEF0u, 42)]
    public void SlotWord_RoundTrips(int state, uint seq, int pid)
    {
        long w = SlotWord.Make(state, seq, pid);
        Assert.Equal(state, SlotWord.State(w));
        Assert.Equal(seq, SlotWord.Seq(w));
        Assert.Equal(pid, SlotWord.Pid(w));
    }

    [Fact]
    public void SlotWord_SeqIsMasked()
    {
        long w = SlotWord.Make(SlotState.Active, 0xFFFF_FFFFu, 7);
        Assert.Equal(0x3FFF_FFFFu, SlotWord.Seq(w));
        Assert.Equal(SlotState.Active, SlotWord.State(w));
        Assert.Equal(7, SlotWord.Pid(w));
    }

    [Fact]
    public void Magic_IsPhotone1LittleEndian()
    {
        Span<byte> bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(bytes, Layout.Magic);
        Assert.Equal("PHOTONE1", System.Text.Encoding.ASCII.GetString(bytes));
        BitConverter.TryWriteBytes(bytes, AliasBlock.MagicValue);
        Assert.Equal("PHOTLINK", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void AliasBlock_SharesTheFieldsAnOpenerReadsFirst()
    {
        // WaitForInit and the magic dispatch read these through a ControlBlock pointer, whichever kind of section was mapped (DESIGN §15.4)
        foreach (string field in new[] { nameof(AliasBlock.Magic), nameof(AliasBlock.InitState), nameof(AliasBlock.CreatorPid), nameof(AliasBlock.CreatorStartTime), nameof(AliasBlock.InstanceId) })
        {
            int alias = typeof(AliasBlock).GetField(field)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            int control = typeof(ControlBlock).GetField(field)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            Assert.Equal(control, alias);
        }

        Assert.Equal(AliasBlock.TargetNameOffset, typeof(AliasBlock).GetField(nameof(AliasBlock.TargetName))!.GetCustomAttribute<FieldOffsetAttribute>()!.Value);
        Assert.True(AliasBlock.TargetNameOffset + (2 * AliasBlock.MaxTargetNameChars) <= Unsafe.SizeOf<AliasBlock>());
        Assert.True(Unsafe.SizeOf<AliasBlock>() <= Layout.ControlBytes);
        Assert.True(MirroredSection.NewPoolSectionName(global: true).Length <= AliasBlock.MaxTargetNameChars);
    }

    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd) => aStart < bEnd && bStart < aEnd;

    private static int FieldSize(FieldInfo f)
    {
        FixedBufferAttribute? fixedBuffer = f.GetCustomAttribute<FixedBufferAttribute>();
        if (fixedBuffer is not null)
        {
            return fixedBuffer.Length * Marshal.SizeOf(fixedBuffer.ElementType);
        }

        if (f.FieldType == typeof(ReaderSlotArray))
        {
            return Unsafe.SizeOf<ReaderSlotArray>();
        }

        return Marshal.SizeOf(f.FieldType);
    }
}
