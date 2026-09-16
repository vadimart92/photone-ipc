namespace Photone.Ipc;

/// <summary>
/// A reservation of exactly <see cref="Length"/> elements at the head of the ring, obtained from <see cref="RingBuffer{T}.GetBucket"/>.
/// <see cref="Span"/> is contiguous even when the reservation wraps around the end of the ring (double mapping).
/// Call <see cref="Commit"/> exactly once to publish a prefix of the span; disposing without committing publishes nothing.
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
public ref struct Bucket<T> : IDisposable where T : unmanaged
{
    private RingBuffer<T>? _owner;
    private Span<T> _span;
    private readonly long _cursor;
    private int _committed;      // -1 while open

    internal Bucket(RingBuffer<T> owner, Span<T> span, long cursor)
    {
        _owner = owner;
        _span = span;
        _cursor = cursor;
        _committed = -1;
    }

    /// <summary>Exactly <see cref="Length"/> writable elements, contiguous through the mirror; empty after <see cref="Commit"/> or <see cref="Dispose"/>.</summary>
    public readonly Span<T> Span => _span;

    /// <summary>Number of reserved elements (0 after <see cref="Commit"/> or <see cref="Dispose"/>).</summary>
    public readonly int Length => _span.Length;

    /// <summary>Absolute element index of <c>Span[0]</c>.</summary>
    public readonly long Cursor => _cursor;

    /// <summary>
    /// Publishes the first <paramref name="count"/> elements (<c>0 &lt;= count &lt;= Length</c>) to all readers. Exactly once per bucket;
    /// the unpublished tail is dropped and the next bucket starts at <c>Cursor + count</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bucket was already committed or disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> exceeds <see cref="Length"/>.</exception>
    public void Commit(int count)
    {
        if (_committed >= 0 || _owner is null)
        {
            throw new InvalidOperationException("The bucket has already been committed or disposed.");
        }

        if ((uint)count > (uint)_span.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and the bucket length.");
        }

        _committed = count;
        RingBuffer<T> owner = _owner;
        _span = default;
        owner.EndWrite(count);
    }

    /// <summary>Without a prior <see cref="Commit"/>, publishes nothing (<c>Commit(0)</c>). Idempotent.</summary>
    public void Dispose()
    {
        if (_committed < 0 && _owner is not null)
        {
            _committed = 0;
            RingBuffer<T> owner = _owner;
            _span = default;
            owner.EndWrite(0);
        }
    }
}
