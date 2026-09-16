using System.Numerics;
using Photone.Ipc.Internal;

namespace Photone.Ipc.Tests;

public sealed class CapacityTests
{
    public static TheoryData<int, long> Cases()
    {
        var data = new TheoryData<int, long>();
        foreach (int s in new[] { 1, 2, 3, 4, 8, 12, 16, 24, 64, 100 })
        {
            foreach (long req in new long[] { 0, 1, 2, 3, 1000, 1024, 1025, 65535, 65536, 65537, 1_000_000, 1 << 20, (1 << 20) + 1 })
            {
                data.Add(s, req);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Capacity_Rounding(int elementSize, long request)
    {
        (long c, long d) = Capacity.Choose(request, elementSize);
        Assert.True(BitOperations.IsPow2(c), $"capacity {c} is not a power of two");
        Assert.True(c >= Math.Max(1, request), $"capacity {c} < request {request}");
        Assert.Equal(c * elementSize, d);
        Assert.Equal(0, d % 65536);
        // minimality: half the capacity would violate one of the two constraints
        long half = c / 2;
        Assert.True(half < Math.Max(1, request) || (half * elementSize) % 65536 != 0, $"capacity {c} is not minimal");
    }

    [Fact]
    public void Capacity_Examples_FromDesign()
    {
        Assert.Equal((1L << 20, 4L << 20), Capacity.Choose(1_000_000, 4));
        Assert.Equal((65536L, 65536L), Capacity.Choose(1, 1));
        Assert.Equal((16384L, 192L * 1024), Capacity.Choose(1, 12));
        Assert.Equal((131072L, 3L << 20), Capacity.Choose(100_000, 24));
    }

    [Fact]
    public void Capacity_TooLarge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Capacity.Choose((1L << 30) + 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Capacity.Choose(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Capacity.Choose(1, -4));
    }

    [Fact]
    public void Capacity_MaxIsAccepted()
    {
        (long c, long d) = Capacity.Choose(1L << 30, 1);
        Assert.Equal(1L << 30, c);
        Assert.Equal(1L << 30, d);
    }

    [Fact]
    public void TypeHash_IsDeterministic()
    {
        uint a = Capacity.TypeHash(typeof(float));
        uint b = Capacity.Fnv1a32("System.Single");
        Assert.Equal(b, a);
        Assert.NotEqual(Capacity.TypeHash(typeof(int)), Capacity.TypeHash(typeof(uint)));
        // FNV-1a over UTF-16 code units: "a" = 0x61, 0x00
        uint h = 2166136261;
        h = (h ^ 0x61) * 16777619;
        h = (h ^ 0x00) * 16777619;
        Assert.Equal(h, Capacity.Fnv1a32("a"));
    }
}
