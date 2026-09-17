using System.Numerics;

namespace Photone.Ipc.Internal;

/// <summary>
/// Preferred-base-address probes (DESIGN §3.6). Identical addresses in peer processes are an optimisation only;
/// correctness never depends on them (shared memory holds cursors and offsets, never pointers).
/// </summary>
internal static class AddressHint
{
    /// <summary>Start of the quiet 16 TiB window <c>[0x4000_0000_0000, 0x5000_0000_0000)</c>, far from heaps, DLLs, stacks and GC segments.</summary>
    public const ulong WindowStart = 0x4000_0000_0000;

    /// <summary>Size of the quiet window.</summary>
    public const ulong WindowBytes = 0x1000_0000_0000;

    /// <summary>Number of hashed probes tried before falling back to a system-chosen address.</summary>
    public const int MaxProbes = 4;

    /// <summary>Minimum stride between probe slots.</summary>
    public const ulong MinStride = 2UL << 20;

    private const ulong GoldenGamma = 0x9E3779B97F4A7C15;

    /// <summary>
    /// Fills <paramref name="dest"/> with the base addresses to try, in order, and returns how many were written.
    /// Zero entries means "let the system choose" only. The caller always tries a <see langword="null"/> base last.
    /// </summary>
    /// <param name="instanceId">Random per-buffer id; the probes are a deterministic function of it.</param>
    /// <param name="reservationBytes">Size of the placeholder (<c>G + 2D</c>).</param>
    /// <param name="preferred">Explicit request: <see langword="null"/> = hashed hints; 0 = no hint; any other value must be 64 KiB aligned.</param>
    /// <param name="dest">Receives the candidates; must hold at least <see cref="MaxProbes"/> entries.</param>
    public static int Candidates(ulong instanceId, ulong reservationBytes, ulong? preferred, Span<ulong> dest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dest.Length, MaxProbes);
        if (preferred is ulong p)
        {
            if (p == 0)
            {
                return 0;
            }

            if (p % Kernel.ExpectedAllocationGranularity != 0)
            {
                throw new ArgumentException("PreferredBaseAddress must be a multiple of 64 KiB.", nameof(preferred));
            }

            dest[0] = p;
            return 1;
        }

        ulong stride = Math.Max(MinStride, BitOperations.RoundUpToPowerOf2(reservationBytes));
        ulong slots = WindowBytes / stride;
        if (slots == 0)
        {
            return 0;
        }

        ulong h = SplitMix64(instanceId);
        for (int probe = 0; probe < MaxProbes; probe++)
        {
            dest[probe] = WindowStart + ((h + (ulong)probe * GoldenGamma) % slots) * stride;
        }

        return MaxProbes;
    }

    /// <summary>SplitMix64 finaliser.</summary>
    public static ulong SplitMix64(ulong x)
    {
        x += GoldenGamma;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }
}
