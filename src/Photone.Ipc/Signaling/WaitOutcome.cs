namespace Photone.Ipc.Signaling;

/// <summary>Result of a blocking wait issued through a <see cref="SignalBackend"/>.</summary>
internal enum WaitOutcome
{
    /// <summary>Woken by the peer (or a stale/spurious wake; the caller re-checks its condition).</summary>
    Signaled = 0,

    /// <summary>The time slice elapsed.</summary>
    Timeout = 1,

    /// <summary>One of the process handles in the wait set became signaled (the process exited).</summary>
    ProcessExited = 2,

    /// <summary>The wait itself failed (see the last P/Invoke error).</summary>
    Failed = 3,
}
