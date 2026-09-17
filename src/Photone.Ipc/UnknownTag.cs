namespace Photone.Ipc;

/// <summary>
/// A tag the reader could not turn into its original type: the reader's <see cref="ITagSerializer"/> does not know <see cref="TypeName"/>, has no
/// serializer at all, or failed on the payload (<see cref="Error"/>). It keeps its place in <see cref="Chunk{T}.Tags"/> and, if the writer declared it
/// persistent, in <see cref="RingReader{T}.ReadLastTagValues"/>.
/// </summary>
public sealed class UnknownTag : ITag
{
    internal UnknownTag(ulong offset, string key, string typeName, bool persistent, byte[] payload, Exception? error)
    {
        Offset = offset;
        Key = key;
        TypeName = typeName;
        Persistent = persistent;
        Payload = payload;
        Error = error;
    }

    /// <inheritdoc/>
    public ulong Offset { get; }

    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>The type name the writer's serializer recorded.</summary>
    public string TypeName { get; }

    /// <summary>Whether the writer's tag type was persistent.</summary>
    public bool Persistent { get; }

    /// <summary>The serialized payload as the writer produced it (UTF-8 JSON with <see cref="JsonTagSerializer"/>).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The exception the serializer threw, or <see langword="null"/> when the type was simply unknown.</summary>
    public Exception? Error { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{TypeName} '{Key}' @ {Offset} ({Payload.Length} bytes{(Error is null ? "" : ", " + Error.GetType().Name)})";
}
