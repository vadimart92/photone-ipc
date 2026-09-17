namespace Photone.Ipc;

/// <summary>Base exception for every failure raised by photone-ipc.</summary>
public class PhotoneIpcException : Exception
{
    /// <summary>Creates an exception without a native error code.</summary>
    public PhotoneIpcException()
    {
    }

    /// <summary>Creates an exception with a message and no native error code.</summary>
    public PhotoneIpcException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PhotoneIpcException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates an exception carrying a Win32 error code.</summary>
    public PhotoneIpcException(string message, int nativeErrorCode) : base(message)
    {
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>The Win32 error code (<c>GetLastError</c>) that caused the failure, or 0 if the failure was not a Win32 error.</summary>
    public int NativeErrorCode { get; }
}

/// <summary>The shared-memory layout does not match this library (magic/version/element size/type/size mismatch, unknown backend, mirror mismatch).</summary>
public sealed class RingBufferLayoutException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public RingBufferLayoutException()
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public RingBufferLayoutException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public RingBufferLayoutException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, int)"/>
    public RingBufferLayoutException(string message, int nativeErrorCode) : base(message, nativeErrorCode)
    {
    }
}

/// <summary><c>Open</c> failed because no section with the given name exists (<c>ERROR_FILE_NOT_FOUND</c>).</summary>
public sealed class RingBufferNotFoundException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public RingBufferNotFoundException()
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public RingBufferNotFoundException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public RingBufferNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, int)"/>
    public RingBufferNotFoundException(string message, int nativeErrorCode) : base(message, nativeErrorCode)
    {
    }
}

/// <summary><c>Create</c> failed because a section with the given name already exists (<c>ERROR_ALREADY_EXISTS</c>).</summary>
public sealed class RingBufferAlreadyExistsException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public RingBufferAlreadyExistsException()
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public RingBufferAlreadyExistsException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public RingBufferAlreadyExistsException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, int)"/>
    public RingBufferAlreadyExistsException(string message, int nativeErrorCode) : base(message, nativeErrorCode)
    {
    }
}

/// <summary>An opener gave up waiting for the creator to finish initialising the buffer, or the creator died mid-initialisation.</summary>
public sealed class RingBufferInitializationException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public RingBufferInitializationException()
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public RingBufferInitializationException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public RingBufferInitializationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, int)"/>
    public RingBufferInitializationException(string message, int nativeErrorCode) : base(message, nativeErrorCode)
    {
    }
}

/// <summary>All reader slots are active (after a dead-slot sweep).</summary>
public sealed class TooManyReadersException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public TooManyReadersException()
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public TooManyReadersException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public TooManyReadersException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The reader's slot was taken away (its process was believed dead or its claim was reclaimed).</summary>
public sealed class ReaderEvictedException : PhotoneIpcException
{
    /// <inheritdoc cref="PhotoneIpcException()"/>
    public ReaderEvictedException()
    {
        SlotIndex = -1;
    }

    /// <inheritdoc cref="PhotoneIpcException(string)"/>
    public ReaderEvictedException(string message) : base(message)
    {
        SlotIndex = -1;
    }

    /// <inheritdoc cref="PhotoneIpcException(string, Exception)"/>
    public ReaderEvictedException(string message, Exception innerException) : base(message, innerException)
    {
        SlotIndex = -1;
    }

    /// <summary>Creates the exception for a specific slot.</summary>
    public ReaderEvictedException(int slotIndex, string message) : base(message)
    {
        SlotIndex = slotIndex;
    }

    /// <summary>The slot index (0..31) that was evicted, or -1 if unknown.</summary>
    public int SlotIndex { get; }
}
