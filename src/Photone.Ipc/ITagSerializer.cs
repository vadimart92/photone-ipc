using System.Buffers;

namespace Photone.Ipc;

/// <summary>
/// Turns tags into bytes in the writer's process and back into objects in every reader's process (configured with
/// <see cref="RingBufferOptions.TagSerializer"/> on both sides). The library stores the type name, <see cref="ITag.Key"/>, <see cref="ITag.Offset"/>
/// and persistence next to the payload, so the payload only has to round-trip the tag object itself. Implementations must be thread-safe.
/// </summary>
public interface ITagSerializer
{
    /// <summary>The name that identifies <typeparamref name="TTag"/> to the reading side (called once per tag written; should not allocate).</summary>
    string GetTypeName<TTag>() where TTag : ITag;

    /// <summary>Writes the payload of <paramref name="tag"/>.</summary>
    void Serialize<TTag>(TTag tag, IBufferWriter<byte> destination) where TTag : ITag;

    /// <summary>
    /// Recreates a tag from its type name and payload; <see langword="null"/> when <paramref name="typeName"/> is unknown here (the reader then keeps
    /// an <see cref="UnknownTag"/>). The result's <see cref="ITag.Offset"/> and <see cref="ITag.Key"/> should equal the written tag's.
    /// </summary>
    ITag? Deserialize(string typeName, ReadOnlySpan<byte> payload);
}
