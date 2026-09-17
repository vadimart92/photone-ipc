namespace Photone.Ipc;

/// <summary>Whether a buffer carries stream tags, and who can read them (<see cref="RingBufferOptions.Tags"/>; DESIGN §16).</summary>
public enum TagMode
{
    /// <summary>No tags (the default): <c>AddTag</c> throws, and reading and writing elements pays nothing for tags.</summary>
    None = 0,

    /// <summary>
    /// Tags stay objects in the writer's process. Readers created by the writer's own <see cref="RingBuffer{T}"/> receive the instances that were added:
    /// nothing is serialized, and the tags live in ordinary managed memory for as long as a reader has not read past them. Readers in other processes,
    /// or of the same buffer opened by name, see no tags.
    /// </summary>
    InProcess = 1,

    /// <summary>
    /// Tags are also serialized with <see cref="RingBufferOptions.TagSerializer"/> into the buffer's shared memory, for readers in every process. That memory
    /// is committed as the tags need it and reused once every reader has read past them. Readers created by the writer's own
    /// <see cref="RingBuffer{T}"/> still receive the original instances, without deserializing.
    /// </summary>
    CrossProcess = 2,
}
