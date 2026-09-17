using System.Runtime.Versioning;

// Windows 10 1803 (build 17134) is what VirtualAlloc2 / MapViewOfFile3 need and what Kernel.EnsurePlatform enforces at run time;
// saying so here is also what satisfies CA1416 against the [SupportedOSPlatform] the CsWin32-generated imports carry.
[assembly: SupportedOSPlatform("windows10.0.17134")]
