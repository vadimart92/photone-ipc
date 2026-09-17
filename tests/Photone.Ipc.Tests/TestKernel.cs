using System.Runtime.InteropServices;

namespace Photone.Ipc.Tests;

/// <summary>x64 <c>MEMORY_BASIC_INFORMATION</c> (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MEMORY_BASIC_INFORMATION
{
    public void* BaseAddress;
    public void* AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

/// <summary>Test-only native imports (the library itself never imports these).</summary>
internal static unsafe partial class TestKernel
{
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_FREE = 0x10000;
    public const uint MEM_PRIVATE = 0x20000;
    public const uint MEM_MAPPED = 0x40000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READWRITE = 0x04;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQuery(void* address, MEMORY_BASIC_INFORMATION* buffer, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(void* address, nuint size, uint freeType);

    /// <summary>Returns the <c>State</c> of the region containing <paramref name="address"/> (<c>MEM_FREE</c> / <c>MEM_RESERVE</c> / <c>MEM_COMMIT</c>).</summary>
    public static uint QueryState(ulong address, out nuint regionSize)
    {
        MEMORY_BASIC_INFORMATION mbi;
        nuint n = VirtualQuery((void*)address, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION));
        if (n == 0)
        {
            throw new InvalidOperationException($"VirtualQuery failed with {Marshal.GetLastPInvokeError()}");
        }

        regionSize = mbi.RegionSize;
        return mbi.State;
    }

    public static uint QueryType(ulong address)
    {
        MEMORY_BASIC_INFORMATION mbi;
        nuint n = VirtualQuery((void*)address, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION));
        if (n == 0)
        {
            throw new InvalidOperationException($"VirtualQuery failed with {Marshal.GetLastPInvokeError()}");
        }

        return mbi.Type;
    }
}

/// <summary>Unique object names per test.</summary>
internal static class TestNames
{
    public static string Unique() => $"photone-test-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public static string UniqueSection() => "Local\\photone." + Unique();
}
