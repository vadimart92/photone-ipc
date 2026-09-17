using System.Buffers;

namespace Photone.Ipc.Internal;

/// <summary>
/// The owner that turns the double-mapped data region into <see cref="Memory{T}"/>: one per buffer, over a pointer into shared memory that never
/// moves, so every readable window is an allocation-free slice of <see cref="MemoryManager{T}.Memory"/>. Holds no resources of its own - the mapping
/// is released by <see cref="RingBuffer{T}"/> (memory kept past that point refers to memory that is no longer mapped).
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
internal sealed unsafe class MappedMemory<T> : MemoryManager<T> where T : unmanaged
{
    private readonly T* _data;
    private readonly int _length;

    /// <param name="data">The first element; <paramref name="length"/> elements from it stay mapped for the lifetime of the mapping.</param>
    /// <param name="length">Number of elements the owner exposes.</param>
    public MappedMemory(void* data, int length)
    {
        _data = (T*)data;
        _length = length;
    }

    /// <summary>The whole region; a slice of it is what <see cref="Chunk{T}.Data"/> hands out.</summary>
    public override Span<T> GetSpan() => new(_data, _length);

    /// <summary>Shared memory is already fixed: the handle carries the address, with nothing to pin and nothing to unpin.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elementIndex"/> lies outside the region.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex), elementIndex, "elementIndex must be between 0 and the length of the region.");
        }

        return new MemoryHandle(_data + elementIndex);
    }

    /// <summary>Nothing is ever pinned.</summary>
    public override void Unpin()
    {
    }

    /// <summary>The owner holds no resources; the mapping outlives it and is released by its buffer's last local reference.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}
