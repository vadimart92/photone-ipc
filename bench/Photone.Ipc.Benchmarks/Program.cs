using BenchmarkDotNet.Running;

namespace Photone.Ipc.Benchmarks;

/// <summary>
/// Entry point.
/// <list type="bullet">
/// <item><c>--quick</c> (alias <c>--cross</c>): the custom Stopwatch harness (<see cref="QuickHarness"/>); finishes in well under a minute.
///   Options: <c>--cores a,b</c> (logical cores for the two sides; default 2,4).</item>
/// <item><c>--lifecycle</c>: create / open / release costs without and with a <see cref="RingBufferPool"/> (<see cref="Lifecycle"/>).</item>
/// <item><c>--peer ...</c>: internal; the harness spawns this executable as its cross-process peer (<see cref="Peer"/>).</item>
/// <item>anything else is passed to the BenchmarkDotNet switcher (<see cref="HotPathBenchmarks"/>), e.g. <c>--filter *</c>.</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--peer")
        {
            return Peer.Run(args.AsSpan(1));
        }

        if (args.Length > 0 && (args[0] == "--quick" || args[0] == "--cross"))
        {
            return QuickHarness.Run(args.AsSpan(1));
        }

        if (args.Length > 0 && args[0] == "--compare")
        {
            return QuickHarness.RunCompare(args.AsSpan(1));
        }

        if (args.Length > 0 && args[0] == "--latency")
        {
            return PacedLatency.Run(args.AsSpan(1));
        }

        if (args.Length > 0 && args[0] == "--lifecycle")
        {
            return Lifecycle.Run(args.AsSpan(1));
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
