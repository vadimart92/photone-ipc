using System.Diagnostics;
using System.Numerics;

namespace Photone.Ipc.Internal;

/// <summary>Capacity rounding and the cross-process-stable type hash (DESIGN §8).</summary>
internal static class Capacity
{
    /// <summary>Largest supported element count exponent (capacity ≤ 2^30 keeps every span length an <see cref="int"/>).</summary>
    public const int MaxCapacityLog2 = 30;

    /// <summary>Largest supported data region (1 TiB).</summary>
    public const long MaxDataBytes = 1L << 40;

    /// <summary>
    /// Chooses the smallest power-of-two capacity ≥ <paramref name="minCapacity"/> such that <c>capacity * elementSize</c> is a multiple of 64 KiB.
    /// </summary>
    public static (long capacity, long dataBytes) Choose(long minCapacity, int elementSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(elementSize, 0);
        if (minCapacity < 1)
        {
            minCapacity = 1;
        }

        int kMin = Math.Max(0, 16 - BitOperations.TrailingZeroCount((uint)elementSize));            // C*s ≡ 0 (mod 2^16)  <=>  k + tz(s) >= 16
        int kReq = minCapacity == 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(minCapacity - 1)); // ceil(log2)
        int k = Math.Max(kMin, kReq);
        if (k > MaxCapacityLog2)
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity, "capacity would exceed 2^30 elements");
        }

        long capacity = 1L << k;
        long dataBytes = checked(capacity * elementSize);
        if (dataBytes > MaxDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity, "data region would exceed 1 TiB");
        }

        Debug.Assert(dataBytes % Layout.HeaderViewBytes == 0);
        return (capacity, dataBytes);
    }

    /// <summary>FNV-1a (32-bit) over the UTF-16 code units of <c>typeof(T).FullName</c>; deterministic across processes.</summary>
    public static uint TypeHash(Type t) => Fnv1a32(t.FullName.AsSpan());

    /// <summary>FNV-1a 32-bit hash over UTF-16 code units.</summary>
    public static uint Fnv1a32(ReadOnlySpan<char> text)
    {
        uint h = 2166136261;
        foreach (char c in text)
        {
            h ^= (byte)c;
            h *= 16777619;
            h ^= (byte)(c >> 8);
            h *= 16777619;
        }

        return h;
    }
}
