namespace Photone.Ipc;

/// <summary>
/// A stream tag: metadata attached to one element of a <see cref="RingBuffer{T}"/> (a burst start, a timestamp, a change of sample rate).
/// The writer attaches tags to the elements of a bucket with <see cref="Bucket{T}.AddTag{TTag}"/>; every reader sees them in
/// <see cref="Chunk{T}.Tags"/> when it reads the element they belong to. Tags cross processes through the buffer's <see cref="ITagSerializer"/>
/// (JSON by default), so a tag type must be serializable by it, including <see cref="Offset"/>.
/// </summary>
public interface ITag
{
    /// <summary>
    /// Persistent tags describe state that stays in effect until the next tag with the same <see cref="Key"/> (a sample rate, a stream format):
    /// <see cref="RingReader{T}.ReadLastTagValues"/> returns the last one of each key before the reader's position, also to a reader that joins long
    /// after the tag was written. Default <see langword="false"/> (an event that concerns only its element).
    /// </summary>
    static virtual bool IsPersistent => false;

    /// <summary>Absolute element index the tag is attached to (the same space as <see cref="Chunk{T}.StartOffset"/> and <see cref="Bucket{T}.StartOffset"/>).</summary>
    ulong Offset { get; }

    /// <summary>The tag's name; for persistent tags also the identity of the state it sets.</summary>
    string Key { get; }
}
