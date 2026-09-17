namespace Photone.Ipc;

/// <summary>
/// A readable window of exactly <see cref="Length"/> elements returned by <see cref="RingReader{T}.TryRead"/>. Contiguous even when it wraps
/// around the end of the ring (double mapping). Valid until <see cref="RingReader{T}.Advance"/> moves the reader past it.
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
public readonly ref struct Chunk<T> where T : unmanaged
{
    private readonly ReadOnlySpan<T> _span;
    private readonly long _cursor;

    internal Chunk(ReadOnlySpan<T> span, long cursor)
    {
        _span = span;
        _cursor = cursor;
    }

    /// <summary>The readable elements.</summary>
    public ReadOnlySpan<T> Span => _span;

    /// <summary>Number of elements.</summary>
    public int Length => _span.Length;

    /// <summary>Absolute element index of <c>Span[0]</c>.</summary>
    public long Cursor => _cursor;
}
