using System.Runtime.InteropServices;
using Windows.Win32;

// CS0436: this project generates its own CsWin32 types while the library's (internal, visible through InternalsVisibleTo) carry the same names;
//         the compiler prefers the ones from this project's source, which is what these helpers want.
#pragma warning disable CS0436

namespace Photone.Ipc.Benchmarks;

/// <summary>Pins the calling thread to one logical core (benchmark-only; the library itself never touches affinity).</summary>
internal static class Affinity
{

    /// <summary>Pins the current OS thread to <paramref name="core"/> (no-op for a negative core) and optionally raises its priority.</summary>
    public static void Pin(int core, bool highest)
    {
        if (core >= 0 && core < Environment.ProcessorCount)
        {
            Thread.BeginThreadAffinity();
            if (PInvoke.SetThreadAffinityMask(PInvoke.GetCurrentThread(), (nuint)1 << core) == 0)
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
