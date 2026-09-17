using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Photone.Ipc.Internal;

/// <summary>Native <c>SYSTEM_INFO</c> (x64 layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture;
    public ushort wReserved;
    public uint dwPageSize;
    public void* lpMinimumApplicationAddress;
    public void* lpMaximumApplicationAddress;
    public nuint dwActiveProcessorMask;
    public uint dwNumberOfProcessors;
    public uint dwProcessorType;
    public uint dwAllocationGranularity;
    public ushort wProcessorLevel;
    public ushort wProcessorRevision;
}

/// <summary>Native <c>PUBLIC_OBJECT_BASIC_INFORMATION</c> (winternl.h; 56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PUBLIC_OBJECT_BASIC_INFORMATION
{
    public uint Attributes;
    public uint GrantedAccess;
    public uint HandleCount;
    public uint PointerCount;
    public fixed uint Reserved[10];
}

/// <summary>
/// All P/Invoke declarations, Win32 constants and small error helpers used by the library.
/// <c>VirtualAlloc2</c> / <c>MapViewOfFile3</c> are exported by kernelbase.dll only (verified); everything else lives in kernel32.dll.
/// </summary>
internal static unsafe partial class Kernel
{
    private const string KernelBase = "kernelbase.dll";
    private const string Kernel32 = "kernel32.dll";

    // VirtualAlloc2 flags
    public const uint MEM_RESERVE = 0x00002000;
    public const uint MEM_RESERVE_PLACEHOLDER = 0x00040000;
    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READWRITE = 0x04;

    // MapViewOfFile3 flags (separate group: MEM_REPLACE_PLACEHOLDER shares its value with VirtualFree's MEM_DECOMMIT)
    public const uint MEM_REPLACE_PLACEHOLDER = 0x00004000;

    // VirtualFree flags
    public const uint MEM_RELEASE = 0x00008000;
    public const uint MEM_PRESERVE_PLACEHOLDER = 0x00000002;

    // sections / access
    public const uint FILE_MAP_WRITE = 0x0002;
    public const uint FILE_MAP_READ = 0x0004;
    public const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_SAME_ACCESS = 0x2;
    public const uint EVENT_MODIFY_STATE = 0x0002;

    // waits
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_ABANDONED = 0x80;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    // errors
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_ENOUGH_MEMORY = 8;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_INVALID_ADDRESS = 487;
    public const int ERROR_MAPPED_ALIGNMENT = 1132;
    public const int ERROR_COMMITMENT_LIMIT = 1455;

    public static readonly nint INVALID_HANDLE_VALUE = -1;

    /// <summary>Expected allocation granularity; every view and the data region are multiples of it.</summary>
    public const uint ExpectedAllocationGranularity = 65536;

    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* VirtualAlloc2(nint process, void* baseAddress, nuint size, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* MapViewOfFile3(SafeHandle fileMapping, nint process, void* baseAddress, ulong offset, nuint viewSize, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(void* address, nuint size, uint freeType);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* baseAddress);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle CreateFileMapping(nint file, void* securityAttributes, uint protect, uint maxSizeHigh, uint maxSizeLow, string? name);

    [LibraryImport(Kernel32, EntryPoint = "OpenFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle OpenFileMapping(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32)]
    public static partial void GetSystemInfo(SYSTEM_INFO* info);

    /// <summary>Pseudo handle (-1); never close it.</summary>
    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32)]
    public static partial ulong GetTickCount64();

    /// <summary>Create-or-open: on an existing name the call succeeds and the last error is <see cref="ERROR_ALREADY_EXISTS"/>.</summary>
    [LibraryImport(Kernel32, EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle CreateEvent(void* securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    // signal-path imports take nint on purpose: no SafeHandle AddRef/Release per call
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(nint handle);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    // ntdll (liveness slow path only): a process snapshot with creation times needs no process handle at all
    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength, out uint returnLength);

    // ntdll (pool slow path only): the system-wide number of open handles to a section decides whether a pooled section may be reused
    public const int ObjectBasicInformation = 0;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryObject(nint handle, int objectInformationClass, void* objectInformation, uint objectInformationLength, out uint returnLength);

    // ---- helpers (never on a hot path) ----

    private static uint s_allocationGranularity;
    private static uint s_pageSize;

    /// <summary>Allocation granularity reported by <c>GetSystemInfo</c>; throws if it is not 64 KiB (the layout depends on it).</summary>
    public static uint AllocationGranularity
    {
        get
        {
            if (s_allocationGranularity == 0)
            {
                QuerySystemInfo();
            }

            return s_allocationGranularity;
        }
    }

    /// <summary>Page size reported by <c>GetSystemInfo</c>.</summary>
    public static uint PageSize
    {
        get
        {
            if (s_pageSize == 0)
            {
                QuerySystemInfo();
            }

            return s_pageSize;
        }
    }

    private static void QuerySystemInfo()
    {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        if (si.dwAllocationGranularity != ExpectedAllocationGranularity)
        {
            throw new PhotoneIpcException($"Unsupported allocation granularity {si.dwAllocationGranularity}; photone-ipc requires 65536.");
        }

        if (si.dwPageSize == 0 || (si.dwPageSize & (si.dwPageSize - 1)) != 0)
        {
            throw new PhotoneIpcException($"Unsupported page size {si.dwPageSize}.");
        }

        s_pageSize = si.dwPageSize;
        s_allocationGranularity = si.dwAllocationGranularity;
    }

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> unless running as a 64-bit process on Windows 10 1803 (build 17134) or later.</summary>
    public static void EnsurePlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            throw new PlatformNotSupportedException("photone-ipc requires Windows 10 version 1803 (build 17134) or later (VirtualAlloc2 / MapViewOfFile3).");
        }

        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("photone-ipc requires a 64-bit process.");
        }
    }

    /// <summary>
    /// Number of handles open to the object behind <paramref name="handle"/> in all processes (<c>NtQueryObject(ObjectBasicInformation)</c>;
    /// a killed process's handles are gone once it has exited). <see langword="false"/> when the query fails.
    /// </summary>
    public static bool TryQueryHandleCount(SafeHandle handle, out uint handleCount)
    {
        handleCount = 0;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            PUBLIC_OBJECT_BASIC_INFORMATION info;
            int status = NtQueryObject(handle.DangerousGetHandle(), ObjectBasicInformation, &info, (uint)sizeof(PUBLIC_OBJECT_BASIC_INFORMATION), out _);
            if (status < 0)
            {
                return false;
            }

            handleCount = info.HandleCount;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Returns the last P/Invoke error (must be called immediately after a <c>SetLastError = true</c> import).</summary>
    public static int LastError() => Marshal.GetLastPInvokeError();

    /// <summary>Builds a <see cref="PhotoneIpcException"/> describing a failed Win32 call.</summary>
    public static PhotoneIpcException Fail(string api, int error, string? detail = null)
    {
        string message = detail is null
            ? $"{api} failed with Win32 error {error}: {Marshal.GetPInvokeErrorMessage(error).TrimEnd()}"
            : $"{api} failed with Win32 error {error}: {Marshal.GetPInvokeErrorMessage(error).TrimEnd()} ({detail})";
        return new PhotoneIpcException(message, error);
    }
}
