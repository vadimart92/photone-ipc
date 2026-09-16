namespace Photone.Ipc;

/// <summary>Options for <see cref="RingBuffer{T}.Create"/> and <see cref="RingBuffer{T}.Open(string, RingBufferOptions?)"/>.</summary>
public sealed class RingBufferOptions
{
    /// <summary>Writer spin budget in <see cref="RingBuffer{T}.GetBucket"/> before a kernel wait. Zero = block immediately; negative = spin forever. Default 20 µs.</summary>
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);

    /// <summary>Slice used while the writer is blocked (liveness backstop for readers whose process handle is unavailable). Default 10 ms.</summary>
    public TimeSpan LivenessCheckInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long <c>Open</c> waits for the creator to finish initialising (creator liveness is checked meanwhile). Default 5 s.</summary>
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Exact 64 KiB-aligned base address to request (creator only). <see langword="null"/> = hashed hint in the quiet window; 0 = no hint.</summary>
    public ulong? PreferredBaseAddress { get; init; }

    /// <summary>Touch every data page once at creation (removes first-touch page faults from the hot path). Default <see langword="true"/>.</summary>
    public bool PreFault { get; init; } = true;

    internal static readonly RingBufferOptions Default = new();
}

/// <summary>Options for <see cref="RingBuffer{T}.CreateReader"/>.</summary>
public sealed class ReaderOptions
{
    /// <summary>Spin budget of <see cref="RingReader{T}.WaitSync(int)"/> before a kernel wait. Zero = block immediately; negative = spin forever. Default 20 µs.</summary>
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);

    /// <summary>Spin budget of <see cref="RingReader{T}.Wait(int, CancellationToken)"/> on the caller's thread before suspending. Default 5 µs.</summary>
    public TimeSpan AsyncSpinTime { get; init; } = TimeSpan.FromMicroseconds(5);

    internal static readonly ReaderOptions Default = new();
}
