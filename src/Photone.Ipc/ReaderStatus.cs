namespace Photone.Ipc;

/// <summary>State of a <see cref="RingReader{T}"/>.</summary>
public enum ReaderStatus
{
    /// <summary>The writer is alive and the reader owns its slot.</summary>
    Active = 0,

    /// <summary>The writer disposed its buffer cleanly; everything up to the final write cursor can still be drained.</summary>
    WriterClosed = 1,

    /// <summary>The writer process died; everything it published before dying can still be drained.</summary>
    WriterTerminated = 2,

    /// <summary>The reader's slot was taken away (its process was believed dead or its claim was reclaimed).</summary>
    Evicted = 3,

    /// <summary>The reader has been disposed.</summary>
    Disposed = 4,
}
