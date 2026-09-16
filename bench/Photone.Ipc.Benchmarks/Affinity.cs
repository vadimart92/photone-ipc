using System.Runtime.InteropServices;

namespace Photone.Ipc.Benchmarks;

/// <summary>Pins the calling thread to one logical core (benchmark-only; the library itself never touches affinity).</summary>
internal static partial class Affinity
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    /// <summary>Pins the current OS thread to <paramref name="core"/> (no-op for a negative core) and optionally raises its priority.</summary>
    public static void Pin(int core, bool highest)
    {
        if (core >= 0 && core < Environment.ProcessorCount)
        {
            Thread.BeginThreadAffinity();
            if (SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << core) == 0)
            {
                Console.Error.WriteLine($"warning: SetThreadAffinityMask({core}) failed with {Marshal.GetLastPInvokeError()}");
            }
        }

        if (highest)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
    }
}
