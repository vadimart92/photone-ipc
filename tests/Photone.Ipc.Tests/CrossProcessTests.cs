using System.Globalization;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

[Collection("ipc")]
public sealed unsafe class CrossProcessTests
{
    private const long D = 4 * 65536;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private static MirroredSection CreateRegion(string name)
    {
        Span<ulong> tmp = stackalloc ulong[AddressHint.MaxProbes];
        int n = AddressHint.Candidates((ulong)Random.Shared.NextInt64(1, long.MaxValue), (ulong)(Layout.HeaderViewBytes + 2 * D), null, tmp);
        SafeSectionHandle section = MirroredSection.CreateSection(D, name);
        return MirroredSection.Create(section, D, tmp[..n]);
    }

    private static void WriteParentMarker(MirroredSection r)
    {
        // 0..15 across the data/mirror boundary, written through a single span starting inside Data
        var span = new Span<byte>(r.Data + D - 8, 16);
        for (int i = 0; i < 16; i++)
        {
            span[i] = (byte)i;
        }
    }

    private static void AssertChildMarker(MirroredSection r)
    {
        // the child wrote 100..115 through its mirror alias; we see it through Data (end) and Data (start)
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)(100 + i), r.Data[(D - 8 + i) % D]);
        }
    }

    /// <summary>Runs one child round trip; returns whether the child landed on the creator's address.</summary>
    private static bool RunChild(string name, MirroredSection r, bool occupy)
    {
        WriteParentMarker(r);
        string[] args = occupy
            ? ["map-region", name, D.ToString(CultureInfo.InvariantCulture), r.BaseAddress.ToString("X", CultureInfo.InvariantCulture), "--occupy"]
            : ["map-region", name, D.ToString(CultureInfo.InvariantCulture), r.BaseAddress.ToString("X", CultureInfo.InvariantCulture)];
        using ChildProcess child = ChildProcess.Start(args);
        string mapped = child.ReadLine(s_timeout);
        Assert.StartsWith("mapped ", mapped, StringComparison.Ordinal);
        string verified = child.ReadLine(s_timeout);
        Assert.Equal("verified", verified);
        AssertChildMarker(r);

        bool atCreator = mapped.Contains("atCreator=True", StringComparison.Ordinal);
        if (atCreator)
        {
            Assert.Contains($"base=0x{r.BaseAddress:X}", mapped, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain($"base=0x{r.BaseAddress:X}", mapped, StringComparison.Ordinal);
        }

        child.WriteLine("exit");
        Assert.Equal("exiting", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));
        return atCreator;
    }

    [Fact]
    public void CrossProcess_ChildMapsAndSharesWrites()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection r = CreateRegion(name);
        RunChild(name, r, occupy: false);
        // the section is still alive here and the child's writes persist after it exited
        AssertChildMarker(r);
    }

    [Fact]
    public void CrossProcess_SameBaseAddress_BestEffort()
    {
        // Hard-assert correctness on every spawn; the same-address outcome is best effort, so require it in >= 1 of 5 spawns.
        int same = 0;
        for (int attempt = 0; attempt < 5 && same == 0; attempt++)
        {
            string name = TestNames.UniqueSection();
            using MirroredSection r = CreateRegion(name);
            if (RunChild(name, r, occupy: false))
            {
                same++;
            }
        }

        Assert.True(same >= 1, "no child of 5 mapped the section at the creator's address");
    }

    [Fact]
    public void CrossProcess_FallbackAddress()
    {
        string name = TestNames.UniqueSection();
        using MirroredSection r = CreateRegion(name);
        bool atCreator = RunChild(name, r, occupy: true);    // the child reserves the creator's range first
        Assert.False(atCreator);
    }

    [Fact]
    public void CrossProcess_NameLifetime_ChildHoldsMapping()
    {
        string name = TestNames.UniqueSection();
        MirroredSection r = CreateRegion(name);
        WriteParentMarker(r);
        using ChildProcess child = ChildProcess.Start("map-region", name, D.ToString(CultureInfo.InvariantCulture), "0");
        Assert.StartsWith("mapped ", child.ReadLine(s_timeout), StringComparison.Ordinal);
        Assert.Equal("verified", child.ReadLine(s_timeout));

        r.Dispose();                                             // creator gone; the child's mapping keeps the section alive
        SafeSectionHandle again = MirroredSection.OpenSection(name);
        using (MirroredSection r2 = MirroredSection.Open(again, D, []))
        {
            AssertChildMarker(r2);
        }

        child.WriteLine("exit");
        Assert.Equal("exiting", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));
        Assert.Throws<RingBufferNotFoundException>(() => MirroredSection.OpenSection(name));
    }

    [Fact]
    public void CrossProcess_MissingSection_ChildReportsNotFound()
    {
        using ChildProcess child = ChildProcess.Start("map-region", TestNames.UniqueSection(), D.ToString(CultureInfo.InvariantCulture), "0");
        string line = child.ReadLine(s_timeout);
        Assert.StartsWith("error RingBufferNotFoundException", line, StringComparison.Ordinal);
        Assert.Equal(1, child.WaitForExit(s_timeout));
    }
}
