using System.Diagnostics;
using System.Runtime.CompilerServices;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

public sealed unsafe class MappingTests
{
    private const long D = 2 * 65536;   // small data region: 128 KiB

    private static ulong[] Hints(long dataBytes, ulong instanceId = 0x1234_5678_9ABC_DEF0)
    {
        Span<ulong> tmp = stackalloc ulong[AddressHint.MaxProbes];
        int n = AddressHint.Candidates(instanceId, (ulong)(Layout.HeaderViewBytes + 2 * dataBytes), null, tmp);
        return tmp[..n].ToArray();
    }

    private static MirroredSection CreateRegion(string name, long dataBytes = D, ulong[]? candidates = null)
    {
        SafeSectionHandle section = MirroredSection.CreateSection(dataBytes, name);
        return MirroredSection.Create(section, dataBytes, candidates ?? Hints(dataBytes));
    }

    // ---------------------------------------------------------------- mirroring

    [Fact]
    public void Create_MirrorAliases()
    {
        using MirroredSection r = CreateRegion(TestNames.UniqueSection());
        Assert.False(r.IsInvalid);
        Assert.Equal((ulong)r.Header, r.BaseAddress);
        Assert.True(r.Data == r.Header + 65536);
        Assert.True(r.Mirror == r.Data + (nuint)D);
        Assert.Equal((nuint)D, r.DataBytes);
        Assert.Equal((nuint)(65536 + 2 * D), r.ReservationBytes);

        // write via Data, read via Mirror at several positions
        for (long i = 0; i < D; i += 4097)
        {
            r.Data[i] = (byte)(i * 7);
            Assert.Equal((byte)(i * 7), r.Mirror[i]);
        }

        // write via Mirror, read via Data
        r.Mirror[D - 1] = 0xEE;
        Assert.Equal(0xEE, r.Data[D - 1]);

        // wrap-around span: fill D-8 .. D+8 through one contiguous span, then check it landed at the end and at the start of Data
        var wrap = new Span<int>(r.Data + D - 8, 4);
        wrap[0] = 11;
        wrap[1] = 22;
        wrap[2] = 33;
        wrap[3] = 44;
        Assert.Equal(11, *(int*)(r.Data + D - 8));
        Assert.Equal(22, *(int*)(r.Data + D - 4));
        Assert.Equal(33, *(int*)r.Data);
        Assert.Equal(44, *(int*)(r.Data + 4));
        Assert.Equal(33, *(int*)r.Mirror);

        // the header view is a distinct part of the section: writing it never touches data
        r.Header[0] = 0x7F;
        Assert.Equal(0x7F, r.Header[0]);
        Assert.NotEqual(0x7F, r.Data[0]);
    }

    [Fact]
    public void Create_SelfTest_LeavesDataZero()
    {
        using MirroredSection r = CreateRegion(TestNames.UniqueSection());
        Assert.Equal(0, r.Data[0]);
        Assert.Equal(0, r.Data[D - 1]);
        Assert.Equal(0, r.Mirror[0]);
    }

    [Fact]
    public void Create_LargeRegion_64MiB()
    {
        const long big = 64L << 20;
        using MirroredSection r = CreateRegion(TestNames.UniqueSection(), big);
        r.Data[big - 1] = 5;
        Assert.Equal(5, r.Mirror[big - 1]);
        var span = new Span<byte>(r.Data + big - 3, 6);
        span.Fill(9);
        Assert.Equal(9, r.Data[2]);
        Assert.Equal(9, r.Data[big - 3]);
    }

    // ---------------------------------------------------------------- address hint

    [Fact]
    public void AddressHint_DeterministicAndAligned()
    {
        ulong[] a = Hints(D, 42);
        ulong[] b = Hints(D, 42);
        Assert.Equal(AddressHint.MaxProbes, a.Length);
        Assert.Equal(a, b);
        foreach (ulong p in a)
        {
            Assert.Equal(0UL, p % 65536);
            Assert.InRange(p, AddressHint.WindowStart, AddressHint.WindowStart + AddressHint.WindowBytes - 1);
        }

        Assert.NotEqual(a, Hints(D, 43));

        Span<ulong> tmp = stackalloc ulong[AddressHint.MaxProbes];
        Assert.Equal(0, AddressHint.Candidates(1, 65536 * 3, 0, tmp));
        Assert.Equal(1, AddressHint.Candidates(1, 65536 * 3, 0x4100_0000_0000, tmp));
        Assert.Equal(0x4100_0000_0000UL, tmp[0]);
        Assert.Throws<ArgumentException>(() =>
        {
            Span<ulong> t = stackalloc ulong[AddressHint.MaxProbes];
            AddressHint.Candidates(1, 65536 * 3, 0x4100_0000_1000, t);
        });
    }

    [Fact]
    public void Create_UsesHintedAddress()
    {
        ulong[] hints = Hints(D, 0xABCDEF);
        using MirroredSection r = CreateRegion(TestNames.UniqueSection(), D, hints);
        Assert.True(r.AtRequestedAddress);
        Assert.Contains(r.BaseAddress, hints);
    }

    [Fact]
    public void Create_ExplicitPreferredBase()
    {
        const ulong preferred = 0x4800_1234_0000;
        using MirroredSection r = CreateRegion(TestNames.UniqueSection(), D, [preferred]);
        Assert.True(r.AtRequestedAddress);
        Assert.Equal(preferred, r.BaseAddress);
    }

    [Fact]
    public void Create_AllCandidatesOccupied_FallsBack()
    {
        const ulong occupied = 0x4900_0000_0000;
        void* blocker = TestKernel.VirtualAlloc((void*)occupied, 65536, TestKernel.MEM_RESERVE, TestKernel.PAGE_NOACCESS);
        Assert.True(blocker != null);
        try
        {
            using MirroredSection r = CreateRegion(TestNames.UniqueSection(), D, [occupied]);
            Assert.False(r.AtRequestedAddress);
            Assert.NotEqual(occupied, r.BaseAddress);
            r.Data[D - 1] = 3;
            Assert.Equal(3, r.Mirror[D - 1]);
        }
        finally
        {
            TestKernel.VirtualFree(blocker, 0, TestKernel.MEM_RELEASE);
        }
    }

    // ---------------------------------------------------------------- open (same process)

    [Fact]
    public void Open_SameProcess_FallsBackToAnotherAddress_AndSharesData()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection creator = CreateRegion(name);
        creator.Data[100] = 0x42;

        SafeSectionHandle section = MirroredSection.OpenSection(name);
        using MirroredSection opener = MirroredSection.Open(section, D, [creator.BaseAddress]);
        Assert.False(opener.AtRequestedAddress);           // the creator occupies that range in this process
        Assert.NotEqual(creator.BaseAddress, opener.BaseAddress);
        Assert.True(opener.Mirror == opener.Data + (nuint)D);

        Assert.Equal(0x42, opener.Data[100]);
        Assert.Equal(0x42, opener.Mirror[100]);
        opener.Mirror[D - 1] = 0x24;
        Assert.Equal(0x24, creator.Data[D - 1]);
        creator.Header[4095] = 9;
        Assert.Equal(9, opener.Header[4095]);
    }

    [Fact]
    public void Open_FromSecondThread_SharesData()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection creator = CreateRegion(name);
        var span = new Span<byte>(creator.Data + D - 4, 8);
        for (int i = 0; i < 8; i++)
        {
            span[i] = (byte)(i + 1);
        }

        ulong creatorBase = creator.BaseAddress;
        Exception? failure = null;
        bool atCreator = true;
        var t = new Thread(() =>
        {
            try
            {
                SafeSectionHandle section = MirroredSection.OpenSection(name);
                using MirroredSection opener = MirroredSection.Open(section, D, [creatorBase]);
                atCreator = opener.AtRequestedAddress;
                for (int i = 0; i < 8; i++)
                {
                    if (opener.Mirror[i - 4] != (byte)(i + 1))
                    {
                        throw new InvalidOperationException($"mismatch at {i}");
                    }
                }

                opener.Data[7] = 0x77;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        t.Start();
        t.Join();
        Assert.Null(failure);
        Assert.False(atCreator);
        Assert.Equal(0x77, creator.Mirror[7]);
    }

    [Fact]
    public void Open_ByHandle_NoName()
    {
        SafeSectionHandle anonymous = MirroredSection.CreateSection(D, null);
        using MirroredSection creator = MirroredSection.Create(anonymous, D, Hints(D));
        creator.Data[1] = 1;
        // "open by handle": a second mapping of the same handle (the region owns it; a real peer would DuplicateHandle first)
        SafeSectionHandle dup = new(creator.Section.DangerousGetHandle(), ownsHandle: false);
        using MirroredSection opener = MirroredSection.Open(dup, D, []);
        Assert.Equal(1, opener.Mirror[1]);
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public void Create_ThenCreateSameName_ThrowsAlreadyExists()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection first = CreateRegion(name);
        var ex = Assert.Throws<RingBufferAlreadyExistsException>(() => MirroredSection.CreateSection(D, name));
        Assert.Equal(183, ex.NativeErrorCode);
    }

    [Fact]
    public void Open_MissingName_ThrowsNotFound()
    {
        var ex = Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(TestNames.UniqueSection()));
        Assert.Equal(2, ex.NativeErrorCode);
    }

    [Fact]
    public void Open_BadName_Throws()
    {
        Assert.Throws<ArgumentException>(() => MirroredSection.OpenSection(string.Empty));
        // a nested object path that cannot exist
        var ex = Assert.ThrowsAny<PhotoneIpcException>(() => MirroredSection.OpenSection("Local\\photone.no\\such\\dir\\" + TestNames.Unique()));
        Assert.NotEqual(0, ex.NativeErrorCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("demo", false)]
    [InlineData("Local\\x", false)]
    [InlineData("Global\\x", false)]
    public void Name_Normalization(string? name, bool isGuid)
    {
        string n = MirroredSection.NormalizeName(name);
        if (isGuid)
        {
            Assert.StartsWith("Local\\photone.", n, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(n["Local\\photone.".Length..], "N", out _));
        }
        else if (name!.StartsWith("Local\\", StringComparison.Ordinal) || name.StartsWith("Global\\", StringComparison.Ordinal))
        {
            Assert.Equal(name, n);
        }
        else
        {
            Assert.Equal("Local\\photone." + name, n);
        }

        Assert.Throws<ArgumentException>(() => MirroredSection.NormalizeName(string.Empty));
        Assert.Throws<ArgumentException>(() => MirroredSection.NormalizeName("a\\b"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-65536)]
    [InlineData(4096)]
    [InlineData(65535)]
    [InlineData(65537)]
    [InlineData(100_000)]
    public void Size_NotMultipleOfGranularity_Throws(long dataBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MirroredSection.CreateSection(dataBytes, TestNames.UniqueSection()));

        SafeSectionHandle section = MirroredSection.CreateSection(D, TestNames.UniqueSection());
        Assert.Throws<ArgumentOutOfRangeException>(() => MirroredSection.Create(section, dataBytes, []));
        Assert.True(section.IsClosed);   // ownership transferred even on failure
    }

    [Fact]
    public void Create_UnalignedCandidate_Throws()
    {
        SafeSectionHandle section = MirroredSection.CreateSection(D, TestNames.UniqueSection());
        Assert.Throws<ArgumentException>(() => MirroredSection.Create(section, D, [0x4000_0000_1000]));
        Assert.True(section.IsClosed);
    }

    [Fact]
    public void Open_SectionSmallerThanClaimed_ThrowsLayout()
    {
        string name = TestNames.UniqueSection();
        SafeSectionHandle small = MirroredSection.CreateSection(65536, name);
        try
        {
            SafeSectionHandle section = MirroredSection.OpenSection(name);
            var ex = Assert.Throws<RingBufferLayoutException>(() => MirroredSection.Open(section, 4 * 65536, []));
            Assert.Equal(5, ex.NativeErrorCode);
            Assert.True(section.IsClosed);
        }
        finally
        {
            small.Dispose();
        }
    }

    [Fact]
    public void Peek_On4KiBSection_ThrowsLayout()
    {
        // a raw 4 KiB section: the 64 KiB header peek cannot be mapped
        string name = TestNames.UniqueSection();
        SafeSectionHandle raw = Kernel.CreateFileMapping(Kernel.INVALID_HANDLE_VALUE, null, Kernel.PAGE_READWRITE, 0, 4096, name);
        Assert.False(raw.IsInvalid);
        try
        {
            var ex = Assert.Throws<RingBufferLayoutException>(() => MirroredSection.MapHeaderPeek(raw));
            Assert.Equal(5, ex.NativeErrorCode);
        }
        finally
        {
            raw.Dispose();
        }
    }

    [Fact]
    public void Peek_MapsAndUnmapsHeader()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection creator = CreateRegion(name);
        creator.Header[123] = 0x5C;
        SafeSectionHandle section = MirroredSection.OpenSection(name);
        try
        {
            byte* peek = MirroredSection.MapHeaderPeek(section);
            Assert.Equal(0x5C, peek[123]);
            ulong addr = (ulong)peek;
            MirroredSection.UnmapPeek(peek);
            Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(addr, out _));
        }
        finally
        {
            section.Dispose();
        }
    }

    // ---------------------------------------------------------------- teardown

    [Fact]
    public void Dispose_ReleasesVA_AndName()
    {
        string name = TestNames.UniqueSection();
        MirroredSection r = CreateRegion(name);
        ulong b = r.BaseAddress;
        Assert.Equal(TestKernel.MEM_COMMIT, TestKernel.QueryState(b, out _));
        Assert.Equal(TestKernel.MEM_MAPPED, TestKernel.QueryType(b));
        Assert.Equal(TestKernel.MEM_MAPPED, TestKernel.QueryType(b + 65536 + (ulong)D));

        r.Dispose();
        Assert.True(r.IsClosed);
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(b, out nuint free));
        Assert.True(free >= (nuint)(65536 + 2 * D), $"free region {free} smaller than the placeholder");
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(b + 65536, out _));
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(b + 65536 + (ulong)D, out _));
        Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(name));
        r.Dispose();   // idempotent
    }

    [Fact]
    public void Dispose_NameSurvivesWhileAnotherHandleIsOpen()
    {
        string name = TestNames.UniqueSection();
        MirroredSection r = CreateRegion(name);
        r.Data[5] = 55;
        SafeSectionHandle keeper = MirroredSection.OpenSection(name);
        r.Dispose();
        try
        {
            SafeSectionHandle again = MirroredSection.OpenSection(name);    // still exists: the keeper holds it
            using MirroredSection r2 = MirroredSection.Open(again, D, []);
            Assert.Equal(55, r2.Mirror[5]);                                    // memory survived too
        }
        finally
        {
            keeper.Dispose();
        }

        Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(name));
    }

    [Fact]
    public void MapUnmap_1000Times_NoLeak()
    {
        string name = TestNames.UniqueSection();
        ulong[] hints = Hints(D, 777);
        using Process self = Process.GetCurrentProcess();

        // warm up (JIT, first-time allocations)
        for (int i = 0; i < 10; i++)
        {
            using MirroredSection r = CreateRegion(name, D, hints);
        }

        self.Refresh();
        long vmBefore = self.VirtualMemorySize64;
        int handlesBefore = self.HandleCount;

        for (int i = 0; i < 1000; i++)
        {
            MirroredSection r = CreateRegion(name, D, hints);       // same name every time: fails with 183 if the previous one leaked
            Assert.True(r.AtRequestedAddress);                       // same address every time: fails if the VA leaked
            r.Data[D - 1] = (byte)i;
            Assert.Equal((byte)i, r.Mirror[D - 1]);
            ulong b = r.BaseAddress;
            r.Dispose();
            Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(b, out _));
        }

        self.Refresh();
        long vmAfter = self.VirtualMemorySize64;
        int handlesAfter = self.HandleCount;
        Assert.True(vmAfter - vmBefore < 16L << 20, $"virtual size grew by {vmAfter - vmBefore} bytes");
        Assert.True(handlesAfter - handlesBefore < 50, $"handle count grew by {handlesAfter - handlesBefore}");
        Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(name));
    }

    [Fact]
    public void Finalizer_Unmaps()
    {
        string name = TestNames.UniqueSection();
        ulong b = CreateAndDrop(name);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(TestKernel.MEM_FREE, TestKernel.QueryState(b, out _));
        Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(name));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong CreateAndDrop(string name)
    {
        MirroredSection r = CreateRegion(name);
        r.Data[0] = 1;
        return r.BaseAddress;
    }

    [Fact]
    public void Platform_Guards()
    {
        Kernel.EnsurePlatform();
        Assert.Equal(65536u, Kernel.AllocationGranularity);
        Assert.Equal(4096u, Kernel.PageSize);
    }
}
