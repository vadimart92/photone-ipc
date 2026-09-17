namespace Photone.Ipc.Internal;

/// <summary>
/// The views of the tag reserve that one <see cref="RingBuffer{T}"/> instance has mapped (DESIGN §16.2): one per ring size class in use and views of the
/// persistent-tag table, each mapped the first time somebody needs it. Shared by the buffer's writer and by every reader it created, so a view stays mapped
/// until the buffer releases its native resources, which happens only after the last of those readers is gone: a view is never unmapped under a reader.
/// Mapping is serialized by a lock; looking up a mapped ring is a plain load.
/// </summary>
internal sealed unsafe class TagViews : IDisposable
{
    private readonly SafeSectionHandle _section;
    private readonly long _reserveOffset;
    private readonly bool _writable;
    private readonly SectionView?[] _rings = new SectionView?[TagFormat.RingClasses];
    private readonly List<SectionView> _tables = [];                // every table view mapped so far: a joiner may still be copying from an older one
    private readonly Lock _gate = new();
    private SectionView? _table;                                    // the largest table view
    private bool _disposed;

    /// <param name="section">The buffer's section (owned by its mapping, not by this object).</param>
    /// <param name="dataBytes">The data region size: the tag reserve starts at section offset 64 KiB + <paramref name="dataBytes"/>.</param>
    /// <param name="writable">The writer maps read/write views and commits through them; readers map read-only views.</param>
    public TagViews(SafeSectionHandle section, long dataBytes, bool writable)
    {
        _section = section;
        _reserveOffset = Layout.DataOffset + dataBytes;
        _writable = writable;
    }

    /// <summary>Ring <paramref name="ring"/> (0 ≤ ring &lt; <see cref="TagFormat.RingClasses"/>), mapped on first use.</summary>
    /// <exception cref="RingBufferLayoutException">The section is smaller than the tag reserve.</exception>
    public SectionView Ring(int ring)
    {
        SectionView? view = Volatile.Read(ref _rings[ring]);
        return view ?? MapRing(ring);
    }

    /// <summary>Rings mapped so far (tests).</summary>
    public int MappedRings
    {
        get
        {
            int n = 0;
            foreach (SectionView? view in _rings)
            {
                n += view is null ? 0 : 1;
            }

            return n;
        }
    }

    private SectionView MapRing(int ring)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SectionView? view = _rings[ring];
            if (view is null)
            {
                view = SectionView.Map(_section, _reserveOffset + TagFormat.RingOffset(ring), TagFormat.RingBytes(ring), _writable);
                Volatile.Write(ref _rings[ring], view);
            }

            return view;
        }
    }

    /// <summary>A view of the table region covering at least its first <paramref name="bytes"/> bytes (at least 64 KiB, doubling), mapped on demand.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> exceeds the table region.</exception>
    public SectionView Table(long bytes)
    {
        if (bytes < 0 || bytes > TagFormat.TableReserveBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"The persistent-tag table region holds {TagFormat.TableReserveBytes} bytes.");
        }

        SectionView? view = Volatile.Read(ref _table);
        if (view is not null && view.Bytes >= bytes)
        {
            return view;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            view = _table;
            if (view is null || view.Bytes < bytes)
            {
                long size = Math.Max(TagFormat.AlignView(Math.Max(bytes, 1)), Math.Min(TagFormat.TableReserveBytes, 2 * (view?.Bytes ?? 0)));
                view = SectionView.Map(_section, _reserveOffset, size, _writable);
                _tables.Add(view);
                Volatile.Write(ref _table, view);
            }

            return view;
        }
    }

    /// <summary>Unmaps every view. Only once nothing can use them any more (the owning buffer releases its native resources).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = 0; i < _rings.Length; i++)
            {
                _rings[i]?.Dispose();
                _rings[i] = null;
            }

            foreach (SectionView view in _tables)
            {
                view.Dispose();
            }

            _tables.Clear();
            _table = null;
        }
    }
}
