using Microsoft.Win32.SafeHandles;

namespace Photone.Ipc.Internal;

/// <summary>
/// Process liveness checks used for dead-reader eviction and dead-writer detection (DESIGN §5.7). Never on a hot path.
/// A PID is considered dead only when it provably is: no such process (<c>ERROR_INVALID_PARAMETER</c>), a different creation time
/// (PID reuse), or a signaled process handle. <c>ERROR_ACCESS_DENIED</c> means "exists but inaccessible" and counts as alive.
/// </summary>
internal static class ProcessLiveness
{
    private static long s_ownStartTime;

    /// <summary>Creation time (FILETIME) of the current process; cached.</summary>
    public static long OwnStartTime
    {
        get
        {
            long t = Volatile.Read(ref s_ownStartTime);
            if (t == 0)
            {
                if (!Kernel.GetProcessTimes(Kernel.GetCurrentProcess(), out t, out _, out _, out _))
                {
                    throw Kernel.Fail("GetProcessTimes", Kernel.LastError(), "current process");
                }

                Volatile.Write(ref s_ownStartTime, t);
            }

            return t;
        }
    }

    /// <summary>Result of <see cref="TryOpen"/>.</summary>
    public enum OpenResult
    {
        /// <summary>A handle was obtained and the creation time matches.</summary>
        Opened,

        /// <summary>The process exists but cannot be opened (<c>ERROR_ACCESS_DENIED</c>); the caller must poll with <see cref="IsAlive"/>.</summary>
        AccessDenied,

        /// <summary>The process provably no longer exists (no such PID, PID reused, or already signaled).</summary>
        Dead,
    }

    /// <summary>
    /// Opens <paramref name="pid"/> with <c>SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION</c> and validates its creation time.
    /// On <see cref="OpenResult.Opened"/> the caller owns <paramref name="handle"/>.
    /// </summary>
    public static OpenResult TryOpen(int pid, long expectedStartTime, out SafeProcessHandle? handle)
    {
        handle = null;
        if (pid <= 0)
        {
            return OpenResult.Dead;
        }

        SafeProcessHandle h = Kernel.OpenProcess(Kernel.SYNCHRONIZE | Kernel.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        int err = Kernel.LastError();
        if (h.IsInvalid)
        {
            h.Dispose();
            if (err == Kernel.ERROR_INVALID_PARAMETER)
            {
                return OpenResult.Dead;
            }

            // Exists but SYNCHRONIZE is denied. The PID may have been reused by a process we cannot open (a service, another user):
            // prove it through the creation time before treating the peer as alive forever.
            if (expectedStartTime != 0 && TryGetCreationTime(pid, out long created) && created != expectedStartTime)
            {
                return OpenResult.Dead;
            }

            return OpenResult.AccessDenied;
        }

        if (!Kernel.GetProcessTimes(h.DangerousGetHandle(), out long creation, out _, out _, out _) || (expectedStartTime != 0 && creation != expectedStartTime))
        {
            h.Dispose();
            return OpenResult.Dead;
        }

        if (Kernel.WaitForSingleObject(h.DangerousGetHandle(), 0) == Kernel.WAIT_OBJECT_0)
        {
            h.Dispose();
            return OpenResult.Dead;
        }

        handle = h;
        return OpenResult.Opened;
    }

    /// <summary>
    /// <see langword="true"/> unless the process provably died: no such PID, a different creation time (PID reused), or a signaled handle.
    /// <c>ERROR_ACCESS_DENIED</c> ⇒ alive. <paramref name="startTime"/> 0 skips the creation-time compare.
    /// </summary>
    public static bool IsAlive(int pid, long startTime)
    {
        switch (TryOpen(pid, startTime, out SafeProcessHandle? h))
        {
            case OpenResult.Opened:
                h!.Dispose();
                return true;
            case OpenResult.AccessDenied:
                return true;
            default:
                return false;
        }
    }

    /// <summary><see langword="false"/> only when <c>OpenProcess</c> fails with <c>ERROR_INVALID_PARAMETER</c> (no such PID).</summary>
    public static bool ProcessExists(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        SafeProcessHandle h = Kernel.OpenProcess(Kernel.SYNCHRONIZE | Kernel.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        int err = Kernel.LastError();
        bool invalid = h.IsInvalid;
        h.Dispose();
        return !(invalid && err == Kernel.ERROR_INVALID_PARAMETER);
    }

    /// <summary>
    /// Creation time of <paramref name="pid"/> without <c>SYNCHRONIZE</c> access: first a <c>PROCESS_QUERY_LIMITED_INFORMATION</c>-only handle,
    /// then a <c>SystemProcessInformation</c> snapshot (no handle at all). <see langword="false"/> when neither works. Slow path only.
    /// </summary>
    private static bool TryGetCreationTime(int pid, out long creation)
    {
        creation = 0;
        SafeProcessHandle h = Kernel.OpenProcess(Kernel.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        try
        {
            if (!h.IsInvalid && Kernel.GetProcessTimes(h.DangerousGetHandle(), out creation, out _, out _, out _))
            {
                return true;
            }
        }
        finally
        {
            h.Dispose();
        }

        return TryGetCreationTimeFromSnapshot(pid, out creation);
    }

    private static unsafe bool TryGetCreationTimeFromSnapshot(int pid, out long creation)
    {
        creation = 0;
        uint size = 512 * 1024;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            byte* buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(size);
            try
            {
                int status = Kernel.NtQuerySystemInformation(Kernel.SystemProcessInformation, buffer, size, out uint needed);
                if (status == Kernel.STATUS_INFO_LENGTH_MISMATCH || status == Kernel.STATUS_BUFFER_TOO_SMALL)
                {
                    size = Math.Max(needed + 64 * 1024, size * 2);
                    continue;
                }

                if (status != 0)
                {
                    return false;
                }

                // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset @0 (u32), CreateTime @32 (i64), UniqueProcessId @80 (HANDLE).
                byte* entry = buffer;
                while (true)
                {
                    if ((nint)(*(nint*)(entry + 80)) == pid)
                    {
                        creation = *(long*)(entry + 32);
                        return true;
                    }

                    uint next = *(uint*)entry;
                    if (next == 0)
                    {
                        return false;                               // not in the snapshot: the process is gone
                    }

                    entry += next;
                }
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.Free(buffer);
            }
        }

        return false;
    }
}
